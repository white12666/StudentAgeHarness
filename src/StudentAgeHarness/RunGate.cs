using System;
using System.IO;
using BepInEx;
using StudentAgeHarness.Engine;

namespace StudentAgeHarness.Plugin
{
    /// <summary>
    /// 启用条件：插件位于 sah 准备的隔离目录里（不是游戏自己的 BepInEx），并且目录里有 run.json。
    /// </summary>
    internal static class RunGate
    {
        internal static RunSettings TryActivate(out string reason)
        {
            reason = null;
            string runRoot;
            string gameRoot;
            try
            {
                runRoot = Path.GetFullPath(Path.Combine(Paths.BepInExRootPath, ".."));
                gameRoot = Path.GetFullPath(Paths.GameRootPath);
            }
            catch (Exception ex)
            {
                reason = "无法确定运行目录：" + ex.Message;
                return null;
            }

            if (SamePath(runRoot, gameRoot))
            {
                reason = "StudentAge Harness 装在了游戏自己的 BepInEx 里，这里不会启用。它只在 sah run 准备的隔离目录里工作，" +
                    "日常游玩请从 BepInEx\\plugins 里删掉它。";
                return null;
            }

            string runJson = Path.Combine(runRoot, "run.json");
            if (!File.Exists(runJson))
            {
                reason = "运行目录缺少 run.json：" + runRoot;
                return null;
            }

            try
            {
                return RunSettings.Load(runJson);
            }
            catch (Exception ex)
            {
                reason = "run.json 无法解析：" + ex.Message;
                return null;
            }
        }

        internal static bool SamePath(string left, string right)
        {
            return string.Equals(left.TrimEnd('\\', '/'), right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>path 是否位于 root 之内（或等于 root）。</summary>
        internal static bool IsUnder(string path, string root)
        {
            return RunPaths.IsInside(path, root);
        }
    }
}
