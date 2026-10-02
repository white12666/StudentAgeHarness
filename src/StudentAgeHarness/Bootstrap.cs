using System;
using BepInEx.Logging;
using StudentAgeHarness.Engine;
using UnityEngine;

namespace StudentAgeHarness.Plugin
{
    /// <summary>
    /// 在第一帧再启动引擎：那时 BepInEx 已经加载完全部插件，场景包引用的被测 mod 程序集都能解析到同一份。
    /// 宿主对象不属于任何场景；万一被游戏销毁，UIMgr.Init 的钩子也会补跑。
    /// </summary>
    internal sealed class Bootstrap : MonoBehaviour
    {
        private static Action pending;

        internal static void Schedule(Action action)
        {
            pending = action;
            var host = new GameObject("StudentAgeHarness.Bootstrap") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(host);
            host.AddComponent<Bootstrap>();
        }

        internal static void RunPending()
        {
            Action action = pending;
            pending = null;
            if (action == null) return;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                HarnessLog.Error("启动引擎失败：" + ex);
                HarnessRuntime.Report?.AddFinding(HarnessSeverity.Error, "harness", "engine-start-failed", "启动引擎失败：" + ex);
                HarnessRuntime.FinalizeAndQuit("engine-start-failed");
            }
        }

        private void Update()
        {
            RunPending();
            Destroy(gameObject);
        }
    }

    internal sealed class BepInExLogSink : IHarnessLogSink
    {
        private readonly ManualLogSource source;

        internal BepInExLogSink(ManualLogSource source)
        {
            this.source = source;
        }

        public void Info(string message) => source.LogInfo(message);

        public void Warning(string message) => source.LogWarning(message);

        public void Error(string message) => source.LogError(message);
    }
}
