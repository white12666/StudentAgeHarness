using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sdk;
using StudentAgeHarness.Engine;
using StudentAgeHarness.Plugin.Isolation;
using UnityEngine;
using View.Hint;
using View.Main;
using GameApi = global::Game;

namespace StudentAgeHarness.Plugin.Adapter
{
    /// <summary>学生时代的 <see cref="IHarnessHostAdapter"/>：启动检查、场景之间回主菜单、弹窗识别、环境和隔离信息。</summary>
    internal sealed class StudentAgeHost : IHarnessHostAdapter
    {
        private const float BackToMainRetrySec = 15f;
        private static readonly MethodInfo CancelConfirm = AccessTools.Method(typeof(CommonComfirmView), "OnClickCancel");

        private readonly RunSettings settings;
        private readonly StudentAgeGame game;
        private readonly ControlInputBinding input = new ControlInputBinding();
        private readonly GameWindow window;
        private readonly ToastRecorder toasts;
        private readonly IsolationAudit audit;
        private readonly List<PluginInfo> pluginsUnderTest = new List<PluginInfo>();
        private float lastBackToMain = -1000f;

        internal StudentAgeHost(RunSettings settings, GameWindow window, ToastRecorder toasts, IsolationAudit audit)
        {
            this.settings = settings;
            this.window = window;
            this.toasts = toasts;
            this.audit = audit;
            game = new StudentAgeGame(toasts);
        }

        public IHarnessGame Game => game;

        public IHarnessInputBinding InputBinding => input;

        internal IList<PluginInfo> PluginsUnderTest => pluginsUnderTest;

        public bool IsStartupReady()
        {
            return game.IsStartupReady();
        }

        public void OnStartupReady(HarnessReport report)
        {
            var entries = new JArray();
            List<PluginInfo> loaded = Chainloader.PluginInfos.Values.Where(info => info != null).ToList();
            foreach (PluginUnderTest item in settings.PluginsUnderTest)
            {
                string full = Path.GetFullPath(Path.Combine(settings.RunDirectory, item.Path));
                List<PluginInfo> matches = loaded.Where(info => !string.IsNullOrEmpty(info.Location) &&
                    (item.IsFolder ? RunGate.IsUnder(info.Location, full) : RunGate.SamePath(Path.GetFullPath(info.Location), full))).ToList();
                pluginsUnderTest.AddRange(matches);
                entries.Add(new JObject
                {
                    ["name"] = item.Name,
                    ["path"] = item.Path,
                    ["kind"] = item.IsFolder ? "folder" : "file",
                    ["loaded"] = new JArray(matches.Select(Describe))
                });
                if (matches.Count == 0)
                    report.AddFinding(HarnessSeverity.Error, "harness", "plugin-not-loaded",
                        "被测插件 " + item.Name + " 没有被 BepInEx 加载。常见原因：缺少依赖插件、BepInPlugin 特性写错、" +
                        "目标框架不兼容。具体原因请看运行目录里的 BepInEx/LogOutput.log。", item.Path);
            }
            report.Environment["pluginsUnderTest"] = entries;

            foreach (string guid in settings.ExpectPlugins)
                if (!IsPluginLoaded(guid))
                    report.AddFinding(HarnessSeverity.Error, "harness", "expected-plugin-missing",
                        "配置里要求加载的插件 " + guid + " 没有加载。", guid);
            if (!toasts.Installed)
                report.AddFinding(HarnessSeverity.Warning, "harness", "toast-recorder-unavailable",
                    "无法记录 Toast（" + toasts.InstallError + "），ctx.Game.Toasts 会一直是空的。");
        }

        public bool TryRecoverToBaseline()
        {
            if (StudentAgeGame.IsLoading()) return false;

            // 确认框只点"取消"，恢复流程绝不替场景做决定。
            BaseView confirm = Views.Find("View.Hint.CommonComfirmView");
            if (Views.IsOpening(confirm)) return false;
            if (Views.IsOpened(confirm))
            {
                if (confirm.isViewReady && CancelConfirm != null)
                {
                    try { CancelConfirm.Invoke(confirm, null); }
                    catch (Exception ex) { HarnessLog.Warning("恢复时取消确认框出错：" + ex.Message); }
                }
                return false;
            }

            if (GameApi.GetGameState() == GameState.Running)
            {
                if (Time.realtimeSinceStartup - lastBackToMain > BackToMainRetrySec)
                {
                    lastBackToMain = Time.realtimeSinceStartup;
                    HarnessLog.Info("恢复：从游戏中回到主菜单。");
                    GameApi.BackToMain();
                }
                return false;
            }

            bool busy = false;
            foreach (BaseView view in Views.All())
            {
                if (view is EntryView || view.viewType == ViewType.Hover) continue;
                if (Views.IsOpening(view))
                {
                    busy = true;
                    continue;
                }
                if (!Views.IsOpened(view)) continue;
                busy = true;
                if (!view.isViewReady) continue;
                try { view.CloseView(); }
                catch (Exception ex) { HarnessLog.Warning("恢复时关闭视图 " + view.Name + " 出错：" + ex.Message); }
            }
            if (busy) return false;

            if (!UIMgr.IsViewOpeningOrOpened<EntryView>())
            {
                UIMgr.OpenView<EntryView>(UILayerType.None, null, Array.Empty<object>());
                return false;
            }
            EntryView entry = UIMgr.GetOpeningView<EntryView>();
            if (entry != null && entry.btn_start != null && entry.btn_start.gameObject.activeInHierarchy)
            {
                HarnessUi.Instance.TryClick(entry.btn_start.btn);
                return false;
            }
            return game.IsAtMainMenu() && game.ToastsDrained();
        }

        public string DescribeUnexpectedModal()
        {
            BaseView confirm = Views.Find("View.Hint.CommonComfirmView");
            if (!Views.IsOpened(confirm) && !Views.IsOpening(confirm)) return null;
            string text = null;
            try
            {
                if (confirm is CommonComfirmView typed && typed.txtex_desc != null)
                    text = HarnessUi.Normalize(HarnessUi.StripRichText(typed.txtex_desc.text));
            }
            catch
            {
            }
            if (!string.IsNullOrEmpty(text) && text.Length > 80) text = text.Substring(0, 80) + "…";
            return "确认框 View.Hint.CommonComfirmView" + (string.IsNullOrEmpty(text) ? string.Empty : "（" + text + "）");
        }

        public bool IsKnownLogNoise(string condition)
        {
            // 原版按语言探测可选 DLC 配置表，键不存在时 Addressables 抛 InvalidKeyException，随后回退到基础表。
            return condition != null &&
                condition.IndexOf("InvalidKeyException", StringComparison.Ordinal) >= 0 &&
                condition.IndexOf("Key=assets/res/cfgs/dlc_", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public bool IsPluginLoaded(string guid)
        {
            return !string.IsNullOrEmpty(guid) && Chainloader.PluginInfos.ContainsKey(guid);
        }

        public void Tick()
        {
            window.Tick();
        }

        public void DescribeEnvironment(JObject environment)
        {
            var dlc = new JObject();
            foreach (DLC_IDX index in Enum.GetValues(typeof(DLC_IDX)))
            {
                try { dlc[index.ToString()] = Singleton<DLCCtrl>.Ins.IsDLCLoaded(index); }
                catch
                {
                }
            }
            string language = null;
            try { language = LocalizationMgr.Lang; }
            catch
            {
            }
            environment["game"] = new JObject
            {
                ["version"] = Application.version,
                ["unity"] = Application.unityVersion,
                ["language"] = language,
                ["dlcLoaded"] = dlc
            };
            environment["bepinex"] = new JObject
            {
                ["version"] = typeof(Chainloader).Assembly.GetName().Version?.ToString(),
                ["root"] = Paths.BepInExRootPath
            };
            environment["plugins"] = new JArray(Chainloader.PluginInfos.Values.Where(info => info != null)
                .OrderBy(info => info.Metadata.GUID, StringComparer.Ordinal).Select(Describe));
            environment["window"] = window.Describe();
            environment["toasts"] = new JObject { ["recorded"] = toasts.Total, ["recorderInstalled"] = toasts.Installed };
        }

        public void OnRunFinished(HarnessReport report)
        {
            ProfileIsolation.Describe(report.Isolation, report);
            SteamGuard.Describe(report.Isolation);
            try { audit.Complete(report.Isolation, report); }
            catch (Exception ex)
            {
                report.AddFinding(HarnessSeverity.Warning, "isolation", "isolation-audit-failed", "对比真实存档和本地 Mods 目录出错：" + ex.Message);
            }
            report.Isolation["bepinexRoot"] = Paths.BepInExRootPath;
            BepInExLogScan.AddFindings(report, pluginsUnderTest.Select(info => info.Metadata.Name));
        }

        private JObject Describe(PluginInfo info)
        {
            return new JObject
            {
                ["guid"] = info.Metadata.GUID,
                ["name"] = info.Metadata.Name,
                ["version"] = info.Metadata.Version?.ToString(),
                ["file"] = info.Location
            };
        }
    }
}
