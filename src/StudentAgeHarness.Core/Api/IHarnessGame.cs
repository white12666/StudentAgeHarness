using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace StudentAgeHarness
{
    /// <summary>
    /// 游戏层的常用操作，由适配插件实现。场景包只依赖这个接口就能完成进主菜单、开新局、点界面按钮等操作，
    /// 不需要直接引用游戏程序集。返回 IEnumerator 的方法要放进步骤的 ActRoutine 里执行，失败时抛异常。
    /// </summary>
    public interface IHarnessGame
    {
        /// <summary>游戏版本号（Application.version）。</summary>
        string GameVersion { get; }

        /// <summary>主菜单视图已打开、配置已加载完。启动阶段和场景之间都用它判断游戏是否就绪。</summary>
        bool IsStartupReady();

        /// <summary>主菜单按钮可以点：已经越过"点击任意位置继续"，并且上面没有盖着别的界面。</summary>
        bool IsAtMainMenu();

        /// <summary>等主菜单就绪，必要时点掉开场的"点击任意位置继续"，直到主菜单按钮可点。</summary>
        IEnumerator EnterMainMenu(float timeoutSec = 30f);

        /// <summary>是否处于一局游戏中。</summary>
        bool IsInGame();

        /// <summary>
        /// 用固定的测试角色开一局新游戏（关闭引导），等到主界面出现，并点掉开场的剧情和提示。
        /// 这是准备测试状态的捷径，不会经过创建角色界面。
        /// </summary>
        IEnumerator StartNewGame(float timeoutSec = 90f);

        /// <summary>回到主菜单并等它可以操作。</summary>
        IEnumerator BackToMainMenu(float timeoutSec = 60f);

        /// <summary>
        /// 依次点掉挡在主界面前面的引导气泡、剧情对话和单按钮提示。遇到需要做选择的对话时，
        /// chooseFirstOption 为 true 就选第一个选项，否则停下。多按钮的确认框不会被点掉。
        /// </summary>
        IEnumerator DismissDialogs(int maxCount = 30, bool chooseFirstOption = true);

        /// <summary>最上层视图的完整类型名，例如 View.Main.MainView。</summary>
        string TopViewName();

        /// <summary>当前打开着的视图（完整类型名）。</summary>
        IList<string> OpenViewNames();

        /// <summary>视图是否已打开。可以写完整类型名，也可以只写类名（例如 SettingView）。</summary>
        bool IsViewOpen(string viewName);

        /// <summary>视图的根 GameObject，没打开时返回 null。</summary>
        GameObject GetViewRoot(string viewName);

        GameObject TopViewRoot();

        /// <summary>
        /// 点击视图上的一个按钮字段（例如 EntryView 的 btn_option）。会等按钮出现、可交互、
        /// 并且没有被别的界面挡住，然后发送一次真实的指针点击事件。
        /// </summary>
        IEnumerator ClickViewButton(string viewName, string buttonField, float timeoutSec = 5f);

        /// <summary>直接关闭一个已打开的视图（相当于界面上的关闭按钮）。没打开时返回 false。</summary>
        bool CloseView(string viewName);

        /// <summary>本次运行中游戏弹过的 Toast 文本（按时间顺序）。</summary>
        IList<string> Toasts { get; }

        void ClearToasts();

        bool AnyToastContains(string text);

        /// <summary>屏幕上的 Toast 已经全部消失。</summary>
        bool ToastsDrained();

        /// <summary>本次运行的本地 Mod 目录（profile/Mods）。游戏常用资源路径 API 已重定向到这里。</summary>
        string LocalModsDirectory { get; }
    }
}
