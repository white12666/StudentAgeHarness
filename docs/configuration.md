# 配置参考

配置文件默认为当前目录 `harness.json`，可用 `-Config` 指定。编辑器可引用 `schemas/harness.schema.json` 提供提示。

```json
{
  "gameRoot": "auto",
  "plugins": ["bin/Release/MyMod"],
  "dependencies": ["../SharedPlugin/bin/Release/SharedPlugin.dll"],
  "packs": ["tests/GameScenarios/bin/Release/MyMod.Scenarios.dll"],
  "expectPlugins": ["com.example.mymod"],
  "scenarios": ["startup", "my-feature"],
  "configFiles": ["tests/config/com.example.mymod.cfg"],
  "patchers": [],
  "window": "visible",
  "resolution": "1920x1080",
  "mute": true,
  "timeoutSec": 600,
  "startupTimeoutSec": 180,
  "strict": false,
  "redactPaths": true,
  "workshopMods": [],
  "keepRuns": 20
}
```

| 字段 | 默认/说明 |
|---|---|
| `gameRoot` | 自动发现；指向包含 StudentAge.exe 的目录 |
| `runsDir` | `<游戏目录>/_harness_runs`。建议避开网络盘、链接、非 ASCII 路径 |
| `plugins` | 被测插件 DLL 或目录。每项必须至少加载一个 BepInEx 插件，否则报错。目录会以其中插件 DLL 的名字复制和显示（例如 `bin/Release/netstandard2.0` 显示为 `MyMod.dll`，复制到 `BepInEx/plugins/MyMod`） |
| `dependencies` | 额外复制的插件/依赖 DLL 或目录，不强制每项包含 BepInPlugin |
| `packs` | 场景 DLL 列表；只复制这些 DLL 和同名 PDB。依赖 DLL 应放入 `dependencies` |
| `expectPlugins` | 额外要求已加载的插件 GUID |
| `configFiles` | 明确复制到本次 BepInEx/config 的文件。缺省只复制 BepInEx.cfg，其余插件使用默认配置 |
| `patchers` | 预加载补丁 DLL 或目录；仅在确有必要时使用可信补丁，可能早于隔离安装 |
| `scenarios` | 缺省 `["startup"]`，按列表顺序选择；标签匹配的场景按包中 Order/名字排序 |
| `window` | 缺省 `visible`：正常显示，可以被别的窗口挡住或最小化；`background`：窗口停在桌面下方并把焦点还给你，运行中调不出来 |
| `resolution` | 缺省 1920x1080，宽 320..8192、高 200..8192 |
| `mute` | 缺省 true，两种窗口模式都生效 |
| `timeoutSec` | 引擎总时间上限（含启动）；缺省 600 |
| `startupTimeoutSec` | 启动到主菜单上限；缺省 180，不能替代总上限 |
| `strict` | 缺省 false；true 把 warning 也作为失败 |
| `redactPaths` | 缺省 true，仅替换已知路径前缀；不处理原始日志或截图 |
| `workshopMods` | 缺省空数组，也可为 `"none"`、`"inherit"` 或已安装工坊 ID 字符串数组 |
| `keepRuns` | 仅供显式 `clean` 命令使用，缺省保留 20 次；run 不自动清理 |

命令行 `-Plugin/-Pack/-Scenario` **替换**配置对应列表，不追加；`-Visible/-Background` 覆盖配置的 `window`，不能同时使用；`-Strict` 只能开启。`-Resolution/-TimeoutSec` 覆盖配置。

### 窗口

- **可见模式（缺省）**：游戏启动时会弹到前台，之后可以切到别的窗口、让它被挡住，或者最小化，测试照常进行，截图仍是游戏画面。最小化后点任务栏图标即可再看。启动阶段游戏应用分辨率时可能会把窗口自己恢复一次，进入主菜单后再最小化就会保持。
- **后台模式**：适合不想看到游戏的时候。窗口一出现就被移到桌面下方（不是最小化），焦点还给启动前的窗口，运行中会一直保持在屏幕外。
- 两种模式都不是无头渲染，仍然需要 GPU、已登录的桌面和 Steam。harness 的点击和输入都不依赖窗口焦点，但运行中请不要在游戏里点击或按键，以免干扰测试。截图如果是黑的，报告会记 `screenshot-blank` warning。

## 故障处理

- **exit 3**：先运行 `doctor`，检查构建路径、依赖、Steam 和已运行的游戏。不要为了继续测试去强行结束用户正在玩的进程。
- **缺 BepInEx 日志**：Doorstop 没从隔离目录启动，检查版本及启动参数。不要改动正式游戏的 doorstop_config.ini 来“凑过”测试。
- **插件未加载**：看隔离目录的 `BepInEx/LogOutput.log`，核对 GUID 和依赖版本。场景包不是 BepInEx 插件，不应写进 `plugins`。
- **主菜单超时**：查看 `logs/Player.log` 和启动超时截图。验证 Steam 能正常启动原版游戏。
- **失败后恢复不到主菜单**：后续场景不会继续操作损坏的状态。先修第一个失败点，不要只延长超时。
- **注册表恢复中断**：确认游戏退出后运行 `sah restore`；不要删除恢复文件，尤其不要运行清理脚本“修复”。
- **截图分辨率偏移**：截图前会尝试矫正并记录 finding。仍有 warning 时，检查显示缩放、窗口最小化与显卡渲染状态。

报告不是证据替代品：确认请求的场景确实执行、被测 DLL 版本正确，再看断言和截图。
