# StudentAge Harness

给《学生时代》Mod 作者用的本地测试工具：**编译 Mod → 一条命令启动隔离游戏 → 看报告和截图**。

不需要改游戏插件目录，也不需要把测试场景编进自己的 Mod。没有自定义场景时，先用内置的启动和新游戏测试；需要检查具体功能时，再写一个独立场景包。

## 环境

- Windows 10/11、PowerShell 5.1 或更高版本。
- Steam 版学生时代，已安装 **BepInEx 5**。
- Steam 已正常启动并登录，游戏已退出；需要可渲染的桌面会话，不支持 `-batchmode -nographics`。
- 从源码构建需要 .NET 8 SDK。运行发布包不需要 SDK。

已实测的游戏/运行库版本和验证结果见 [验证记录](docs/validation.md)。新游戏版本可能需要更新适配层。

## 快速开始

解压本地生成的发布包，**不要将 harness DLL 安装到游戏的 BepInEx/plugins**。在你的 Mod 项目目录运行：

```powershell
$sah = 'C:\Tools\StudentAgeHarness\tools\sah.cmd' # 改成你的解压路径
& $sah init
```

把生成的 `harness.json` 改为你的构建产物：

```json
{
  "plugins": ["bin/Release/MyMod"],
  "scenarios": ["startup", "new-game"],
  "window": "visible",
  "resolution": "1920x1080"
}
```

游戏窗口默认正常显示：运行中可以切到别的窗口或把游戏最小化，测试照常进行，想看时点任务栏图标即可。不想看到游戏时把 `window` 改成 `background`（或命令行加 `-Background`），窗口会被藏到桌面下方，运行中调不出来。运行中请不要在游戏里点击或按键。

`plugins` 可以是一个 DLL，也可以是含 DLL、依赖和资源的目录。**只写 DLL 时不会猜测和复制其旁边的依赖 DLL**，有依赖时传整个目录，或填写 `dependencies`。不要把含 Unity、游戏、BepInEx 程序集的构建目录直接作为插件目录。

```powershell
# 先按自己项目的构建方式编译 Mod。
& $sah doctor
& $sah run
$LASTEXITCODE
```

不写配置也可以临时测试：

```powershell
& $sah run -Plugin '.\bin\Release\MyMod.dll' -Scenario startup,new-game
& $sah run -Background -Scenario startup
& $sah report -Open
```

所有配置里的相对路径以 **harness.json 所在目录** 为准；命令行路径以当前目录为准。启动器优先使用 `-GameRoot`、配置 `gameRoot`、`STUDENTAGE_GAME_ROOT`，然后查找附近游戏目录和 Steam 库。

## 能测什么

| 场景 | 检查内容 |
|---|---|
| `startup` | 到达主菜单、被测插件确实加载、菜单存在可点击目标 |
| `new-game` | 用固定测试角色开局、处理开场对话、返回主菜单 |
| `ui-dump` | 手动选择的工具场景，导出主菜单和新游戏 UI 文字、按钮、节点树及截图 |
| `sample-*` | 示例包：主菜单布局留档、打开设置、新游戏 Esc 暂停菜单 |

场景选择支持名字、`tag:标签`、`*`。`*` 不选带 `manual` 标签的场景。重复选择会去重；不存在的名字/标签会报错，不能靠拼错名字得到绿色结果。

自定义场景使用 `[HarnessScenario]` 和 `IHarnessScenario`，只需引用 `StudentAgeHarness.Core.dll`。见 [编写场景](docs/authoring.md) 和 `samples/ExamplePack/ExampleScenarios.cs`。

**内置烟雾测试通过不等于你的 Mod 全部功能通过。** 应为新功能增加独立的操作和结果断言。

## 产物和退出码

默认写入 `<游戏目录>/_harness_runs/<时间戳-随机后缀>/`：

- `report.md`：人读的摘要；`report.json`：稳定的 v2 数据格式。
- `screenshots/`：成功步骤可选截图，失败步骤自动截图。
- `ui-dump/`：场景显式导出的 UI 数据。
- `BepInEx/LogOutput.log`、`logs/Player.log`：原始日志。
- `profile/`：本次运行的存档和本地 Mod 数据。
- `run.json`、`harness-ready.json`、`launcher.json`：启动和恢复记录。

| 退出码 | 含义 |
|---|---|
| 0 | 通过 |
| 1 | 断言失败、异常、隔离违规、步骤漏跑；`-Strict` 下 warning 也失败 |
| 2 | 启动失败、没有有效报告、全部场景未执行、游戏未正常退出或恢复未完成 |
| 3 | 配置/环境问题，例如路径错误、Steam 未启动、游戏已运行 |

Unity 未处理异常会失败；BepInEx 和被测插件的 Error/Fatal 日志也会失败。原版游戏把部分普通消息写成 `Debug.LogError`，这类 Unity 日志仅计数和留样，不直接判失败。

报告 Schema 在 `schemas/report-v2.schema.json`，扩展数据写入 `extensions`，不要把自己的字段塞进顶层。

## 隔离与边界

- 每次复制本机 BepInEx core、明确指定的插件/依赖/配置到独立目录，通过 Steam 的 Doorstop 参数启动。**不会加载游戏目录里全部已有插件**。
- harness 只在 `sah run` 准备的运行目录里启用；装在游戏自己的 BepInEx 里不会启用。
- `PathDefine` 存档重定向；`SaveMgr` 拒绝写到测试存档目录以外的存档；游戏偏好只留在内存。
- `ResPath` 常用本地 Mod 路径和 `ModCtrl.GetFullUrl` 的本地回退路径重定向到 `profile/Mods`。临时夹具创建在运行目录内。
- 拦截已知 Steam 成就、统计、云文件写入和工坊发布/订阅/投票 API。读取和身份验证仍走 Steam。
- 启动前原始字节备份游戏 PlayerPrefs 注册表，游戏退出后原样恢复并复核。快照放在 `%LOCALAPPDATA%/StudentAgeHarness/recovery`，不放在分享包里。
- 启动器中断后，下次 `run` 会先恢复。也可在游戏关闭后运行 `sah restore`；不同项目共享恢复记录和互斥锁。

**这不是操作系统安全沙箱。** 任意 Mod/场景包都是本机执行的代码。直接 `File.WriteAllText`、硬编码的 `Application.persistentDataPath`、自己调用原生 Steam API 或网络、早于 harness 的插件/预加载补丁初始化，可能绕过保护。只测试可信代码，重要创作文件仍需备份。真实 `Saves` 和 `Mods` 的前后文件元数据对比是额外侦测，不是回滚或完整内容证明。尤其图片选择器中硬编码真实路径的原版编辑器功能，不保证全部可用。

默认不继承玩家启用的工坊 Mod。需明确设置 `workshopMods` 为 ID 字符串数组，或 `"inherit"`；后者只复制启用列表，不复制玩家存档。

默认对报告和 UI dump 中已知的运行、游戏、LocalLow 和用户目录前缀做脱敏。**原始日志、截图、文本内容、run.json 及任意外部路径不保证脱敏，不能直接整目录公开上传。**

## 清理

`run` 不会自动删除旧报告。下面先预览，再明确删除旧运行目录（只处理带 harness `run.json` 的目录）：

```powershell
& $sah clean -Keep 20
& $sah clean -Keep 20 -Delete
```

有游戏进程或未恢复快照时拒绝清理。

## 配置、skills 与开发

- [配置说明](docs/configuration.md)
- [场景 API 和扩展](docs/authoring.md)
- [AI 工作流 skills](docs/skills.md)：setup / run / author，可选，不依赖 AI 才能使用 harness

源码仓库：

```powershell
$env:STUDENTAGE_GAME_ROOT = 'D:\Games\StudentAge'
dotnet build .\StudentAgeHarness.sln -c Release
dotnet test .\tests\StudentAgeHarness.Core.Tests -c Release --no-build
.\tests\Test-Launcher.ps1
.\tools\sah.cmd run -Config .\samples\harness.example.json
.\tools\Pack-Release.ps1
```

修改插件源码后，**先**运行 `tools/Set-HarnessVersion.ps1 -Bump patch` 再构建。版本只有 `Directory.Build.props` 一处声明。打包脚本只打包已构建的同版本二进制，不构建、不发布、不覆盖既有包；不包含游戏、Unity 或 BepInEx 程序集。

目录分工：`Core` 提供执行器和公共 API，`StudentAgeHarness` 提供学生时代适配层，`samples` 提供场景包，`tools` 提供 Windows 启动器。

P0 不包含 live 命令通道、热重载、自动生成业务断言、无桌面 CI 或自动发布。

MIT License。非官方工具，与游戏开发商、Valve 无隶属关系。
