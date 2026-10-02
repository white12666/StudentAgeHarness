---
name: studentage-harness-author
description: Write declarative StudentAge Harness scenario packs for a mod feature, then run them in the real game. Use when mod-specific assertions are missing; not for changing the generic engine to hardcode a mod's tests.
license: MIT
---

# 编写 Mod 场景

1. 阅读项目指令和 Harness docs/authoring.md，明确要证明的玩家可观察结果。必要时用 ui-dump 收集按钮文字/字段路径，不能猜点击坐标作为稳定断言。
2. 新建/复用 netstandard2.0 场景类库，引用 StudentAgeHarness.Core.dll，Private=false；按需引用同版本 Unity/游戏/Mod DLL，不把这些库重新打进场景产物。
3. 场景使用公开无参构造函数、[HarnessScenario]、IHarnessScenario。名字带 Mod 前缀，声明 RequiresPlugins GUID、TimeoutSec 和标签；需要焦点/独占进程的测试明确标注。
4. Build 只描述步骤。每个动作后用 Then/Assert 检查真正结果，设置有界等待；失败自动截图，关键成功步骤 WithCapture。收尾 Cleanup，明确预期弹窗才 ExpectingModal。
5. Ui 是射线检查后的 EventSystem 点击；真实悬停/拖拽/快捷键使用 Input 的临时 InputSystem 设备。不要直接调用产品私有处理函数冒充 UI 交互。
6. 测试数据用 Fixtures 写入运行目录，不读取或改写正式存档。跨场景只恢复菜单，不保证全局静态状态重置；必要时 RunAlone 或单独进程。
7. 给 harness.json 添加 packs/scenarios，保留用户其他设置。扩展数据写 report.SetExtension("mymod.key", data)，不改报告的顶层协议。
8. 编译并在真实游戏跑场景。验证场景的断言确实执行；必要时加一个临时故意失败的测试确认退出码/截图/cleanup 路径，但不要把故意失败场景加入默认列表。

完成标准：场景包可独立加载，产品代码无 harness 特判，真实游戏结果和证据可复现；无法运行时如实记录未验证事项。不发布、不上传私有证据。
