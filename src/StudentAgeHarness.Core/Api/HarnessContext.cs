using System;
using System.Collections;
using System.Collections.Generic;
using StudentAgeHarness.Diagnostics;
using StudentAgeHarness.Engine;
using UnityEngine;

namespace StudentAgeHarness
{
    /// <summary>整次运行共享的上下文。扩展拿到的是它，场景拿到的是派生的 <see cref="HarnessContext"/>。</summary>
    public class HarnessRunContext
    {
        internal HarnessRunContext()
        {
        }

        public string RunId => HarnessRuntime.Settings.RunId;

        /// <summary>本次运行的隔离目录（报告、截图、隔离存档都在这里）。</summary>
        public string RunDirectory => HarnessRuntime.Settings.RunDirectory;

        /// <summary>true 表示游戏窗口被挪到了屏幕外，不能依赖操作系统焦点。</summary>
        public bool IsBackgroundWindow => HarnessRuntime.Settings.IsBackground;

        public HarnessReport Report => HarnessRuntime.Report;

        public IHarnessGame Game => HarnessRuntime.Adapter.Game;

        /// <summary>按屏幕上可见的文字和几何位置查找、点击界面。</summary>
        public HarnessUi Ui => HarnessUi.Instance;

        /// <summary>通过 Unity InputSystem 发送真实的键盘和鼠标输入。</summary>
        public HarnessInput Input => HarnessRuntime.Input;

        /// <summary>创建会在运行结束时自动清理的测试数据目录。</summary>
        public HarnessFixtures Fixtures => HarnessRuntime.Fixtures;

        public bool IsPluginLoaded(string guid)
        {
            return HarnessRuntime.Adapter.IsPluginLoaded(guid);
        }

        public void Log(string message)
        {
            HarnessLog.Info(message);
        }

        /// <summary>截一张图并挂到当前步骤上。在 ActRoutine 里 <c>yield return ctx.Capture("标签")</c>。</summary>
        public IEnumerator Capture(string label)
        {
            return HarnessRuntime.CaptureForCurrentStep(label);
        }

        /// <summary>
        /// 把当前界面的可见文字、可点击目标、输入框和最上层视图的节点树写到 ui-dump 目录，返回相对路径。
        /// 写场景时用它找按钮文字和节点路径。
        /// </summary>
        public string DumpUi(string label)
        {
            return HarnessRuntime.DumpUi(label);
        }
    }

    /// <summary>单个场景的上下文。</summary>
    public sealed class HarnessContext : HarnessRunContext
    {
        internal HarnessContext(string scenarioName)
        {
            ScenarioName = scenarioName;
            State = new Dictionary<string, object>(StringComparer.Ordinal);
        }

        public string ScenarioName { get; }

        /// <summary>同一场景内的步骤之间传递数据用。</summary>
        public IDictionary<string, object> State { get; }

        /// <summary>条件不成立时让当前步骤失败，消息会写进报告。</summary>
        public void Assert(bool condition, string message)
        {
            if (!condition) throw new HarnessAssertionException(message);
        }

        public void Fail(string message)
        {
            throw new HarnessAssertionException(message);
        }

        public HarnessFinding Info(string rule, string message, string path = null)
        {
            return Report.AddFinding(HarnessSeverity.Info, "scenario", rule, message, path);
        }

        public HarnessFinding Warn(string rule, string message, string path = null)
        {
            return Report.AddFinding(HarnessSeverity.Warning, "scenario", rule, message, path);
        }

        public HarnessFinding Error(string rule, string message, string path = null)
        {
            return Report.AddFinding(HarnessSeverity.Error, "scenario", rule, message, path);
        }

        /// <summary>
        /// 对 root 下的 UGUI 做布局检查（文字截断、子节点越界、兄弟重叠、点击区域过小），结果写进报告。
        /// maxSeverity 可以把严重程度封顶，例如检查原版界面时传 <see cref="HarnessSeverity.Info"/>，只留档不判失败。
        /// 返回写入的发现条数。
        /// </summary>
        public int LintLayout(GameObject root, string label, string maxSeverity = null)
        {
            List<HarnessFinding> findings = LayoutLinter.Run(root, label);
            foreach (HarnessFinding finding in findings)
            {
                if (maxSeverity != null) finding.Severity = HarnessSeverity.Cap(finding.Severity, maxSeverity);
                Report.AddFinding(finding);
            }
            return findings.Count;
        }
    }

    /// <summary>断言失败。报告里只写消息，不写调用栈。</summary>
    public sealed class HarnessAssertionException : Exception
    {
        public HarnessAssertionException(string message) : base(message)
        {
        }
    }
}
