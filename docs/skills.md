# 可选 AI 工作流

`skills/` 下有三个独立 `SKILL.md`：

- `studentage-harness-setup`：首次接入 Mod 项目、配置插件产物与依赖、跑通 doctor/startup。
- `studentage-harness-run`：编译后快速测试，核对实际 DLL、报告、截图、隔离结果。
- `studentage-harness-author`：为具体 Mod 功能编写独立场景包并验证。

这些只是可复用的操作指引，不需要购买或运行 AI 才能使用 harness。

## Factory Droid

把需要的 skill 目录复制到 **你的 Mod 项目** `.factory/skills/` 下，例如：

```text
MyMod/
  .factory/skills/studentage-harness-run/SKILL.md
  harness.json
```

按 [Factory skills 文档](https://docs.factory.com/harness/skills.md)，可在新会话中使用 `/skills` 检查发现情况，然后自然语言请求或调用 `/studentage-harness-run`。也支持兼容的 `.agents/skills/` 项目目录。

只复制需要的文件，不覆盖已有同名 skill 或项目指令。skill 没有写死个人用户名、安装目录、旧版 DND harness 场景或某个 Mod 的私有协议。

## 可合并到项目 AGENTS.md 的片段

```markdown
## 游戏内验收
- 本项目使用 StudentAge Harness，配置为 harness.json。
- 插件源码改动后按项目规定递增版本，再编译。
- 游戏目录不要安装/覆盖 harness DLL，不要触碰正式存档。
- 先 sah doctor，再 sah run；失败看 report.md/report.json 和对应截图。
- 检查 environment.pluginsUnderTest 的实际加载版本。
- startup 通过只证明启动正常，新功能必须有对应场景断言。
- 不自动上传日志、截图或发布 Mod；清理先预览，经授权再 -Delete。
```
