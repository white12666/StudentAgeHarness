using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Config;
using Sdk;
using StudentAgeHarness.Engine;
using UnityEngine;
using UnityEngine.UI;
using View.Evt;
using View.Guide;
using View.Hint;
using View.Main;
using GameApi = global::Game;

namespace StudentAgeHarness.Plugin.Adapter
{
    /// <summary>学生时代的 <see cref="IHarnessGame"/> 实现。界面点击一律走射线检查过的指针事件。</summary>
    internal sealed class StudentAgeGame : IHarnessGame
    {
        private const float PollSec = 0.25f;
        private const float IdleSettleSec = 1.5f;

        private readonly ToastRecorder toasts;

        internal StudentAgeGame(ToastRecorder toasts)
        {
            this.toasts = toasts;
        }

        public string GameVersion => Application.version;

        public string LocalModsDirectory => Isolation.ProfileIsolation.LocalModsRoot;

        public IList<string> Toasts => toasts.Messages;

        public void ClearToasts() => toasts.Clear();

        public bool AnyToastContains(string text) => toasts.AnyContains(text);

        public bool ToastsDrained()
        {
            BaseView toast = Views.Find("View.Common.ToastView");
            return toast == null || (!Views.IsOpened(toast) && !Views.IsOpening(toast));
        }

        // ======================== 状态 ========================

        public bool IsStartupReady()
        {
            try
            {
                if (!UIMgr.IsViewOpened<EntryView>()) return false;
                return Singleton<ModCtrl>.Ins != null && Cfg.ActionCfgMap != null && Cfg.ActionCfgMap.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        public bool IsAtMainMenu()
        {
            try
            {
                // LoadingView is a Hover view and is omitted by UIMgr.GetTopView,
                // but its full-screen transition still covers an otherwise ready menu.
                if (GameApi.GetGameState() != GameState.Start || IsLoading()) return false;
                EntryView entry = UIMgr.GetOpeningView<EntryView>();
                if (entry == null || !entry.isViewReady || entry.gameObject == null) return false;
                if (entry.btn_start != null && entry.btn_start.gameObject.activeInHierarchy) return false;
                if (entry.group_content == null || !entry.group_content.gameObject.activeInHierarchy) return false;
                if (entry.canvasgroup_content != null && entry.canvasgroup_content.alpha < 0.99f) return false;
                return ReferenceEquals(Views.Top(), entry);
            }
            catch
            {
                return false;
            }
        }

        public bool IsInGame()
        {
            try
            {
                return GameApi.GetGameState() == GameState.Running && UIMgr.IsViewOpened<MainView>();
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsLoading()
        {
            try
            {
                return GameApi.GetGameState() == GameState.Loading || UIMgr.IsViewOpeningOrOpened<LoadingView>();
            }
            catch
            {
                return true;
            }
        }

        // ======================== 流程 ========================

        public IEnumerator EnterMainMenu(float timeoutSec = 30f)
        {
            float deadline = Time.realtimeSinceStartup + Math.Max(1f, timeoutSec);
            while (!IsAtMainMenu())
            {
                if (Time.realtimeSinceStartup > deadline)
                    throw new InvalidOperationException("等待主菜单超时（" + timeoutSec + " 秒）。最上层视图：" + TopViewName() +
                        "；可见文字：" + HarnessUi.Instance.VisibleTextSummary(20));
                EntryView entry = SafeOpening<EntryView>();
                if (entry != null && entry.btn_start != null && entry.btn_start.gameObject.activeInHierarchy)
                    HarnessUi.Instance.TryClick(entry.btn_start.btn);
                else
                    TryDismissOne(false, out _);
                yield return new WaitForSecondsRealtime(PollSec);
            }
        }

        public IEnumerator StartNewGame(float timeoutSec = 90f)
        {
            float deadline = Time.realtimeSinceStartup + Math.Max(10f, timeoutSec);
            yield return EnterMainMenu(Math.Min(30f, timeoutSec));

            NewGameData data = BuildNewGameData();
            UIMgr.CloseView<EntryView>();
            GameApi.NewGame(data);
            HarnessLog.Info("已用测试角色开始新游戏（关闭引导）。");

            while (!(IsInGame() && !IsLoading()))
            {
                if (Time.realtimeSinceStartup > deadline)
                    throw new InvalidOperationException("新游戏在 " + timeoutSec + " 秒内没有进入主界面。最上层视图：" + TopViewName());
                yield return new WaitForSecondsRealtime(PollSec);
            }

            while (true)
            {
                if (Time.realtimeSinceStartup > deadline)
                    throw new InvalidOperationException("新游戏开场的对话和提示在 " + timeoutSec + " 秒内没有处理完。最上层视图：" +
                        TopViewName() + "；可见文字：" + HarnessUi.Instance.VisibleTextSummary(20));
                yield return DismissDialogs(60, true);
                if (IsInGame() && !IsLoading() && Views.Top() is MainView) break;
                yield return new WaitForSecondsRealtime(PollSec);
            }
        }

        public IEnumerator BackToMainMenu(float timeoutSec = 60f)
        {
            if (!IsAtMainMenu())
            {
                GameApi.BackToMain();
                yield return new WaitForSecondsRealtime(0.5f);
            }
            yield return EnterMainMenu(timeoutSec);
        }

        public IEnumerator DismissDialogs(int maxCount = 30, bool chooseFirstOption = true)
        {
            int actions = 0;
            float idleSince = Time.realtimeSinceStartup;
            float deadline = Time.realtimeSinceStartup + Math.Max(15f, maxCount * 4f);
            while (actions < maxCount && Time.realtimeSinceStartup < deadline)
            {
                DismissResult result = TryDismissOne(chooseFirstOption, out string what);
                if (result == DismissResult.Acted)
                {
                    actions++;
                    idleSince = Time.realtimeSinceStartup;
                    HarnessLog.Info("点掉了 " + what);
                    yield return new WaitForSecondsRealtime(0.35f);
                    continue;
                }
                if (result == DismissResult.NeedsDecision) yield break;
                if (result == DismissResult.Busy) idleSince = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - idleSince >= IdleSettleSec) yield break;
                yield return new WaitForSecondsRealtime(0.2f);
            }
        }

        private enum DismissResult
        {
            Idle,
            Busy,
            Acted,
            NeedsDecision
        }

        private DismissResult TryDismissOne(bool chooseFirstOption, out string description)
        {
            description = null;
            if (IsLoading()) return DismissResult.Busy;
            BaseView top = Views.Top();
            if (top == null) return DismissResult.Idle;
            if (!Views.IsOpened(top) || !top.isViewReady) return DismissResult.Busy;
            description = top.Name;

            switch (top)
            {
                case MainView _:
                case EntryView _:
                    return DismissResult.Idle;
                case GuideView guide:
                    return Click(guide.btn_talk) ? DismissResult.Acted : DismissResult.Busy;
                case GuideImgView guideImage:
                    return Click(guideImage.btn_close) ? DismissResult.Acted : DismissResult.Busy;
                case NewTalkView talk:
                    return AdvanceTalk(talk, chooseFirstOption, ref description);
                case CommonComfirmView confirm:
                    // 有"取消"按钮的确认框需要真正的决定，交给场景处理；只有一个按钮时就是知道了。
                    if (confirm.btn_cancel != null && confirm.btn_cancel.gameObject.activeInHierarchy)
                        return DismissResult.NeedsDecision;
                    return Click(confirm.btn_ok) ? DismissResult.Acted : DismissResult.Busy;
            }

            if (top.Name == "View.Common.NewRoundView") return DismissResult.Busy;
            if (top.Name.StartsWith("View.Hint.", StringComparison.Ordinal))
            {
                Button[] buttons = top.gameObject.GetComponentsInChildren<Button>()
                    .Where(button => button.IsActive() && button.IsInteractable()).ToArray();
                if (buttons.Length != 1) return DismissResult.NeedsDecision;
                return HarnessUi.Instance.TryClick(buttons[0]) ? DismissResult.Acted : DismissResult.Busy;
            }
            return DismissResult.Idle;
        }

        private static DismissResult AdvanceTalk(NewTalkView talk, bool chooseFirstOption, ref string description)
        {
            List<GameObject> options = ActiveCells(talk.itemgroup_options).Concat(ActiveCells(talk.itemgroup_evt_options)).ToList();
            if (options.Count > 0)
            {
                if (!chooseFirstOption) return DismissResult.NeedsDecision;
                Button option = options.Select(cell => cell.GetComponentsInChildren<Button>()
                        .FirstOrDefault(button => button.IsActive() && button.IsInteractable()))
                    .FirstOrDefault(button => button != null);
                if (option == null) return DismissResult.Busy;
                description += "（选择了第一个选项）";
                return HarnessUi.Instance.TryClick(option) ? DismissResult.Acted : DismissResult.Busy;
            }
            UIButton next = talk.btn_evt != null && talk.btn_evt.gameObject.activeInHierarchy ? talk.btn_evt : talk.btn_click;
            // 文字逐字出现时按钮不可交互，等它打完。
            if (next == null || !next.btn.IsInteractable()) return DismissResult.Busy;
            return Click(next) ? DismissResult.Acted : DismissResult.Busy;
        }

        private static IEnumerable<GameObject> ActiveCells(UIItemGroup group)
        {
            if (group == null) return Enumerable.Empty<GameObject>();
            List<UICell> cells;
            try { cells = group.GetCells(); }
            catch { return Enumerable.Empty<GameObject>(); }
            return (cells ?? new List<UICell>()).Where(cell => cell != null && cell.gameObject != null &&
                cell.gameObject.activeInHierarchy).Select(cell => cell.gameObject).ToList();
        }

        private static bool Click(UIButton button)
        {
            return button != null && button.btn != null && HarnessUi.Instance.TryClick(button.btn);
        }

        private static T SafeOpening<T>() where T : class
        {
            try { return UIMgr.GetOpeningView<T>(); }
            catch { return null; }
        }

        private static NewGameData BuildNewGameData()
        {
            var attributes = new Dictionary<int, (int type, float value)>();
            foreach (TalentCfg talent in Cfg.TalentCfgMap.Values) attributes[talent.attrId] = (talent.type, talent.value);
            return new NewGameData
            {
                xing = "测",
                ming = "试",
                gameMode = GameMode.GuideOff,
                isMale = true,
                mainPersonalitys = Cfg.PersonalityTypeCfgMap.Values
                    .First(personality => personality.replace == null || personality.replace.Count == 0).ids.ToList(),
                attrs = attributes,
                birthday = new List<int> { 1995, 1, 1 },
                diff = Cfg.GameDiffCfgMap.Keys.Min()
            };
        }

        // ======================== 视图 ========================

        public string TopViewName()
        {
            return Views.Top()?.Name ?? "(无)";
        }

        public IList<string> OpenViewNames()
        {
            return Views.OpenedNames();
        }

        public bool IsViewOpen(string viewName)
        {
            return Views.IsOpened(Views.Find(viewName));
        }

        public GameObject GetViewRoot(string viewName)
        {
            BaseView view = Views.Find(viewName);
            return Views.IsOpened(view) ? view.gameObject : null;
        }

        public GameObject TopViewRoot()
        {
            BaseView top = Views.Top();
            return Views.IsOpened(top) ? top.gameObject : null;
        }

        public IEnumerator ClickViewButton(string viewName, string buttonField, float timeoutSec = 5f)
        {
            float deadline = Time.realtimeSinceStartup + Math.Max(0.5f, timeoutSec);
            BaseView view = Views.Find(viewName);
            while (!Views.IsOpened(view) || !view.isViewReady)
            {
                if (Time.realtimeSinceStartup > deadline)
                    throw new InvalidOperationException("视图 " + viewName + " 没有打开。当前打开的视图：" +
                        string.Join(", ", OpenViewNames().ToArray()));
                yield return null;
                view = Views.Find(viewName);
            }
            Component target = ResolveButton(view, buttonField);
            float remaining = Math.Max(0.5f, deadline - Time.realtimeSinceStartup);
            yield return HarnessUi.Instance.ClickWhenReachable(target, remaining);
        }

        public bool CloseView(string viewName)
        {
            BaseView view = Views.Find(viewName);
            if (!Views.IsOpened(view)) return false;
            view.CloseView();
            return true;
        }

        private static Component ResolveButton(BaseView view, string fieldName)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            object value = null;
            for (Type type = view.GetType(); type != null && value == null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(fieldName, flags | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    value = field.GetValue(view);
                    break;
                }
                PropertyInfo property = type.GetProperty(fieldName, flags | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    value = property.GetValue(view, null);
                    break;
                }
            }
            switch (value)
            {
                case UIButton uiButton when uiButton.btn != null:
                    return uiButton.btn;
                case Component component:
                    return component;
                case GameObject gameObject:
                    return gameObject.transform;
                case null:
                    throw new InvalidOperationException("视图 " + view.Name + " 上没有按钮字段 " + fieldName + "（或者它还没初始化）。");
                default:
                    throw new InvalidOperationException("视图 " + view.Name + " 的字段 " + fieldName + " 不是按钮，而是 " +
                        value.GetType().FullName + "。");
            }
        }
    }
}
