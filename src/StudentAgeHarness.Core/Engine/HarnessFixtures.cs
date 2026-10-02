using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace StudentAgeHarness
{
    /// <summary>
    /// 场景用的测试数据目录，位于当前运行目录内。fixtures 留作证据，
    /// 临时 mod 正常结束时清理；崩溃后保留在旧运行目录里，不扫描玩家工作区。
    /// </summary>
    public sealed class HarnessFixtures
    {
        internal const string LocalModPrefix = "StudentAgeHarness_";

        private readonly object gate = new object();
        private readonly string runDirectory;
        private readonly string runId;
        private readonly string localModsDirectory;
        private readonly List<string> localMods = new List<string>();
        private readonly HashSet<string> kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal HarnessFixtures(string runDirectory, string runId, string localModsDirectory)
        {
            this.runDirectory = runDirectory;
            this.runId = runId;
            this.localModsDirectory = string.IsNullOrEmpty(localModsDirectory) ? null : Path.GetFullPath(localModsDirectory);
        }

        /// <summary>在运行目录的 fixtures 下建一个新目录并返回绝对路径。游戏不会读这里，适合放场景自己的输入文件。</summary>
        public string CreateDirectory(string name)
        {
            string path = Path.Combine(runDirectory, "fixtures", SafeName(name));
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// 在游戏的本地 Mods 目录（游戏内 Mod 编辑器的工作区）下建一个临时 mod 目录，返回绝对路径。
        /// 目录名形如 StudentAgeHarness_&lt;运行ID&gt;_&lt;name&gt;，运行结束时整个删除。
        /// </summary>
        public string CreateLocalMod(string name)
        {
            if (localModsDirectory == null) throw new InvalidOperationException("当前适配层没有提供本地 Mods 目录。");
            string folder = LocalModPrefix + SafeName(runId) + "_" + SafeName(name);
            string path = Path.Combine(localModsDirectory, folder);
            if (Directory.Exists(path)) throw new InvalidOperationException("临时 mod 目录已存在：" + folder);
            Directory.CreateDirectory(path);
            lock (gate) localMods.Add(path);
            return path;
        }

        /// <summary>运行结束后保留这个隔离临时 mod 目录（调试用），报告里记录 info。</summary>
        public void Keep(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (gate) kept.Add(Path.GetFullPath(path));
        }

        internal void CleanupAll(HarnessReport report)
        {
            string[] pending;
            lock (gate) pending = localMods.ToArray();
            foreach (string path in pending)
            {
                bool keep;
                lock (gate) keep = kept.Contains(Path.GetFullPath(path));
                if (keep)
                {
                    report?.AddFinding(HarnessSeverity.Info, "isolation", "fixture-kept",
                        "按场景要求保留了临时 mod 目录，用完请手动删除。", path);
                    continue;
                }
                if (!TryDelete(path))
                    report?.AddFinding(HarnessSeverity.Warning, "isolation", "fixture-not-removed",
                        "没能删除临时 mod 目录，保留在本次运行目录中。", path);
            }
            lock (gate) localMods.Clear();
        }

        internal IList<string> LocalModFixtures
        {
            get { lock (gate) return localMods.ToArray(); }
        }

        private static bool TryDelete(string path)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (!Directory.Exists(path)) return true;
                    Directory.Delete(path, true);
                    return true;
                }
                catch (DirectoryNotFoundException)
                {
                    return true;
                }
                catch (Exception)
                {
                    // 句柄还没释放时 Windows 会拒绝删除，稍等再试。
                    Thread.Sleep(150);
                }
            }
            return !Directory.Exists(path);
        }

        internal static string SafeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "fixture";
            var builder = new StringBuilder(value.Length);
            foreach (char c in value.Trim())
                builder.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            string result = builder.ToString();
            return result.Length > 60 ? result.Substring(0, 60) : result;
        }
    }
}
