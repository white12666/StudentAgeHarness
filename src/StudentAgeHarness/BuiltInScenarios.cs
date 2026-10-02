using System.Collections.Generic;

namespace StudentAgeHarness.Plugin
{
    [HarnessScenario("startup",
        Description = "游戏能启动到主菜单，被测插件已加载，主菜单按钮可以点。",
        Tags = new[] { "builtin", "smoke" }, Order = -100, TimeoutSec = 120)]
    internal sealed class StartupScenario : IHarnessScenario
    {
        public IEnumerable<HarnessStep> Build(HarnessContext ctx)
        {
            yield return HarnessStep.Routine("enter_main_menu", () => ctx.Game.EnterMainMenu(60f), 70f)
                .WithCapture(0.5f);
            yield return HarnessStep.Do("main_menu_clickable", () =>
                ctx.Assert(ctx.Ui.ClickableTargets().Count > 0, "主菜单上没有任何可以点击的按钮。"));
        }
    }

    [HarnessScenario("new-game",
        Description = "用测试角色开一局新游戏，进入主界面后回到主菜单。",
        Tags = new[] { "builtin", "smoke" }, Order = -90, TimeoutSec = 300)]
    internal sealed class NewGameScenario : IHarnessScenario
    {
        public IEnumerable<HarnessStep> Build(HarnessContext ctx)
        {
            yield return HarnessStep.Routine("start_new_game", () => ctx.Game.StartNewGame(150f), 160f)
                .Then(() => ctx.Game.IsInGame(), 10f, "新游戏没有停在主界面。")
                .WithCapture(1f);
            yield return HarnessStep.Routine("back_to_main_menu", () => ctx.Game.BackToMainMenu(90f), 100f)
                .WithCapture(0.5f);
        }
    }

    [HarnessScenario("ui-dump",
        Description = "导出主菜单和新游戏主界面的界面结构（ui-dump 目录）和截图，写场景时用来查按钮文字和节点路径。",
        Tags = new[] { "builtin", "manual" }, TimeoutSec = 300)]
    internal sealed class UiDumpScenario : IHarnessScenario
    {
        public IEnumerable<HarnessStep> Build(HarnessContext ctx)
        {
            yield return HarnessStep.Routine("enter_main_menu", () => ctx.Game.EnterMainMenu(60f), 70f);
            yield return HarnessStep.Do("dump_main_menu", () => ctx.DumpUi("main-menu")).WithCapture(0.3f);
            yield return HarnessStep.Routine("start_new_game", () => ctx.Game.StartNewGame(150f), 160f);
            yield return HarnessStep.Do("dump_main_view", () => ctx.DumpUi("main-view")).WithCapture(0.3f);
        }
    }
}
