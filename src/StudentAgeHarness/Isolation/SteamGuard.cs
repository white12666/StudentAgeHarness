using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sdk.PlatformAPI;
using Steamworks;
using StudentAgeHarness.Engine;

namespace StudentAgeHarness.Plugin.Isolation
{
    /// <summary>
    /// 拦截会改动玩家 Steam 账号的调用：成就、统计、创意工坊上传/订阅/投票、云存储写入。
    /// 读取类调用（查询成就、下载 mod 信息）不受影响。游戏和被测插件收到的都是"调用失败"。
    /// </summary>
    internal static class SteamGuard
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, int> Blocked = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly List<string> Patched = new List<string>();

        private static readonly string[] PlatformMethods =
        {
            "SetAchievement", "SetStat", "AddStat", "StoreStat", "ResetAllStat", "IndicateAchievementProgress",
            "CreateMod", "UpdateMod", "UnsubscribeMod", "VoteOnMod"
        };

        private static readonly string[] UserStatsMethods =
        {
            "SetAchievement", "ClearAchievement", "SetStat", "UpdateAvgRateStat", "StoreStats", "ResetAllStats",
            "IndicateAchievementProgress"
        };

        private static readonly string[] UgcMethods =
        {
            "CreateItem", "SubmitItemUpdate", "DeleteItem", "SubscribeItem", "UnsubscribeItem", "SetUserItemVote",
            "AddItemToFavorites", "RemoveItemFromFavorites", "AddDependency", "RemoveDependency",
            "AddAppDependency", "RemoveAppDependency"
        };

        private static readonly string[] RemoteStorageMethods =
        {
            "FileWrite", "FileWriteAsync", "FileDelete", "FileForget", "FileShare", "FileWriteStreamWriteChunk",
            "FileWriteStreamClose", "SetSyncPlatforms", "PublishWorkshopFile", "CommitPublishedFileUpdate",
            "DeletePublishedFile", "SubscribePublishedFile", "UnsubscribePublishedFile", "SetUserPublishedFileAction",
            "UpdateUserPublishedItemVote", "UGCDownloadToLocation"
        };

        internal static void Install(Harmony harmony)
        {
            PatchAll(harmony, typeof(SteamPlatform), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, PlatformMethods);
            PatchAll(harmony, typeof(SteamUserStats), BindingFlags.Public | BindingFlags.Static, UserStatsMethods);
            PatchAll(harmony, typeof(SteamUGC), BindingFlags.Public | BindingFlags.Static, UgcMethods);
            PatchAll(harmony, typeof(SteamRemoteStorage), BindingFlags.Public | BindingFlags.Static, RemoteStorageMethods);
        }

        private static void PatchAll(Harmony harmony, Type type, BindingFlags flags, string[] names)
        {
            foreach (MethodInfo method in type.GetMethods(flags).Where(m => names.Contains(m.Name)))
            {
                string prefix = method.ReturnType == typeof(bool) ? nameof(BlockBool)
                    : method.ReturnType == typeof(SteamAPICall_t) ? nameof(BlockCall)
                    : method.ReturnType == typeof(void) ? nameof(BlockVoid)
                    : null;
                if (prefix == null) continue;
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(SteamGuard), prefix));
                lock (Gate) Patched.Add(type.Name + "." + method.Name);
            }
        }

        private static bool BlockBool(MethodBase __originalMethod, ref bool __result)
        {
            Count(__originalMethod);
            __result = false;
            return false;
        }

        private static bool BlockCall(MethodBase __originalMethod, ref SteamAPICall_t __result)
        {
            Count(__originalMethod);
            __result = SteamAPICall_t.Invalid;
            return false;
        }

        private static bool BlockVoid(MethodBase __originalMethod)
        {
            Count(__originalMethod);
            return false;
        }

        private static void Count(MethodBase method)
        {
            string name = method == null ? "(unknown)" : method.DeclaringType?.Name + "." + method.Name;
            lock (Gate)
            {
                Blocked.TryGetValue(name, out int count);
                Blocked[name] = count + 1;
            }
        }

        internal static void Describe(JObject isolation)
        {
            lock (Gate)
            {
                var blocked = new JObject();
                foreach (KeyValuePair<string, int> pair in Blocked.OrderBy(item => item.Key, StringComparer.Ordinal))
                    blocked[pair.Key] = pair.Value;
                isolation["steam"] = new JObject
                {
                    ["guardedMethods"] = Patched.Distinct().Count(),
                    ["blockedCalls"] = blocked,
                    ["blockedTotal"] = Blocked.Values.Sum()
                };
            }
            if (Blocked.Count > 0)
                HarnessLog.Info("运行中拦截了 " + Blocked.Values.Sum() + " 次 Steam 写入调用。");
        }
    }
}
