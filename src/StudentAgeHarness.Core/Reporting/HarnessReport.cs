using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StudentAgeHarness.Engine;
using UnityEngine;

namespace StudentAgeHarness
{
    /// <summary>单个步骤的执行记录。</summary>
    public sealed class HarnessStepRecord
    {
        public string Name { get; internal set; }

        /// <summary>pending / passed / failed / skipped / skipped-allowed</summary>
        public string Status { get; internal set; } = "pending";

        public double DurationSec { get; internal set; }
        public string Error { get; internal set; }

        /// <summary>截图，路径相对运行目录。</summary>
        public List<string> Screenshots { get; } = new List<string>();

        /// <summary>其它产物（例如 ui-dump），路径相对运行目录。</summary>
        public List<string> Artifacts { get; } = new List<string>();

        // 同一步里已经记过的意外弹窗，避免轮询时重复记。
        internal readonly HashSet<string> NotedModals = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>单个场景的执行记录。</summary>
    public sealed class HarnessScenarioRecord
    {
        public string Name { get; internal set; }
        public string Description { get; internal set; }
        public string Pack { get; internal set; }
        public string[] Tags { get; internal set; }

        /// <summary>pending / passed / failed / skipped</summary>
        public string Status { get; internal set; } = "pending";

        public double DurationSec { get; internal set; }
        public string Error { get; internal set; }
        public string SkipReason { get; internal set; }
        public List<HarnessStepRecord> Steps { get; } = new List<HarnessStepRecord>();
    }

    /// <summary>
    /// 一次运行的报告，结束时写成 report.json（schema studentage-harness-report/2）。
    /// JSON 手工构造，不反射序列化 Unity 类型（Vector2.normalized 之类的自引用属性会把序列化器带进死循环）。
    /// </summary>
    public sealed class HarnessReport
    {
        public const string SchemaId = "studentage-harness-report/2";

        private readonly object gate = new object();
        private readonly RunSettings settings;
        private readonly List<HarnessScenarioRecord> scenarios = new List<HarnessScenarioRecord>();
        private readonly List<HarnessFinding> findings = new List<HarnessFinding>();
        private readonly JObject extensions = new JObject();
        private HarnessScenarioRecord currentScenario;

        internal HarnessReport(RunSettings settings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public string RunId => settings.RunId;
        public string StartedAtUtc { get; internal set; }
        public string FinishedAtUtc { get; internal set; }
        public double DurationSec { get; internal set; }
        public bool Aborted { get; internal set; }
        public string AbortReason { get; internal set; }

        /// <summary>环境信息（游戏版本、BepInEx、已加载插件、场景包）。适配插件在结束时填充。</summary>
        public JObject Environment { get; } = new JObject();

        /// <summary>隔离信息（隔离存档目录、被拦截的写入次数等）。</summary>
        public JObject Isolation { get; } = new JObject();

        internal readonly List<string> SelectedScenarioNames = new List<string>();
        internal readonly List<string> RunScreenshots = new List<string>();
        internal readonly List<string> RunArtifacts = new List<string>();

        public IReadOnlyList<HarnessScenarioRecord> Scenarios
        {
            get { lock (gate) return scenarios.ToArray(); }
        }

        /// <summary>到目前为止的发现（副本）。</summary>
        public IReadOnlyList<HarnessFinding> Findings
        {
            get { lock (gate) return findings.ToArray(); }
        }

        internal HarnessScenarioRecord BeginScenario(string name, string description, string pack, string[] tags)
        {
            lock (gate)
            {
                HarnessScenarioRecord existing = scenarios.Find(item =>
                    string.Equals(item.Name, name, StringComparison.Ordinal));
                if (existing == null)
                {
                    existing = new HarnessScenarioRecord
                    {
                        Name = name,
                        Description = description,
                        Pack = pack,
                        Tags = tags ?? new string[0]
                    };
                    scenarios.Add(existing);
                }
                currentScenario = existing;
                return existing;
            }
        }

        internal void EndScenario()
        {
            lock (gate) currentScenario = null;
        }

        /// <summary>在当前场景里按名字找步骤记录，没有就新建；没有当前场景时返回一个不进报告的临时记录。</summary>
        internal HarnessStepRecord FindOrCreateStep(string name)
        {
            lock (gate)
            {
                if (currentScenario == null) return new HarnessStepRecord { Name = name };
                HarnessStepRecord existing = currentScenario.Steps.Find(step =>
                    string.Equals(step.Name, name, StringComparison.Ordinal));
                if (existing != null) return existing;
                var record = new HarnessStepRecord { Name = name };
                currentScenario.Steps.Add(record);
                return record;
            }
        }

        public void AddFinding(HarnessFinding finding)
        {
            if (finding == null) return;
            if (string.IsNullOrEmpty(finding.Severity)) finding.Severity = HarnessSeverity.Info;
            if (finding.Scenario == null) finding.Scenario = HarnessRuntime.CurrentScenarioName;
            if (finding.Step == null) finding.Step = HarnessRuntime.CurrentStepName;
            lock (gate) findings.Add(finding);
        }

        public HarnessFinding AddFinding(string severity, string category, string rule, string message,
            string path = null)
        {
            HarnessFinding finding = HarnessFinding.Create(severity, category, rule, message, path);
            AddFinding(finding);
            return finding;
        }

        /// <summary>
        /// 写入场景包自己的数据（报告 extensions 节点）。key 建议带前缀，例如 "mymod.saveCheck"。
        /// value 可以是 JToken，也可以是能被 Newtonsoft 序列化的普通对象（不要直接放 Unity 对象）。
        /// </summary>
        public void SetExtension(string key, object value)
        {
            if (string.IsNullOrEmpty(key)) return;
            JToken token;
            try { token = value == null ? JValue.CreateNull() : value as JToken ?? JToken.FromObject(value); }
            catch (Exception ex) { token = new JValue("(无法序列化：" + ex.Message + ")"); }
            lock (gate) extensions[key] = token;
        }

        public int CountBySeverity(string severity)
        {
            lock (gate) return findings.Count(item => SameSeverity(item.Severity, severity));
        }

        internal void Write(string path, PathRedactor redactor)
        {
            JObject json = ToJson();
            if (redactor != null) redactor.Apply(json);
            string directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, json.ToString(Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }

        internal JObject ToJson()
        {
            lock (gate)
            {
                int stepCount = 0, failedSteps = 0, skippedSteps = 0, allowedSkips = 0, screenshots = RunScreenshots.Count;
                int passedScenarios = 0, failedScenarios = 0, skippedScenarios = 0;
                var scenariosJson = new JArray();
                foreach (HarnessScenarioRecord scenario in scenarios)
                {
                    if (scenario.Status == "passed") passedScenarios++;
                    else if (scenario.Status == "skipped") skippedScenarios++;
                    else failedScenarios++;
                    var stepsJson = new JArray();
                    foreach (HarnessStepRecord step in scenario.Steps)
                    {
                        stepCount++;
                        screenshots += step.Screenshots.Count;
                        if (step.Status == "failed") failedSteps++;
                        else if (step.Status == "skipped") skippedSteps++;
                        else if (step.Status == "skipped-allowed") allowedSkips++;
                        stepsJson.Add(new JObject
                        {
                            ["name"] = step.Name,
                            ["status"] = step.Status,
                            ["durationSec"] = Math.Round(step.DurationSec, 2),
                            ["error"] = step.Error,
                            ["screenshots"] = new JArray(step.Screenshots),
                            ["artifacts"] = new JArray(step.Artifacts)
                        });
                    }
                    scenariosJson.Add(new JObject
                    {
                        ["name"] = scenario.Name,
                        ["description"] = scenario.Description,
                        ["pack"] = scenario.Pack,
                        ["tags"] = new JArray(scenario.Tags ?? new string[0]),
                        ["status"] = scenario.Status,
                        ["durationSec"] = Math.Round(scenario.DurationSec, 2),
                        ["error"] = scenario.Error,
                        ["skipReason"] = scenario.SkipReason,
                        ["steps"] = stepsJson
                    });
                }

                var findingsJson = new JArray();
                foreach (HarnessFinding finding in findings) findingsJson.Add(FindingJson(finding));

                return new JObject
                {
                    ["schema"] = SchemaId,
                    ["run"] = new JObject
                    {
                        ["id"] = settings.RunId,
                        ["startedAtUtc"] = StartedAtUtc,
                        ["finishedAtUtc"] = FinishedAtUtc,
                        ["durationSec"] = Math.Round(DurationSec, 1),
                        ["aborted"] = Aborted,
                        ["abortReason"] = AbortReason,
                        ["window"] = settings.Window,
                        ["timeoutSec"] = settings.TimeoutSec,
                        ["requestedScenarios"] = new JArray(settings.Scenarios),
                        ["selectedScenarios"] = new JArray(SelectedScenarioNames),
                        ["screenshots"] = new JArray(RunScreenshots),
                        ["artifacts"] = new JArray(RunArtifacts)
                    },
                    ["harness"] = new JObject
                    {
                        ["version"] = HarnessVersion.Value,
                        ["plugin"] = "com.studentage.harness"
                    },
                    ["environment"] = Environment.DeepClone(),
                    ["isolation"] = Isolation.DeepClone(),
                    ["scenarios"] = scenariosJson,
                    ["findings"] = findingsJson,
                    ["summary"] = new JObject
                    {
                        ["scenarios"] = scenarios.Count,
                        ["passedScenarios"] = passedScenarios,
                        ["failedScenarios"] = failedScenarios,
                        ["skippedScenarios"] = skippedScenarios,
                        ["steps"] = stepCount,
                        ["failedSteps"] = failedSteps,
                        ["skippedSteps"] = skippedSteps,
                        ["allowedSkippedSteps"] = allowedSkips,
                        ["errors"] = findings.Count(item => SameSeverity(item.Severity, HarnessSeverity.Error)),
                        ["warnings"] = findings.Count(item => SameSeverity(item.Severity, HarnessSeverity.Warning)),
                        ["infos"] = findings.Count(item => SameSeverity(item.Severity, HarnessSeverity.Info)),
                        ["screenshots"] = screenshots
                    },
                    ["extensions"] = extensions.DeepClone()
                };
            }
        }

        private static bool SameSeverity(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static JObject FindingJson(HarnessFinding finding)
        {
            var metrics = new JObject();
            if (finding.Metrics != null)
                foreach (KeyValuePair<string, object> pair in finding.Metrics)
                    metrics[pair.Key] = MetricToken(pair.Value);
            return new JObject
            {
                ["severity"] = finding.Severity,
                ["category"] = finding.Category,
                ["rule"] = finding.Rule,
                ["scenario"] = finding.Scenario,
                ["step"] = finding.Step,
                ["path"] = finding.Path,
                ["message"] = finding.Message,
                ["metrics"] = metrics
            };
        }

        internal static JToken MetricToken(object value)
        {
            if (value == null) return JValue.CreateNull();
            if (value is JToken token) return token.DeepClone();
            if (value is Vector2 v2) return new JObject { ["x"] = v2.x, ["y"] = v2.y };
            if (value is Vector3 v3) return new JObject { ["x"] = v3.x, ["y"] = v3.y, ["z"] = v3.z };
            if (value is Rect rect)
                return new JObject { ["x"] = rect.x, ["y"] = rect.y, ["width"] = rect.width, ["height"] = rect.height };
            if (value is Color color)
                return new JValue("#" + ColorUtility.ToHtmlStringRGBA(color));
            if (value is string || value is bool || value is int || value is long || value is float ||
                value is double || value is decimal)
                return new JValue(value);
            try { return JToken.FromObject(value); }
            catch { return new JValue(Convert.ToString(value, CultureInfo.InvariantCulture)); }
        }
    }
}
