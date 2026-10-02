using System;
using System.Collections.Generic;
using StudentAgeHarness.Engine;
using UnityEngine;

namespace StudentAgeHarness.Diagnostics
{
    /// <summary>
    /// 把 Unity 未处理异常（包括 async void 续体经 UnitySynchronizationContext 抛出的）记成 error。
    /// 步骤断言只看功能结果，异步续体在已销毁的界面上崩溃时步骤照样会通过，所以要单独监听。
    /// 同一场景同一签名只记一条，累计次数。启动时自发一条带标记的探针，证明监听真的生效。
    /// Debug.LogError 只计数并留几条样本（原版游戏会用它输出普通信息），不判失败。
    /// </summary>
    internal static class UnityLogMonitor
    {
        private const int MaxDistinctFindings = 40;
        private const int MaxErrorSamples = 10;

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, HarnessFinding> Seen =
            new Dictionary<string, HarnessFinding>(StringComparer.Ordinal);
        private static readonly List<string> ErrorSamples = new List<string>();
        private static Func<string, bool> noiseFilter;
        private static bool installed;
        private static int ignoredNoise;
        private static int droppedDistinct;
        private static int errorLogs;
        private static string selfCheckSignature;

        internal static void Install()
        {
            lock (Sync)
            {
                if (installed) return;
                installed = true;
            }
            Application.logMessageReceivedThreaded += OnLogMessage;
            try
            {
                throw new InvalidOperationException(UnityExceptionPolicy.SelfCheckMarker);
            }
            catch (InvalidOperationException probe)
            {
                Debug.LogException(probe);
            }
        }

        internal static void SetNoiseFilter(Func<string, bool> filter)
        {
            noiseFilter = filter;
        }

        internal static void Complete(HarnessReport report)
        {
            lock (Sync)
            {
                if (!installed) return;
                installed = false;
            }
            Application.logMessageReceivedThreaded -= OnLogMessage;
            if (report == null) return;

            int noise, dropped, errors;
            string probe;
            string[] samples;
            lock (Sync)
            {
                noise = ignoredNoise;
                dropped = droppedDistinct;
                errors = errorLogs;
                probe = selfCheckSignature;
                samples = ErrorSamples.ToArray();
            }

            report.AddFinding(probe != null
                ? new HarnessFinding
                {
                    Severity = HarnessSeverity.Info,
                    Category = "unity-log",
                    Rule = "exception-monitor-self-check",
                    Scenario = string.Empty,
                    Step = string.Empty,
                    Message = "Unity 异常监听自检通过：收到了启动时发出的探针。"
                }
                : new HarnessFinding
                {
                    Severity = HarnessSeverity.Error,
                    Category = "unity-log",
                    Rule = "exception-monitor-inactive",
                    Scenario = string.Empty,
                    Step = string.Empty,
                    Message = "Unity 异常监听没有收到启动探针，这份报告无法证明运行中没有未处理异常。"
                });
            if (noise > 0)
                report.AddFinding(new HarnessFinding
                {
                    Severity = HarnessSeverity.Info,
                    Category = "unity-log",
                    Rule = "known-game-noise",
                    Scenario = string.Empty,
                    Step = string.Empty,
                    Message = "忽略了 " + noise + " 条游戏自身已知无害的异常日志。"
                }.WithMetric("occurrences", noise));
            if (dropped > 0)
                report.AddFinding(new HarnessFinding
                {
                    Severity = HarnessSeverity.Error,
                    Category = "unity-log",
                    Rule = "exception-overflow",
                    Scenario = string.Empty,
                    Step = string.Empty,
                    Message = "不同签名的异常超过 " + MaxDistinctFindings + " 种，另有 " + dropped +
                        " 条没有单独列出，请查看 logs/Player.log。"
                });
            if (errors > 0)
                report.AddFinding(new HarnessFinding
                {
                    Severity = HarnessSeverity.Info,
                    Category = "unity-log",
                    Rule = "debug-log-error",
                    Scenario = string.Empty,
                    Step = string.Empty,
                    Message = "运行中出现 " + errors + " 条 Debug.LogError（原版游戏也会用它输出普通信息，仅供参考）。"
                }
                .WithMetric("occurrences", errors)
                .WithMetric("samples", samples));
        }

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            // 日志回调里绝不能再抛异常：Unity 会把它当成新的异常日志递归回来。
            try
            {
                if (type == LogType.Error || type == LogType.Assert)
                {
                    lock (Sync)
                    {
                        errorLogs++;
                        if (ErrorSamples.Count < MaxErrorSamples)
                            ErrorSamples.Add(UnityExceptionPolicy.Truncate(condition, 200));
                    }
                    return;
                }
                if (type != LogType.Exception) return;
                HarnessReport report = HarnessRuntime.Report;
                if (report == null) return;
                if (UnityExceptionPolicy.IsSelfCheck(condition))
                {
                    lock (Sync)
                        if (selfCheckSignature == null)
                            selfCheckSignature = UnityExceptionPolicy.Signature(condition, stackTrace);
                    return;
                }
                Func<string, bool> filter = noiseFilter;
                if (filter != null && filter(condition))
                {
                    lock (Sync) ignoredNoise++;
                    return;
                }

                string scenario = HarnessRuntime.CurrentScenarioName ?? "(启动阶段)";
                string step = HarnessRuntime.CurrentStepName ?? "(步骤之间)";
                string signature = UnityExceptionPolicy.Signature(condition, stackTrace);
                string key = scenario + "\n" + signature;
                HarnessFinding finding;
                lock (Sync)
                {
                    if (!installed) return;
                    if (Seen.TryGetValue(key, out HarnessFinding existing))
                    {
                        existing.Metrics["occurrences"] = (int)existing.Metrics["occurrences"] + 1;
                        return;
                    }
                    if (Seen.Count >= MaxDistinctFindings)
                    {
                        droppedDistinct++;
                        return;
                    }
                    finding = new HarnessFinding
                    {
                        Severity = HarnessSeverity.Error,
                        Category = "unity-log",
                        Rule = "unhandled-exception",
                        Scenario = HarnessRuntime.CurrentScenarioName ?? string.Empty,
                        Step = HarnessRuntime.CurrentStepName ?? string.Empty,
                        Path = signature,
                        Message = "未处理异常：" + UnityExceptionPolicy.DescribeCondition(condition)
                    }
                    .WithMetric("signature", signature)
                    .WithMetric("firstStep", step)
                    .WithMetric("occurrences", 1)
                    .WithMetric("stack", UnityExceptionPolicy.Truncate(stackTrace, UnityExceptionPolicy.MaxStackChars));
                    Seen[key] = finding;
                }
                report.AddFinding(finding);
                HarnessLog.Error("Unity 未处理异常（" + scenario + "/" + step + "）：" + signature);
            }
            catch
            {
            }
        }
    }
}
