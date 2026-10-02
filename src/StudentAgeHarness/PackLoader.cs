using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using StudentAgeHarness.Engine;

namespace StudentAgeHarness.Plugin
{
    /// <summary>加载 run.json 里列出的场景包（运行目录 packs 下的 DLL），连同插件自带的内置场景一起交给引擎。</summary>
    internal static class PackLoader
    {
        private static string packsDirectory;

        internal static List<ScenarioSource> Load(RunSettings settings, HarnessReport report)
        {
            var sources = new List<ScenarioSource>
            {
                new ScenarioSource
                {
                    Assembly = typeof(PackLoader).Assembly,
                    Name = "builtin",
                    File = "StudentAgeHarness.dll",
                    AllowExtensions = false
                }
            };
            packsDirectory = Path.Combine(settings.RunDirectory, "packs");
            foreach (string relative in settings.Packs)
            {
                string path;
                try { path = Path.GetFullPath(Path.Combine(settings.RunDirectory, relative)); }
                catch (Exception ex)
                {
                    report.AddFinding(HarnessSeverity.Error, "harness", "pack-path-invalid", "场景包路径无效：" + ex.Message, relative);
                    continue;
                }
                if (!File.Exists(path))
                {
                    report.AddFinding(HarnessSeverity.Error, "harness", "pack-missing", "运行目录里找不到场景包。", relative);
                    continue;
                }
                try
                {
                    Assembly assembly = Assembly.LoadFrom(path);
                    sources.Add(new ScenarioSource
                    {
                        Assembly = assembly,
                        Name = Path.GetFileNameWithoutExtension(path),
                        File = relative
                    });
                }
                catch (Exception ex)
                {
                    report.AddFinding(HarnessSeverity.Error, "harness", "pack-load-failed",
                        "无法加载场景包：" + ScenarioCatalog.Unwrap(ex).Message, relative);
                }
            }
            return sources;
        }

        /// <summary>场景包引用的程序集：优先用已经加载的同名程序集（引擎、被测 mod），其次找 packs 目录。</summary>
        internal static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string name;
            try { name = new AssemblyName(args.Name).Name; }
            catch { return null; }
            if (string.Equals(name, "StudentAgeHarness.Core", StringComparison.OrdinalIgnoreCase))
                return typeof(HarnessReport).Assembly;
            Assembly loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
            {
                try { return string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });
            if (loaded != null) return loaded;
            if (packsDirectory == null) return null;
            string candidate = Path.Combine(packsDirectory, name + ".dll");
            try { return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null; }
            catch { return null; }
        }
    }
}
