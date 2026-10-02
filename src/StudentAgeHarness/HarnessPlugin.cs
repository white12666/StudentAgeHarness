using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Sdk;
using StudentAgeHarness.Engine;
using StudentAgeHarness.Plugin.Adapter;
using StudentAgeHarness.Plugin.Isolation;
using UnityEngine;

namespace StudentAgeHarness.Plugin
{
    /// <summary>
    /// StudentAge Harness 插件入口。只有在 sah run 准备的隔离目录里才会启用，装在游戏自己的 BepInEx 里什么也不做。
    /// 启用后：先装隔离（存档、偏好、Steam），再在第一帧加载场景包并开始跑场景，跑完写报告并退出游戏。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, HarnessVersion.Value)]
    public sealed class HarnessPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.studentage.harness";
        public const string PluginName = "StudentAge Harness";

        private void Awake()
        {
            RunSettings settings = RunGate.TryActivate(out string reason);
            if (settings == null)
            {
                Logger.LogWarning(reason);
                return;
            }
            try
            {
                Activate(settings);
            }
            catch (Exception ex)
            {
                Logger.LogError("启用失败：" + ex);
                WriteJson(settings, "activation-error.json", new JObject
                {
                    ["runId"] = settings.RunId,
                    ["error"] = ex.ToString()
                });
                if (HarnessRuntime.Report != null) HarnessRuntime.FinalizeAndQuit("activation-failed");
                else Application.Quit();
            }
        }

        private void Activate(RunSettings settings)
        {
            // 后台模式下窗口没有焦点，Unity 默认会暂停，协程和看门狗都会停住。
            Application.runInBackground = true;
            HarnessRuntime.Initialize(settings, new BepInExLogSink(Logger), Paths.GameRootPath, Application.persistentDataPath);

            var harmony = new Harmony(PluginGuid);
            ProfileIsolation.Install(harmony, settings);
            SteamGuard.Install(harmony);
            IsolationAudit audit = IsolationAudit.Begin(Application.persistentDataPath);
            var toasts = new ToastRecorder();
            toasts.Install(harmony);
            harmony.Patch(AccessTools.Method(typeof(UIMgr), nameof(UIMgr.Init)),
                postfix: new HarmonyMethod(typeof(HarnessPlugin), nameof(AfterUiInit)));
            AppDomain.CurrentDomain.AssemblyResolve += PackLoader.Resolve;

            var host = new StudentAgeHost(settings, new GameWindow(settings.RunDirectory, settings.IsBackground, settings.Mute),
                toasts, audit);
            Bootstrap.Schedule(() => HarnessRuntime.Start(host, PackLoader.Load(settings, HarnessRuntime.Report)));

            WriteJson(settings, "harness-ready.json", new JObject
            {
                ["runId"] = settings.RunId,
                ["harnessVersion"] = HarnessVersion.Value,
                ["processId"] = Process.GetCurrentProcess().Id,
                ["activatedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["saveRoot"] = ProfileIsolation.SaveRoot
            });
            Logger.LogInfo("已启用（运行 " + settings.RunId + "）：存档写入 " + ProfileIsolation.SaveRoot +
                "，游戏偏好只存在内存里，Steam 成就/统计/创意工坊写入已拦截。");
        }

        private static void AfterUiInit()
        {
            Bootstrap.RunPending();
            HarnessRuntime.Ensure();
        }

        private static void WriteJson(RunSettings settings, string fileName, JObject json)
        {
            try
            {
                HarnessRuntime.Redactor.Apply(json);
                File.WriteAllText(Path.Combine(settings.RunDirectory, fileName), json.ToString(Formatting.Indented),
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }
}
