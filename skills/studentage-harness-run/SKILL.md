---
name: studentage-harness-run
description: Quickly test a completed StudentAge mod using StudentAge Harness and inspect evidence. Use after mod changes or when asked for in-game smoke/regression testing; not for publishing, live control, or normal gameplay.
license: MIT
---

# 编译后快速验收

1. 读取项目指令、harness.json 和近期改动，确认待测版本与对应场景。遵守项目版本递增要求，再用项目自己的构建命令编译。
2. 定位 tools/sah.cmd，运行 doctor；游戏正在运行时停止并让用户退出，不强杀。遇到 DOORSTOP_* 污染使用干净终端；不要关闭安全门禁。
3. 运行最窄相关场景（启动、具体功能、必要时新游戏），不把 startup 当完整功能验收。只运行用户项目可信的插件、场景包和预加载补丁。
4. 保存退出码与本次 run 路径，读取 report.json、report.md 和 launcher.json：
   - schema 必须为 studentage-harness-report/2；核对请求/实际场景、步骤未漏跑；
   - environment.pluginsUnderTest 中实际加载的 GUID/版本；
   - failedSteps、errors、warnings、aborted，以及恢复是否完成；
   - isolation.realSaves/localMods 的完整性和变化，不能将 incomplete 审计说成绝对安全。
5. 查看对应失败截图/成功 UI 证据，必要时读取日志中的相关片段。不向对话输出 Steam 身份或玩家私有内容。
6. 失败定位到首个真实错误。用户请求修复时才改产品代码；不能削弱断言、吞异常或换场景让报告变绿。改插件后递增版本再构建和复测。
7. 简要返回哪些检查通过/失败、未测范围、报告位置。退出码：0 通过，1 测试失败，2 不完整/恢复故障，3 配置/环境错误。

恢复：游戏退出后 sah restore。不得删除待恢复快照。清理先 sah clean 预览，仅用户授权删除时追加 -Delete。禁止自动上传日志/截图或发布 Mod。
