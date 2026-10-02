using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace StudentAgeHarness.Plugin.Isolation
{
    /// <summary>
    /// 运行前后对比玩家真实存档目录和本地 Mods 目录的文件清单（路径、大小、修改时间），
    /// 用结果辅助检查隔离：真实存档有变化记 error；本地 Mods 有变化记 warning（也可能是作者在编辑）。
    /// 只比对文件元数据，不能证明字节级不变；夹具位于运行目录，不接触真实 Mods。
    /// </summary>
    internal sealed class IsolationAudit
    {
        private const int MaxFiles = 50000;
        private const int MaxListedChanges = 30;

        private readonly string savesRoot;
        private readonly string modsRoot;
        private readonly Snapshot savesBefore;
        private readonly Snapshot modsBefore;

        private IsolationAudit(string persistentDataPath)
        {
            savesRoot = Path.Combine(persistentDataPath, "Saves");
            modsRoot = Path.Combine(persistentDataPath, "Mods");
            savesBefore = Snapshot.Capture(savesRoot, null);
            modsBefore = Snapshot.Capture(modsRoot, null);
        }

        internal static IsolationAudit Begin(string persistentDataPath)
        {
            return new IsolationAudit(persistentDataPath);
        }

        internal void Complete(JObject isolation, HarnessReport report)
        {
            Snapshot savesAfter = Snapshot.Capture(savesRoot, null);
            Snapshot modsAfter = Snapshot.Capture(modsRoot, null);
            List<string> saveChanges = savesBefore.Compare(savesAfter);
            List<string> modChanges = modsBefore.Compare(modsAfter);

            isolation["realSaves"] = Describe(savesRoot, savesBefore, saveChanges);
            isolation["localMods"] = Describe(modsRoot, modsBefore, modChanges);
            if (savesBefore.Truncated || savesAfter.Truncated || modsBefore.Truncated || modsAfter.Truncated)
                report.AddFinding(HarnessSeverity.Warning, "isolation", "audit-incomplete",
                    "文件清单检查不完整，不能据此保证真实目录没有变化。");

            if (saveChanges.Count > 0)
                report.AddFinding(new HarnessFinding
                {
                    Severity = HarnessSeverity.Error,
                    Category = "isolation",
                    Rule = "real-saves-changed",
                    Scenario = string.Empty,
                    Step = string.Empty,
                    Path = savesRoot,
                    Message = "运行期间玩家真实存档目录里有 " + saveChanges.Count + " 处变化，隔离没有挡住：" +
                        string.Join("；", saveChanges.Take(5).ToArray())
                }.WithMetric("changes", saveChanges.Take(MaxListedChanges).ToArray()));
            if (modChanges.Count > 0)
                report.AddFinding(new HarnessFinding
                {
                    Severity = HarnessSeverity.Warning,
                    Category = "isolation",
                    Rule = "local-mods-changed",
                    Scenario = string.Empty,
                    Step = string.Empty,
                    Path = modsRoot,
                    Message = "运行期间本地 Mods 目录里有 " + modChanges.Count + " 处变化（这个目录不在隔离范围内，" +
                        "如果不是你自己在编辑，请检查场景或被测 mod 是否写了这里）：" +
                        string.Join("；", modChanges.Take(5).ToArray())
                }.WithMetric("changes", modChanges.Take(MaxListedChanges).ToArray()));
        }

        private static JObject Describe(string root, Snapshot before, List<string> changes)
        {
            return new JObject
            {
                ["root"] = root,
                ["exists"] = before.Exists,
                ["files"] = before.Files.Count,
                ["truncated"] = before.Truncated,
                ["unchanged"] = changes.Count == 0,
                ["changes"] = new JArray(changes.Take(MaxListedChanges).ToArray())
            };
        }

        private sealed class Snapshot
        {
            internal readonly Dictionary<string, KeyValuePair<long, long>> Files =
                new Dictionary<string, KeyValuePair<long, long>>(StringComparer.OrdinalIgnoreCase);
            internal bool Exists;
            internal bool Truncated;

            internal static Snapshot Capture(string root, Func<string, bool> exclude)
            {
                var snapshot = new Snapshot();
                try
                {
                    if (!Directory.Exists(root)) return snapshot;
                    snapshot.Exists = true;
                    string prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
                    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        string relative = file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            ? file.Substring(prefix.Length)
                            : file;
                        if (exclude != null && exclude(relative)) continue;
                        if (snapshot.Files.Count >= MaxFiles)
                        {
                            snapshot.Truncated = true;
                            break;
                        }
                        try
                        {
                            var info = new FileInfo(file);
                            snapshot.Files[relative] = new KeyValuePair<long, long>(info.Length, info.LastWriteTimeUtc.Ticks);
                        }
                        catch
                        {
                            snapshot.Truncated = true;
                        }
                    }
                }
                catch
                {
                    snapshot.Truncated = true;
                }
                return snapshot;
            }

            internal List<string> Compare(Snapshot after)
            {
                var changes = new List<string>();
                foreach (KeyValuePair<string, KeyValuePair<long, long>> pair in Files)
                {
                    if (!after.Files.TryGetValue(pair.Key, out KeyValuePair<long, long> now))
                    {
                        // 截断时缺失不一定是删除，只比较两边都有的文件。
                        if (!after.Truncated) changes.Add("删除 " + pair.Key);
                    }
                    else if (now.Key != pair.Value.Key || now.Value != pair.Value.Value)
                    {
                        changes.Add("修改 " + pair.Key);
                    }
                }
                if (!Truncated)
                    foreach (string key in after.Files.Keys)
                        if (!Files.ContainsKey(key)) changes.Add("新增 " + key);
                if (Exists != after.Exists) changes.Add(after.Exists ? "目录被创建" : "目录被删除");
                return changes;
            }
        }
    }
}
