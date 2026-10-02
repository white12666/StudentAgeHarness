using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StudentAgeHarness.Diagnostics;
using StudentAgeHarness.Imaging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StudentAgeHarness.Engine
{
    /// <summary>
    /// 无人值守的场景驱动器。所有状态都放在静态字段里：游戏可能销毁早期创建的对象，
    /// 驱动器被销毁后会在下一次场景加载或适配层的钩子里重建，并从当前步骤继续。
    /// </summary>
    internal static class HarnessRuntime
    {
        private const string RunnerName = "StudentAgeHarness.Runner";
        private const float PollIntervalSec = 0.25f;
        private const float RecoveryTimeoutSec = 45f;

        internal static RunSettings Settings { get; private set; }
        internal static HarnessReport Report { get; private set; }
        internal static IHarnessHostAdapter Adapter { get; private set; }
        internal static HarnessFixtures Fixtures { get; private set; }
        internal static HarnessInput Input { get; private set; }
        internal static PathRedactor Redactor { get; private set; } = PathRedactor.None;
        internal static bool IsFinalized => finalized;

        private static readonly List<IHarnessExtension> extensions = new List<IHarnessExtension>();
        private static List<ScenarioEntry> selected = new List<ScenarioEntry>();
        private static HashSet<ScenarioEntry> explicitlySelected = new HashSet<ScenarioEntry>();
        private static HarnessRunContext runContext;
        private static int scenarioIndex;
        private static List<HarnessStep> steps;
        private static int currentIndex;
        private static bool abortRemaining;
        private static bool abortAll;
        private static bool scenarioTimedOut;
        private static HarnessScenarioRecord scenarioRecord;
        private static HarnessContext scenarioContext;
        private static float scenarioBeginRealtime;
        private static float scenarioDeadline = float.PositiveInfinity;
        private static bool startupDone;
        private static bool started;
        private static bool finalized;
        private static int screenshotCounter;
        private static int dumpCounter;
        private static float startRealtime = -1f;
        private static HarnessRunner runner;
        private static bool sceneHookInstalled;

        internal static string CurrentScenarioName => scenarioRecord?.Name;

        internal static string CurrentStepName
        {
            get
            {
                List<HarnessStep> current = steps;
                int index = currentIndex;
                return current != null && index >= 0 && index < current.Count ? current[index].Name : null;
            }
        }

        internal static HarnessStepRecord CurrentStepRecord { get; private set; }

        /// <summary>第一步：建报告、装异常监听。插件 Awake 里尽早调用。</summary>
        internal static void Initialize(RunSettings settings, IHarnessLogSink sink, string gameRoot, string localLow)
        {
            if (Report != null) return;
            HarnessLog.SetSink(sink);
            Settings = settings;
            startRealtime = Time.realtimeSinceStartup;
            Redactor = settings.RedactPaths
                ? PathRedactor.Create(settings.RunDirectory, gameRoot, localLow,
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile))
                : PathRedactor.None;
            Report = new HarnessReport(settings)
            {
                StartedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            Directory.CreateDirectory(settings.ScreenshotDirectory);
            UnityLogMonitor.Install();
        }

        /// <summary>第二步：接上适配层、发现场景，开始驱动。</summary>
        internal static void Start(IHarnessHostAdapter adapter, IList<ScenarioSource> sources)
        {
            if (started || Report == null) return;
            started = true;
            Adapter = adapter;
            UnityLogMonitor.SetNoiseFilter(adapter.IsKnownLogNoise);
            Fixtures = new HarnessFixtures(Settings.RunDirectory, Settings.RunId, adapter.Game?.LocalModsDirectory);
            Input = new HarnessInput(adapter.InputBinding ?? NullInputBinding.Instance);
            runContext = new HarnessRunContext();

            var packs = new JArray();
            List<ScenarioEntry> all = ScenarioCatalog.Discover(sources, Report, extensions, packs);
            Report.Environment["packs"] = packs;
            selected = ScenarioCatalog.Select(all, Settings.Scenarios, Report, out explicitlySelected);
            Report.SelectedScenarioNames.AddRange(selected.Select(item => item.Name));
            HarnessLog.Info("发现 " + all.Count + " 个场景，本次运行：" +
                (selected.Count == 0 ? "(无)" : string.Join(", ", Report.SelectedScenarioNames.ToArray())));

            if (!sceneHookInstalled)
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
                sceneHookInstalled = true;
            }
            Ensure();
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Ensure();
        }

        /// <summary>确保驱动器存在并在运行（游戏销毁它之后由钩子调用）。</summary>
        internal static void Ensure()
        {
            if (!started || finalized) return;
            if (runner == null)
            {
                var host = new GameObject(RunnerName) { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(host);
                runner = host.AddComponent<HarnessRunner>();
            }
            runner.StartIfNeeded();
        }

        internal static void TickAdapter()
        {
            Adapter?.Tick();
        }

        // ======================== 主循环 ========================

        internal static IEnumerator Drive()
        {
            if (!startupDone)
            {
                yield return WaitForStartup();
                if (finalized) yield break;
            }

            if (selected.Count == 0)
            {
                Report.AddFinding(HarnessSeverity.Error, "harness", "no-scenarios-selected",
                    "没有可运行的场景。请检查配置里的 scenarios 和 packs。");
                FinalizeAndQuit(null);
                yield break;
            }

            while (scenarioIndex < selected.Count && !finalized)
            {
                ScenarioEntry entry = selected[scenarioIndex];
                if (steps == null)
                {
                    if (!PrepareScenario(entry))
                    {
                        CloseScenario();
                        scenarioIndex++;
                        continue;
                    }
                }
                else if (scenarioRecord == null)
                {
                    // 驱动器重建后续跑：按名字复用同一条场景记录。
                    scenarioRecord = Report.BeginScenario(entry.Name, entry.Attribute.Description, entry.Pack,
                        entry.Tags);
                }

                while (currentIndex < steps.Count && !finalized)
                {
                    yield return ExecuteStep(steps[currentIndex]);
                    currentIndex++;
                }
                if (finalized) yield break;

                bool anyFailed = scenarioRecord.Steps.Any(step => step.Status == "failed" || step.Status == "skipped");
                scenarioRecord.Status = anyFailed ? "failed" : "passed";
                scenarioRecord.DurationSec = Time.realtimeSinceStartup - scenarioBeginRealtime;
                if (anyFailed && scenarioRecord.Error == null)
                    scenarioRecord.Error = scenarioRecord.Steps.FirstOrDefault(step => step.Status == "failed")?.Error;
                HarnessLog.Info("场景结束：" + entry.Name + " → " + scenarioRecord.Status + "（" +
                    scenarioRecord.DurationSec.ToString("F1", CultureInfo.InvariantCulture) + "s）");
                CloseScenario();
                scenarioIndex++;
            }

            FinalizeAndQuit(null);
        }

        private static IEnumerator WaitForStartup()
        {
            HarnessLog.Info("等待游戏进入主菜单（最多 " + Settings.StartupTimeoutSec + " 秒）……");
            float deadline = startRealtime + Settings.StartupTimeoutSec;
            while (!SafeEval(Adapter.IsStartupReady, out _))
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    yield return Capture("startup_timeout", null);
                    Report.AddFinding(HarnessSeverity.Error, "harness", "game-startup-timeout",
                        "游戏在 " + Settings.StartupTimeoutSec + " 秒内没有进入主菜单。可见文字：" +
                        HarnessUi.Instance.VisibleTextSummary(30));
                    FinalizeAndQuit("startup-timeout");
                    yield break;
                }
                yield return new WaitForSecondsRealtime(PollIntervalSec);
            }

            startupDone = true;
            HarnessLog.Info("游戏已就绪（" +
                (Time.realtimeSinceStartup - startRealtime).ToString("F1", CultureInfo.InvariantCulture) + "s）。");
            try { Adapter.OnStartupReady(Report); }
            catch (Exception ex) { HarnessFailure("adapter-startup-failed", "适配层启动检查出错", ex); }
            foreach (IHarnessExtension extension in extensions)
            {
                try { extension.OnRunStart(runContext); }
                catch (Exception ex) { HarnessFailure("extension-start-failed", "扩展 " + extension.GetType().FullName + " 的 OnRunStart 出错", ex); }
            }
        }

        private static bool PrepareScenario(ScenarioEntry entry)
        {
            scenarioRecord = Report.BeginScenario(entry.Name, entry.Attribute.Description, entry.Pack, entry.Tags);
            scenarioBeginRealtime = Time.realtimeSinceStartup;
            scenarioTimedOut = false;

            if (abortAll)
            {
                Skip(entry, "前一个场景结束后没能回到主菜单，后面的场景都没有运行。", HarnessSeverity.Info);
                return false;
            }

            string requirement = CheckRequirements(entry);
            if (requirement != null)
            {
                // 被直接点名却跑不了是配置问题；通过 * 或标签带进来的只留档。
                Skip(entry, requirement, explicitlySelected.Contains(entry) ? HarnessSeverity.Error : HarnessSeverity.Info);
                return false;
            }

            List<HarnessStep> built;
            try
            {
                scenarioContext = new HarnessContext(entry.Name);
                IEnumerable<HarnessStep> declared = entry.Create().Build(scenarioContext);
                built = declared == null ? new List<HarnessStep>() : declared.Where(step => step != null).ToList();
                if (built.Count == 0) throw new InvalidOperationException("Scenario contains no steps.");
            }
            catch (Exception ex)
            {
                Exception inner = ScenarioCatalog.Unwrap(ex);
                scenarioRecord.Status = "failed";
                scenarioRecord.Error = "场景构建出错：" + inner.Message;
                Report.AddFinding(HarnessSeverity.Error, "harness", "scenario-build-failed",
                    "场景 " + entry.Name + " 的 Build 抛出异常：" + inner, entry.Type.FullName);
                return false;
            }

            EnsureUniqueStepNames(built);
            if (scenarioIndex < selected.Count - 1) built.Add(BuildRecoveryStep());
            steps = built;
            currentIndex = 0;
            abortRemaining = false;
            scenarioDeadline = entry.Attribute.TimeoutSec > 0
                ? scenarioBeginRealtime + entry.Attribute.TimeoutSec
                : float.PositiveInfinity;
            HarnessLog.Info("场景开始：" + entry.Name + "（" + steps.Count + " 步）");
            return true;
        }

        private static void Skip(ScenarioEntry entry, string reason, string severity)
        {
            scenarioRecord.Status = "skipped";
            scenarioRecord.SkipReason = reason;
            Report.AddFinding(new HarnessFinding
            {
                Severity = severity,
                Category = "harness",
                Rule = "scenario-skipped",
                Scenario = entry.Name,
                Step = string.Empty,
                Path = entry.Name,
                Message = "场景 " + entry.Name + " 没有运行：" + reason
            });
            HarnessLog.Warning("跳过场景 " + entry.Name + "：" + reason);
        }

        private static string CheckRequirements(ScenarioEntry entry)
        {
            string[] required = entry.Attribute.RequiresPlugins ?? new string[0];
            var missing = new List<string>();
            foreach (string guid in required)
            {
                bool loaded;
                try { loaded = Adapter.IsPluginLoaded(guid); }
                catch { loaded = false; }
                if (!loaded) missing.Add(guid);
            }
            if (missing.Count > 0) return "缺少依赖的插件：" + string.Join(", ", missing.ToArray());
            if (entry.Attribute.RequiresFocus && Settings.IsBackground)
                return "这个场景需要游戏窗口获得焦点，后台模式（window=background）下不能运行。";
            return null;
        }

        private static void EnsureUniqueStepNames(List<HarnessStep> list)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (HarnessStep step in list)
            {
                if (string.IsNullOrWhiteSpace(step.Name)) step.Name = "step";
                string name = step.Name;
                int suffix = 2;
                while (!seen.Add(name)) name = step.Name + "#" + suffix++;
                step.Name = name;
            }
        }

        private static void CloseScenario()
        {
            steps = null;
            scenarioRecord = null;
            scenarioContext = null;
            CurrentStepRecord = null;
            scenarioDeadline = float.PositiveInfinity;
            Report.EndScenario();
        }

        // ======================== 单步执行 ========================

        private sealed class WaitResult
        {
            internal bool Satisfied;
            internal bool DeadlineHit;
            internal string LastError;
        }

        private static IEnumerator ExecuteStep(HarnessStep step)
        {
            HarnessStepRecord record = Report.FindOrCreateStep(step.Name);
            CurrentStepRecord = record;
            bool guarded = !step.AlwaysRun && !step.IsRecovery;

            if (abortRemaining && !step.AlwaysRun)
            {
                record.Status = "skipped";
                record.Error = "前面的步骤失败，本步没有执行。";
                yield break;
            }
            if (guarded && Time.realtimeSinceStartup > scenarioDeadline)
            {
                FailForScenarioTimeout(record);
                yield break;
            }
            if (step.SkipWhen != null && SafeEval(step.SkipWhen, out _))
            {
                record.Status = step.AllowSkip ? "skipped-allowed" : "skipped";
                record.Error = step.SkipReason ?? "SkipWhen 条件成立，本步没有执行。";
                yield break;
            }

            float begin = Time.realtimeSinceStartup;
            bool ok = true;
            string error = null;
            bool evidenceCaptured = false;

            yield return NoteUnexpectedModal(step, record);

            if (step.Pre != null)
            {
                var wait = new WaitResult();
                yield return WaitFor(step, record, step.Pre, step.PreTimeoutSec, wait);
                if (!wait.Satisfied)
                {
                    ok = false;
                    error = wait.DeadlineHit ? ScenarioTimeoutText() : Prefixed(step.PreTimeoutMessage) +
                        TimeoutText("等待条件（Pre）", step.PreTimeoutSec, wait.LastError);
                    yield return Capture(step.Name + "_pre_timeout", record);
                    evidenceCaptured = true;
                }
            }

            if (ok && step.Act != null)
            {
                try { step.Act(); }
                catch (Exception ex)
                {
                    ok = false;
                    error = Describe("操作（Act）", ex);
                }
            }

            if (ok && step.ActRoutine != null)
            {
                IEnumerator routine = null;
                try { routine = RoutineDriver.Flatten(step.ActRoutine()); }
                catch (Exception ex)
                {
                    ok = false;
                    error = Describe("创建 ActRoutine", ex);
                }

                float actionStarted = Time.realtimeSinceStartup;
                while (ok && routine != null)
                {
                    if (step.ActTimeoutSec > 0 && Time.realtimeSinceStartup - actionStarted > step.ActTimeoutSec)
                    {
                        ok = false;
                        error = "操作（ActRoutine）超过 " + step.ActTimeoutSec + " 秒没有结束。可见文字：" +
                            HarnessUi.Instance.VisibleTextSummary(20);
                        break;
                    }
                    if (guarded && Time.realtimeSinceStartup > scenarioDeadline)
                    {
                        ok = false;
                        error = ScenarioTimeoutText();
                        break;
                    }
                    bool hasNext = false;
                    object current = null;
                    try
                    {
                        hasNext = routine.MoveNext();
                        if (hasNext) current = routine.Current;
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        error = Describe("操作（ActRoutine）", ex);
                    }
                    if (!ok || !hasNext) break;
                    yield return current;
                }
                try { (routine as IDisposable)?.Dispose(); }
                catch (Exception ex)
                {
                    if (ok)
                    {
                        ok = false;
                        error = Describe("ActRoutine 收尾", ex);
                    }
                }
            }

            if (ok && step.Post != null)
            {
                var wait = new WaitResult();
                yield return WaitFor(step, record, step.Post, step.PostTimeoutSec, wait);
                if (!wait.Satisfied)
                {
                    ok = false;
                    error = wait.DeadlineHit ? ScenarioTimeoutText() : Prefixed(step.PostTimeoutMessage) +
                        TimeoutText("结果检查（Post）", step.PostTimeoutSec, wait.LastError);
                    yield return Capture(step.Name + "_post_timeout", record);
                    evidenceCaptured = true;
                }
            }

            if (ok && step.CaptureAfter)
            {
                if (step.SettleBeforeCaptureSec > 0f)
                    yield return new WaitForSecondsRealtime(step.SettleBeforeCaptureSec);
                yield return Capture(step.Name, record);
            }
            else if (!ok && !evidenceCaptured)
            {
                yield return Capture(step.Name + "_failed", record);
            }

            record.DurationSec = Time.realtimeSinceStartup - begin;
            record.Status = ok ? "passed" : "failed";
            record.Error = error;

            if (ok)
            {
                HarnessLog.Info("步骤通过：" + step.Name + "（" +
                    record.DurationSec.ToString("F1", CultureInfo.InvariantCulture) + "s）");
                yield break;
            }

            HarnessLog.Error("步骤失败：" + step.Name + " — " + error);
            Report.AddFinding(new HarnessFinding
            {
                Severity = step.IsRecovery || !step.ContinueOnFailure ? HarnessSeverity.Error : HarnessSeverity.Warning,
                Category = "harness",
                Rule = step.IsRecovery ? "recover-to-main-menu-failed" : "step-failed",
                Path = step.Name,
                Message = step.IsRecovery ? "场景结束后没能回到主菜单，后面的场景都不再运行：" + error : error
            });
            if (!step.ContinueOnFailure) abortRemaining = true;
            if (step.IsRecovery) abortAll = true;
        }

        private static void FailForScenarioTimeout(HarnessStepRecord record)
        {
            record.Status = "failed";
            record.Error = ScenarioTimeoutText();
            abortRemaining = true;
            if (scenarioTimedOut) return;
            scenarioTimedOut = true;
            Report.AddFinding(HarnessSeverity.Error, "harness", "scenario-timeout", ScenarioTimeoutText(),
                CurrentScenarioName);
        }

        private static string ScenarioTimeoutText()
        {
            ScenarioEntry entry = scenarioIndex < selected.Count ? selected[scenarioIndex] : null;
            return "场景超过了 " + (entry == null ? 0f : entry.Attribute.TimeoutSec) + " 秒的上限（TimeoutSec）。";
        }

        private static IEnumerator WaitFor(HarnessStep step, HarnessStepRecord record, Func<bool> condition,
            float timeoutSec, WaitResult result)
        {
            float deadline = Time.realtimeSinceStartup + Mathf.Max(0.1f, timeoutSec);
            bool guarded = !step.AlwaysRun && !step.IsRecovery;
            while (true)
            {
                yield return NoteUnexpectedModal(step, record);
                if (SafeEval(condition, out string evaluationError))
                {
                    result.Satisfied = true;
                    yield break;
                }
                if (evaluationError != null) result.LastError = evaluationError;
                float now = Time.realtimeSinceStartup;
                if (now >= deadline) yield break;
                if (guarded && now >= scenarioDeadline)
                {
                    result.DeadlineHit = true;
                    yield break;
                }
                yield return new WaitForSecondsRealtime(PollIntervalSec);
            }
        }

        private static string TimeoutText(string phase, float timeoutSec, string lastError)
        {
            string text = phase + "在 " + timeoutSec + " 秒内没有满足。可见文字：" +
                HarnessUi.Instance.VisibleTextSummary(20);
            if (!string.IsNullOrEmpty(lastError)) text += "。最后一次求值异常：" + lastError;
            return text;
        }

        private static string Prefixed(string message)
        {
            return string.IsNullOrEmpty(message) ? string.Empty : message + " ";
        }

        private static string Describe(string phase, Exception ex)
        {
            Exception inner = ScenarioCatalog.Unwrap(ex);
            if (inner is HarnessAssertionException) return inner.Message;
            string stack = inner.StackTrace ?? string.Empty;
            string[] lines = stack.Split('\n');
            string head = string.Join("\n", lines.Take(6).Select(line => line.TrimEnd()).ToArray());
            return phase + "抛出 " + inner.GetType().Name + "：" + inner.Message +
                (head.Length > 0 ? "\n" + head : string.Empty);
        }

        private static bool SafeEval(Func<bool> condition, out string error)
        {
            error = null;
            try { return condition(); }
            catch (Exception ex)
            {
                Exception inner = ScenarioCatalog.Unwrap(ex);
                error = inner.GetType().Name + "：" + inner.Message;
                return false;
            }
        }

        private static void HarnessFailure(string rule, string message, Exception ex)
        {
            Exception inner = ScenarioCatalog.Unwrap(ex);
            Report.AddFinding(HarnessSeverity.Error, "harness", rule, message + "：" + inner);
            HarnessLog.Error(message + "：" + inner);
        }

        // ======================== 场景之间恢复 ========================

        private static HarnessStep BuildRecoveryStep()
        {
            return new HarnessStep
            {
                Name = "recover_to_main_menu",
                IsRecovery = true,
                AlwaysRun = true,
                ContinueOnFailure = true,
                ExpectModal = true,
                Post = TryRecover,
                PostTimeoutSec = RecoveryTimeoutSec
            };
        }

        private static bool TryRecover()
        {
            bool clean = true;
            foreach (IHarnessExtension extension in extensions)
            {
                try
                {
                    if (!extension.TryRecover()) clean = false;
                }
                catch (Exception ex)
                {
                    clean = false;
                    HarnessLog.Warning("扩展 " + extension.GetType().FullName + " 的 TryRecover 出错：" + ex.Message);
                }
            }
            return clean && Adapter.TryRecoverToBaseline();
        }

        // ======================== 意外弹窗 ========================

        private static IEnumerator NoteUnexpectedModal(HarnessStep step, HarnessStepRecord record)
        {
            if (step.ExpectModal) yield break;
            string modal = DetectUnexpectedModal();
            if (modal == null || !record.NotedModals.Add(modal)) yield break;
            string evidence = modal + "；可见文字：" + HarnessUi.Instance.VisibleTextSummary(20);
            HarnessLog.Error("步骤 " + step.Name + " 出现意外弹窗：" + evidence);
            Report.AddFinding(HarnessSeverity.Error, "harness", "unexpected-modal",
                "出现了没有预期的弹窗：" + evidence + "。如果这一步本来就会弹窗，给步骤加 ExpectingModal()。",
                step.Name);
            yield return Capture(step.Name + "_modal", record);
        }

        private static string DetectUnexpectedModal()
        {
            try
            {
                string modal = Adapter.DescribeUnexpectedModal();
                if (modal != null) return modal;
            }
            catch
            {
            }
            foreach (IHarnessExtension extension in extensions)
            {
                try
                {
                    string modal = extension.DescribeUnexpectedModal();
                    if (modal != null) return modal;
                }
                catch
                {
                }
            }
            return null;
        }

        // ======================== 截图与界面导出 ========================

        internal static IEnumerator CaptureForCurrentStep(string label)
        {
            return Capture(label, CurrentStepRecord);
        }

        internal static IEnumerator Capture(string label, HarnessStepRecord record)
        {
            screenshotCounter++;
            string fileName = screenshotCounter.ToString("000", CultureInfo.InvariantCulture) + "_" + Sanitize(label) + ".png";
            string absolute = Path.Combine(Settings.ScreenshotDirectory, fileName);
            string relative = "screenshots/" + fileName;
            bool captured = false;
            yield return HarnessScreenshot.Capture(absolute, Settings, Report, record?.Name, label, ok => captured = ok);
            if (captured)
            {
                if (record != null) record.Screenshots.Add(relative);
                else Report.RunScreenshots.Add(relative);
            }
            else
            {
                Report.AddFinding(HarnessSeverity.Warning, "harness", "screenshot-failed",
                    "截图失败：" + relative, label);
            }
        }

        internal static string DumpUi(string label)
        {
            dumpCounter++;
            string directory = Path.Combine(Settings.RunDirectory, "ui-dump");
            Directory.CreateDirectory(directory);
            string fileName = dumpCounter.ToString("00", CultureInfo.InvariantCulture) + "_" + Sanitize(label) + ".json";
            JObject dump = UiDump.Capture(Adapter?.Game);
            Redactor.Apply(dump);
            File.WriteAllText(Path.Combine(directory, fileName), dump.ToString(Formatting.Indented), new UTF8Encoding(false));
            string relative = "ui-dump/" + fileName;
            if (CurrentStepRecord != null && scenarioRecord != null) CurrentStepRecord.Artifacts.Add(relative);
            else Report.RunArtifacts.Add(relative);
            return relative;
        }

        internal static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "step";
            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
                builder.Append(c < 128 && (char.IsLetterOrDigit(c) || c == '_' || c == '-') ? c : '_');
            string result = builder.ToString();
            return result.Length > 80 ? result.Substring(0, 80) : result;
        }

        // ======================== 看门狗与收尾 ========================

        internal static IEnumerator Watchdog()
        {
            while (!finalized)
            {
                if (startRealtime >= 0f && Time.realtimeSinceStartup - startRealtime > Settings.TimeoutSec)
                {
                    string position = scenarioIndex < selected.Count ? selected[scenarioIndex].Name : "(启动阶段)";
                    if (CurrentStepName != null) position += "/" + CurrentStepName;
                    string message = "整次运行超过 " + Settings.TimeoutSec + " 秒（timeoutSec），强制结束。当时位于：" +
                        position + "；可见文字：" + HarnessUi.Instance.VisibleTextSummary(20);
                    HarnessLog.Error(message);
                    HarnessStepRecord record = steps != null ? CurrentStepRecord : null;
                    if (record != null)
                    {
                        record.Status = "failed";
                        record.Error = message;
                    }
                    yield return Capture("watchdog_timeout", record);
                    Report.AddFinding(HarnessSeverity.Error, "harness", "watchdog-timeout", message, position);
                    if (scenarioRecord != null)
                    {
                        scenarioRecord.Status = "failed";
                        scenarioRecord.Error = message;
                        scenarioRecord.DurationSec = Time.realtimeSinceStartup - scenarioBeginRealtime;
                    }
                    FinalizeAndQuit("watchdog-timeout");
                    yield break;
                }
                yield return new WaitForSecondsRealtime(1f);
            }
        }

        /// <summary>写报告并退出游戏。只执行一次。</summary>
        internal static void FinalizeAndQuit(string abortReason)
        {
            if (finalized) return;
            finalized = true;
            string reportPath = Path.Combine(Settings.RunDirectory, "report.json");
            try
            {
                if (abortReason != null)
                {
                    Report.Aborted = true;
                    Report.AbortReason = abortReason;
                }
                foreach (IHarnessExtension extension in extensions)
                {
                    try { extension.OnRunEnd(Report); }
                    catch (Exception ex) { HarnessFailure("extension-end-failed", "扩展 " + extension.GetType().FullName + " 的 OnRunEnd 出错", ex); }
                }
                try { Fixtures?.CleanupAll(Report); }
                catch (Exception ex) { HarnessFailure("fixture-cleanup-failed", "清理测试数据出错", ex); }
                try { Adapter?.OnRunFinished(Report); }
                catch (Exception ex) { HarnessFailure("adapter-finish-failed", "适配层收尾出错", ex); }
                try { Adapter?.DescribeEnvironment(Report.Environment); }
                catch (Exception ex) { HarnessLog.Warning("填充环境信息出错：" + ex.Message); }
                Report.Environment["resolution"] = new JObject
                {
                    ["requested"] = Settings.HasExpectedResolution
                        ? new JObject { ["width"] = Settings.Width, ["height"] = Settings.Height }
                        : null,
                    ["actual"] = new JObject
                    {
                        ["width"] = Screen.width,
                        ["height"] = Screen.height,
                        ["fullScreen"] = Screen.fullScreen
                    }
                };
                if (Input != null) Report.Environment["input"] = Input.Describe();
                if (Settings.Source != null) Report.Environment["source"] = Settings.Source.DeepClone();
                UnityLogMonitor.Complete(Report);
                Report.FinishedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                Report.DurationSec = Time.realtimeSinceStartup - startRealtime;
                Report.Write(reportPath, Redactor);
                HarnessLog.Info("报告已写入 " + reportPath + "（error " + Report.CountBySeverity(HarnessSeverity.Error) +
                    "，warning " + Report.CountBySeverity(HarnessSeverity.Warning) + "）。");
            }
            catch (Exception ex)
            {
                HarnessLog.Error("写报告失败：" + ex);
                try
                {
                    File.WriteAllText(Path.Combine(Settings.RunDirectory, "report-error.txt"), ex.ToString(),
                        new UTF8Encoding(false));
                }
                catch
                {
                }
            }
            Application.Quit();
        }
    }

    /// <summary>驱动器宿主。只负责承载协程，状态全在 <see cref="HarnessRuntime"/> 里。</summary>
    internal sealed class HarnessRunner : MonoBehaviour
    {
        private bool driving;
        private bool tickFailed;

        internal void StartIfNeeded()
        {
            if (driving || HarnessRuntime.IsFinalized) return;
            driving = true;
            StartCoroutine(HarnessRuntime.Drive());
            StartCoroutine(HarnessRuntime.Watchdog());
        }

        private void Update()
        {
            if (tickFailed) return;
            try { HarnessRuntime.TickAdapter(); }
            catch (Exception ex)
            {
                tickFailed = true;
                HarnessLog.Warning("适配层 Tick 出错，已停用：" + ex.Message);
            }
        }

        private void OnDestroy()
        {
            driving = false;
            if (!HarnessRuntime.IsFinalized)
                HarnessLog.Warning("驱动器被销毁，等待重建后从当前步骤继续。");
        }
    }
}
