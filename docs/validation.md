# 验证记录

验证日期：2026-10-02（本机时间）。

环境：Windows 10、PowerShell 5.1、.NET SDK 8.0.416、Steam 游戏 1.90、
Unity 2020.3.48f1c1、BepInEx 5.4.23.4、Doorstop 4.4.1、InputSystem 1.5.1。
其他游戏/运行库版本未验证。

## 已完成的检查

- Core 单元测试与报告 Schema 测试：覆盖配置解析、场景筛选、报告汇总、递归脱敏、夹具清理、协程释放。
- 启动器测试：使用独立临时注册表键验证原始字节快照，包含 Unity 的八字节 REG_DWORD；覆盖恢复、退出码、列表处理和清理预览。
- 真游戏场景：`startup`、`new-game`、`sample-main-menu`、`sample-new-game`、`sample-settings` 通过。后台模式有非黑屏截图，Esc 暂停和设置开关结果已核对。
- 独立 FixtureMod：确认实际加载 GUID/版本；SaveMgr 保存到隔离目录，偏好读写删除正确；本地 Mod 路径重定向和临时夹具生效。
- 真实 InputSystem 鼠标：在失焦后台窗口打开设置，报告记录一次后台鼠标手势，截图与打开状态都通过。
- 可见模式最小化：运行中把游戏窗口最小化并保持到结束，`startup`、`new-game` 和 3 个示例场景全部通过；最小化期间的 5 张截图（主菜单、新游戏、暂停菜单、设置）内容正确，没有黑图。启动阶段最小化时，游戏应用分辨率会把窗口恢复一次。
- Steam 写入拦截：先断言目标方法有本工具的 Harmony prefix，再调用 StoreStats，结果被拒绝且计数加一。未执行任何工坊发布。
- 故意失败：断言失败截图存在，后续普通步骤跳过，Cleanup 执行，场景间恢复有效，启动器返回 1。
- 越界保存：普通越界目录和文件名 `../` 越界均被拒绝，预定的临时越界目标不存在，报告含隔离 error。
- 配置遗漏：把非插件 DLL 当作插件、请求不存在的场景时，报告失败且退出码为 1，没有“假绿”。
- 已验证运行的真实 Saves/Mods 文件元数据无变化，PlayerPrefs 注册表原始字节恢复并复核。

故意失败的报告是负面测试成功的证据，不是发布功能已知失败。原始运行目录保留在本机 `_harness_runs`，不随发布包传播。

## 重现

```powershell
dotnet build .\StudentAgeHarness.sln -c Release
dotnet test .\tests\StudentAgeHarness.Core.Tests -c Release --no-build
.\tests\Test-Launcher.ps1
.\tools\sah.cmd run -Config .\samples\harness.example.json

.\tools\sah.cmd run `
  -Plugin .\tests\FixtureMod\bin\Release\StudentAgeHarness.FixtureMod.dll `
  -Pack .\tests\ValidationPack\bin\Release\StudentAgeHarness.ValidationPack.dll `
  -Scenario verify-isolation

# 下面预期退出码为 1，且 report.extensions["validation.cleanup"] 为 true。
.\tools\sah.cmd run `
  -Pack .\tests\ValidationPack\bin\Release\StudentAgeHarness.ValidationPack.dll `
  -Scenario verify-failure,verify-save-guard
```

真实报告 Schema 检查：设置 `SAH_REPORTS` 为一或多个 `report.json` 的绝对路径（用分号分隔），再运行 Core 测试。

## 未覆盖

- 所有第三方 Mod、全部图片选择器和编辑器硬编码路径。
- 前台焦点专属测试、拖拽/滚轮的全部控件组合、IME。
- 未安装 DLC 的配置组合、所有显示缩放、多显示器、其他 Windows/游戏版本。
- OS 级恶意代码隔离、强断电时正在写快照的极端情况、绕过已拦截 API 的原生网络/Steam 调用。

不要把这些测试结果解释为上述未测范围的兼容或安全保证。
