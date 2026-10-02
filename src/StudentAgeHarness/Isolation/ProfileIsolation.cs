using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sdk;
using Sdk.PlatformAPI;
using StudentAgeHarness.Engine;
using UnityEngine;

namespace StudentAgeHarness.Plugin.Isolation
{
    /// <summary>
    /// 存档和偏好隔离：存档目录改到运行目录的 profile/Saves，SaveMgr 往别处写会被拦下；
    /// 游戏偏好（SaveMgr.SetPref/GetPref）只存在内存里，每次运行都是全新默认值；
    /// 启动时不检查、不修复玩家真实存档（Main.FixSaveProblem）。
    /// 必须在插件 Awake 里安装：此时 Main.OnInit 和 ModCtrl.Load 都还没运行。
    /// </summary>
    internal static class ProfileIsolation
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, object> Preferences = new Dictionary<string, object>(StringComparer.Ordinal);
        private static readonly List<string> BlockedSaveTargets = new List<string>();
        private static RunSettings settings;
        private static string profileRoot;
        private static string saveRoot;
        private static int preferenceWrites;
        private static int blockedSaves;
        private static bool fixSaveProblemSkipped;
        private static string workshopSource = "none";
        private static int workshopModCount;
        private static readonly List<string> Warnings = new List<string>();

        internal static string SaveRoot => saveRoot;
        internal static string LocalModsRoot => Path.Combine(profileRoot, "Mods");

        internal static void Install(Harmony harmony, RunSettings runSettings)
        {
            settings = runSettings;
            profileRoot = runSettings.ProfileDirectory;
            saveRoot = Path.Combine(profileRoot, "Saves");
            Directory.CreateDirectory(saveRoot);
            Directory.CreateDirectory(LocalModsRoot);

            // 第一次访问 PathDefine 会用当时的平台算出默认路径；这里立刻覆盖，游戏后面不会再改它。
            PathDefine.SAVE_PATH = saveRoot;
            PathDefine.TEST_SAVE_PATH = Path.Combine(profileRoot, "Saves_Test");
            PathDefine.IMG_PATH = Path.Combine(saveRoot, "Images");
            PathDefine.MUSIC_PATH = Path.Combine(saveRoot, "Musics");
            Type resourcePaths = typeof(Main).Assembly.GetType("Sdk.ResPath", true);
            harmony.Patch(AccessTools.Method(resourcePaths, "ToLocalLowModUrl"),
                prefix: new HarmonyMethod(typeof(ProfileIsolation), nameof(LocalModPath)));
            harmony.Patch(AccessTools.Method(resourcePaths, "ToLocalLowUrl"),
                prefix: new HarmonyMethod(typeof(ProfileIsolation), nameof(LocalResourcePath)));
            harmony.Patch(AccessTools.Method(typeof(ModCtrl), nameof(ModCtrl.GetFullUrl)),
                postfix: new HarmonyMethod(typeof(ProfileIsolation), nameof(RedirectModResource)));

            Type saveMgr = typeof(SaveMgr);
            foreach (MethodInfo method in saveMgr.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "SetPref"))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(ProfileIsolation), nameof(SetPreference)));
            foreach (MethodInfo method in saveMgr.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "GetPref"))
            {
                string prefix = method.ReturnType == typeof(string) ? nameof(GetStringPreference)
                    : method.ReturnType == typeof(int) ? nameof(GetIntPreference)
                    : nameof(GetFloatPreference);
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(ProfileIsolation), prefix));
            }
            harmony.Patch(AccessTools.Method(saveMgr, nameof(SaveMgr.DelPref)),
                prefix: new HarmonyMethod(typeof(ProfileIsolation), nameof(DeletePreference)));
            harmony.Patch(AccessTools.Method(saveMgr, nameof(SaveMgr.Save)),
                prefix: new HarmonyMethod(typeof(ProfileIsolation), nameof(GuardSave)));
            harmony.Patch(AccessTools.Method(saveMgr, nameof(SaveMgr.SaveAsync)),
                prefix: new HarmonyMethod(typeof(ProfileIsolation), nameof(GuardSaveAsync)));

            MethodInfo fixSaveProblem = AccessTools.Method(typeof(Main), "FixSaveProblem");
            if (fixSaveProblem != null)
                harmony.Patch(fixSaveProblem, prefix: new HarmonyMethod(typeof(ProfileIsolation), nameof(SkipFixSaveProblem)));
            else
                Warnings.Add("找不到 Main.FixSaveProblem，启动时可能弹出存档修复提示。");

            PrepareWorkshopMods(harmony);
        }

        private static void PrepareWorkshopMods(Harmony harmony)
        {
            string modList = Path.Combine(saveRoot, "_mod");
            if (settings.WorkshopMods.Count > 0)
            {
                File.WriteAllText(modList, string.Join("\n", settings.WorkshopMods.ToArray()) + "\n", new UTF8Encoding(false));
                workshopSource = "config";
                workshopModCount = settings.WorkshopMods.Count;
            }
            else if (settings.InheritWorkshopMods)
            {
                // 玩家的启用列表在真实存档目录里，要等 Steam 初始化后才知道用户 ID，所以在 ModCtrl.Load 开始时复制。
                MethodInfo load = AccessTools.Method(typeof(ModCtrl), nameof(ModCtrl.Load));
                if (load != null)
                    harmony.Patch(load, prefix: new HarmonyMethod(typeof(ProfileIsolation), nameof(CopyPlayerModList)));
                else
                    Warnings.Add("找不到 ModCtrl.Load，无法沿用玩家的创意工坊启用列表。");
                workshopSource = "inherit";
            }
        }

        private static void CopyPlayerModList()
        {
            try
            {
                string target = Path.Combine(saveRoot, "_mod");
                if (File.Exists(target)) return;
                string source = Path.Combine(Application.persistentDataPath, "Saves", Platform.Current.GetUserId(), "_mod");
                if (!File.Exists(source)) return;
                File.Copy(source, target);
                workshopModCount = File.ReadAllLines(source).Count(line => line.Trim().Length > 0);
            }
            catch (Exception ex)
            {
                lock (Gate) Warnings.Add("复制玩家的创意工坊启用列表失败：" + ex.Message);
            }
        }

        private static bool SkipFixSaveProblem()
        {
            fixSaveProblemSkipped = true;
            return false;
        }

        // ======================== 偏好 ========================

        private static bool SetPreference(string _key, object _value)
        {
            lock (Gate)
            {
                Preferences[_key ?? string.Empty] = _value;
                preferenceWrites++;
            }
            return false;
        }

        private static bool GetStringPreference(string _key, string _default, ref string __result)
        {
            lock (Gate) __result = Preferences.TryGetValue(_key ?? string.Empty, out object value) && value is string text ? text : _default;
            return false;
        }

        private static bool GetIntPreference(string _key, int _default, ref int __result)
        {
            lock (Gate) __result = Preferences.TryGetValue(_key ?? string.Empty, out object value) && value is int number ? number : _default;
            return false;
        }

        private static bool GetFloatPreference(string _key, float _default, ref float __result)
        {
            lock (Gate) __result = Preferences.TryGetValue(_key ?? string.Empty, out object value) && value is float number ? number : _default;
            return false;
        }

        private static bool DeletePreference(string _key)
        {
            lock (Gate)
            {
                if (_key == null) Preferences.Clear();
                else Preferences.Remove(_key);
            }
            return false;
        }

        // ======================== 存档写入 ========================

        private static bool GuardSave(string _directory, string _filename, ref bool __result)
        {
            if (IsAllowed(_directory, _filename)) return true;
            Block(_directory, _filename);
            __result = false;
            return false;
        }

        private static bool GuardSaveAsync(string _directory, string _filename, Action<bool> _finish)
        {
            if (IsAllowed(_directory, _filename)) return true;
            Block(_directory, _filename);
            try { _finish?.Invoke(false); }
            catch (Exception ex) { HarnessLog.Warning("存档回调出错：" + ex.Message); }
            return false;
        }

        private static bool IsAllowed(string directory, string filename)
        {
            try { return RunGate.IsUnder(Path.Combine(directory ?? string.Empty, filename ?? string.Empty), profileRoot); }
            catch { return false; }
        }

        private static bool LocalModPath(string _path, ref string __result)
        {
            __result = Path.GetFullPath(Path.Combine(LocalModsRoot, _path ?? string.Empty));
            return false;
        }

        private static bool LocalResourcePath(string _path, ref string __result)
        {
            string relative = (_path ?? string.Empty).Replace('\\', '/');
            if (relative != "Mods" && !relative.StartsWith("Mods/", StringComparison.OrdinalIgnoreCase)) return true;
            __result = Path.GetFullPath(Path.Combine(profileRoot, relative));
            return false;
        }

        private static void RedirectModResource(ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;
            string real = Path.Combine(Application.persistentDataPath, "Mods");
            if (!RunGate.IsUnder(__result, real)) return;
            string suffix = Path.GetFullPath(__result).Substring(Path.GetFullPath(real).Length).TrimStart('\\', '/');
            __result = Path.GetFullPath(Path.Combine(LocalModsRoot, suffix));
        }

        private static void Block(string directory, string filename)
        {
            string target;
            try { target = Path.Combine(directory ?? string.Empty, filename ?? string.Empty); }
            catch { target = (directory ?? "(null)") + "\\" + filename; }
            lock (Gate)
            {
                blockedSaves++;
                if (BlockedSaveTargets.Count < 20) BlockedSaveTargets.Add(target);
            }
            HarnessLog.Error("隔离拦截了一次写到测试存档目录以外的存档：" + target);
            HarnessRuntime.Report?.AddFinding(HarnessSeverity.Error, "isolation", "save-outside-profile-blocked",
                "有代码试图把存档写到测试存档目录以外，已拦截（游戏收到的是保存失败）。", target);
        }

        // ======================== 报告 ========================

        internal static void Describe(JObject isolation, HarnessReport report)
        {
            lock (Gate)
            {
                isolation["saves"] = new JObject
                {
                    ["root"] = saveRoot,
                    ["blockedWrites"] = blockedSaves,
                    ["blockedTargets"] = new JArray(BlockedSaveTargets.ToArray()),
                    ["fixSaveProblemSkipped"] = fixSaveProblemSkipped
                };
                isolation["preferences"] = new JObject
                {
                    ["mode"] = "memory",
                    ["writes"] = preferenceWrites,
                    ["keys"] = Preferences.Count
                };
                isolation["workshopMods"] = new JObject
                {
                    ["source"] = workshopSource,
                    ["count"] = workshopModCount
                };
                isolation["localModsRoot"] = LocalModsRoot;
                foreach (string warning in Warnings)
                    report.AddFinding(HarnessSeverity.Warning, "isolation", "isolation-setup-warning", warning);
            }
        }
    }
}
