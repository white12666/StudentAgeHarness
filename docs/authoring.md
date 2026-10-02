# 编写场景包

场景包是 `netstandard2.0` 类库，不是 BepInEx 插件。每个场景使用公开无参构造函数，标注 `[HarnessScenario]`，实现 `IHarnessScenario.Build`。

## 最小例子

```csharp
using System.Collections.Generic;
using StudentAgeHarness;

[HarnessScenario("my-settings",
    Description = "打开并关闭设置界面",
    Tags = new[] { "smoke" },
    TimeoutSec = 60)]
public sealed class MySettings : IHarnessScenario
{
    public IEnumerable<HarnessStep> Build(HarnessContext ctx)
    {
        yield return HarnessStep.Routine("menu", () => ctx.Game.EnterMainMenu());
        yield return HarnessStep.Routine("open",
            () => ctx.Game.ClickViewButton("EntryView", "btn_option"))
            .Then(() => ctx.Game.IsViewOpen("SettingView"), 10)
            .WithCapture(0.5f);
        yield return HarnessStep.Routine("close",
            () => ctx.Game.ClickViewButton("SettingView", "btn_cancel"))
            .Then(() => !ctx.Game.IsViewOpen("SettingView"), 10);
    }
}
```

仓库内可参照 `samples/ExamplePack/ExamplePack.csproj`。独立 Mod 项目引用发布包：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <HarnessHome>$(STUDENTAGE_HARNESS_HOME)</HarnessHome>
    <GameRoot>$(STUDENTAGE_GAME_ROOT)</GameRoot>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="StudentAgeHarness.Core">
      <HintPath>$(HarnessHome)\plugin\StudentAgeHarness.Core.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <!-- 操作 GameObject、真实键鼠时按需添加同游戏版本的 Unity 引用。 -->
    <Reference Include="UnityEngine.CoreModule">
      <HintPath>$(GameRoot)\StudentAge_Data\Managed\UnityEngine.CoreModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

在当前终端设置这两个环境变量，编译后把场景 DLL 填入 `harness.json` 的 `packs`，然后用 `scenarios: ["my-settings"]` 运行。使用 `ctx.Input` 时还需引用游戏自带的 `Unity.InputSystem.dll`。操作 Mod 自己的类型时引用对应程序集，并在 `plugins/dependencies` 中提供同一版本。

## 场景声明

- `RequiresPlugins = new[] { "com.example.mymod" }`：依赖 GUID，不是 DLL 名字。直接点名场景缺依赖会失败；通过标签或 `*` 带入时记录跳过。
- `RequiresFocus = true`：后台模式不运行。P0 不替你强制抢焦点。
- `RunAlone = true`：混选时报告配置错误，仅保留该场景。
- `Tags = new[] { "manual" }`：`*` 默认不选，可直接点名或使用显式标签。
- `Order`：同一个包中的顺序。
- `TimeoutSec`：该场景时间上限。每次测试都是新进程，场景之间只恢复主菜单，**不重置静态字段或配置表**。有进程级污染的测试应单独运行。

## 步骤与断言

顺序是 `SkipWhen → Pre → Act → ActRoutine → Post → 截图`：

- `Do(name, Action)`：同步动作/断言。
- `Routine(name, Func<IEnumerator>, timeoutSec)`：跨帧操作，支持嵌套协程。超时或异常会释放协程的 `finally`。
- `WaitUntil` / `After` / `Then`：轮询条件；超时携带最后一次求值异常。
- `WithCapture` / `ctx.Capture`：成功截图；失败自动截图。
- `ctx.Assert` / `ctx.Fail`：让步骤失败。`Build` 只描述步骤，不能直接操作游戏；空场景报错。
- `Cleanup()`：前面的步骤失败也执行。`Soft()`：继续后续步骤，finding 降到 warning，但失败步骤仍保留，不能当作通过。
- `SkipIf(..., allowed: true)`：预期分支记 `skipped-allowed`。非预期跳过影响结果。
- `ExpectingModal()`：这一步明确预期确认框；不是忽略所有游戏异常。

不要用固定睡眠代替可验证的结果。一次操作后至少断言可观察状态变化，不能只检查代码“执行到这里了”。

## UI 与输入

`ctx.Ui` 用文字、组件和射线检测操作 UGUI：

- `HasText` / `HasTextContaining`、`ClickText` / `ClickTextContaining`。
- `IsReachable` / `ClickWhenReachable`；遮挡时拒绝点穿。
- `VisibleTexts`、`ClickableTargets`、`ctx.DumpUi("label")` 帮助查找控件。
- `TypeIntoInputByLabel` / `TypeIntoInputByPlaceholder` 使用输入框键盘事件，支持文档中列明的 ASCII 字符，不模拟中文输入法。

**`ctx.Ui` 的点击是 EventSystem 指针事件，不是物理鼠标输入。** 若要测悬停、拖拽或快捷键，使用 `ctx.Input.Click/Drag/Hover/Scroll/Press/Chord`：临时虚拟 InputSystem 设备，操作后恢复设备状态，并连接游戏缓存的 Control 键鼠设备。

`ctx.Game` 封装进主菜单、开新局、回菜单、视图查询、按按钮字段点击、Toast 捕获。`StartNewGame` 是状态准备捷径，**不覆盖玩家创建角色的界面流程**。`DismissDialogs` 可按参数选第一个剧情选项，不替你确认双按钮对话框。

布局检查：`ctx.LintLayout(root, label)`。测原版界面时可传 `HarnessSeverity.Info` 只留档，避免把原版布局情况归为自己的 Mod 回归。

## 夹具和报告扩展

`ctx.Fixtures.CreateDirectory(name)` 在 `run/fixtures` 中创建证据目录；`CreateLocalMod(name)` 在 `run/profile/Mods` 创建临时工程，正常退出时清理。`Keep(path)` 保留本次夹具供检查。不会扫描或删除玩家真实 Mods。

继承 `HarnessExtension` 可实现：

- `OnRunStart`：游戏就绪时初始化。
- `TryRecover`：场景之间轮询恢复，必须可重复调用。
- `DescribeUnexpectedModal`：报告 Mod 自己的阻塞弹窗。
- `OnRunEnd`：`report.SetExtension("mymod.save-check", yourData)`。

扩展键带自己的前缀。值应是普通 JSON 数据，不要直接序列化 Unity 对象，不要写 Steam 身份或玩家正文内容。场景失败的证据属于测试产物，不应直接发布。
