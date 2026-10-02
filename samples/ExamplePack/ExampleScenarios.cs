using System.Collections.Generic;
using StudentAgeHarness;
using UnityEngine.InputSystem;

namespace ExamplePack
{
    /// <summary>主菜单：按钮都能点到，布局检查只留档（原版界面的问题不算你的 mod 失败）。</summary>
    [HarnessScenario("sample-main-menu",
        Description = "主菜单的按钮可以点击，主菜单布局检查结果留档。",
        Tags = new[] { "sample" })]
    public sealed class MainMenuScenario : IHarnessScenario
    {
        public IEnumerable<HarnessStep> Build(HarnessContext ctx)
        {
            yield return HarnessStep.Routine("enter_main_menu", () => ctx.Game.EnterMainMenu());

            yield return HarnessStep.Do("menu_buttons_reachable", () =>
            {
                int count = ctx.Ui.ClickableTargets().Count;
                ctx.Assert(count >= 3, "主菜单上能点到的按钮只有 " + count + " 个。可见文字：" + ctx.Ui.VisibleTextSummary());
            });

            yield return HarnessStep.Do("lint_main_menu", () =>
            {
                int findings = ctx.LintLayout(ctx.Game.GetViewRoot("EntryView"), "EntryView", HarnessSeverity.Info);
                ctx.Log("主菜单布局检查：" + findings + " 条（只留档）。");
            }).WithCapture(0.2f);
        }
    }

    /// <summary>通过按钮字段打开设置界面，再点取消关闭。ClickViewButton 会等按钮可点、没被挡住才点。</summary>
    [HarnessScenario("sample-settings",
        Description = "从主菜单打开设置界面，截图后点取消关闭。",
        Tags = new[] { "sample" })]
    public sealed class SettingsScenario : IHarnessScenario
    {
        public IEnumerable<HarnessStep> Build(HarnessContext ctx)
        {
            yield return HarnessStep.Routine("enter_main_menu", () => ctx.Game.EnterMainMenu());

            yield return HarnessStep.Routine("open_settings", () => ctx.Game.ClickViewButton("EntryView", "btn_option"))
                .Then(() => ctx.Game.IsViewOpen("SettingView"), 10f, "点了设置按钮，设置界面没有打开。")
                .WithCapture(0.5f);

            yield return HarnessStep.Routine("cancel_settings", () => ctx.Game.ClickViewButton("SettingView", "btn_cancel"))
                .Then(() => !ctx.Game.IsViewOpen("SettingView"), 10f, "点了取消，设置界面没有关闭。");
        }
    }

    /// <summary>开一局新游戏，用真实键盘（虚拟设备）按 Esc 打开暂停菜单，再按一次关闭。</summary>
    [HarnessScenario("sample-new-game",
        Description = "开新游戏后用键盘 Esc 打开并关闭暂停菜单。",
        Tags = new[] { "sample", "slow" }, TimeoutSec = 300)]
    public sealed class NewGameScenario : IHarnessScenario
    {
        public IEnumerable<HarnessStep> Build(HarnessContext ctx)
        {
            yield return HarnessStep.Routine("start_new_game", () => ctx.Game.StartNewGame(), 160f)
                .WithCapture(1f);

            yield return HarnessStep.Routine("open_pause_menu", () => ctx.Input.Press(Key.Escape))
                .Then(() => ctx.Game.IsViewOpen("EntryView"), 10f, "按 Esc 后没有出现暂停菜单。")
                .WithCapture(0.5f);

            yield return HarnessStep.Routine("close_pause_menu", () => ctx.Input.Press(Key.Escape))
                .Then(() => !ctx.Game.IsViewOpen("EntryView"), 10f, "再按 Esc 后暂停菜单没有关闭。");
        }
    }

    /// <summary>扩展参与整次运行：这里只是把 Toast 数量写进报告的 extensions 节点。</summary>
    public sealed class ExampleExtension : HarnessExtension
    {
        private HarnessRunContext run;

        public override void OnRunStart(HarnessRunContext context)
        {
            run = context;
        }

        public override void OnRunEnd(HarnessReport report)
        {
            if (run == null) return;
            report.SetExtension("example.summary", new Dictionary<string, object>
            {
                ["toasts"] = run.Game.Toasts.Count
            });
        }
    }
}
