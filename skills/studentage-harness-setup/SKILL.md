---
name: studentage-harness-setup
description: Set up StudentAge Harness for a mod author's project. Use when first connecting compiled BepInEx mod outputs and scenario packs to harness.json; not for installing game binaries or publishing mods.
license: MIT
---

# StudentAge Harness 接入

1. 读取项目 AGENTS.md、构建配置和现有 harness.json。定位已解压的 Harness（tools/sah.cmd），不要猜用户安装路径。没有安装包时按仓库 README 构建本工具；只问真正阻塞的问题。
2. 确认本机 Steam 版学生时代、BepInEx 5 Mono、PowerShell 5.1+。Steam 应由用户正常启动并登录，游戏应已关闭。不要替用户结束正在玩的游戏、改 Steam 登录或绕过启动器安全检查。
3. 找到待测 Mod 的真实输出。简单 Mod 指向 DLL；有资源/依赖时指向输出目录或明确 dependencies。不能把 Unity、游戏或 BepInEx DLL 当作插件依赖复制。保留用户现有配置。
4. 写最小 harness.json：plugins、expectPlugins（已知 GUID）、scenarios=["startup"]。有自定义场景才填写 packs。配置相对路径基于配置文件目录。
5. 用启动器的绝对路径运行 doctor。修复配置或依赖问题；不要部署 harness 到游戏插件目录。
6. 用户请求接入测试即授权可信代码的隔离运行，运行 startup，检查实际加载 DLL、版本、截图、注册表恢复与真实目录审计。不能仅以进程退出或工具退出码为成功证据。
7. 返回配置位置、可复现运行命令和实际检查结果；不推送、不上传报告。

完成标准：配置可复用，doctor 成功，startup 实际通过，正式插件目录未改动。没有可用桌面/Steam 时明确说明阻塞，不伪造游戏测试结果。
