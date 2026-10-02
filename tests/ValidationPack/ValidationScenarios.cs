using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Sdk;
using Steamworks;
using UnityEngine.UI;

namespace StudentAgeHarness.Validation
{
    [HarnessScenario("verify-isolation", Tags = new[] { "manual" },
        RequiresPlugins = new[] { "com.studentage.harness.fixture" }, TimeoutSec = 90)]
    public sealed class IsolationScenario : IHarnessScenario
    {
        public IEnumerable<HarnessStep> Build(HarnessContext ctx)
        {
            yield return HarnessStep.Routine("menu", () => ctx.Game.EnterMainMenu());
            yield return HarnessStep.Do("save_and_preferences", () =>
            {
                ctx.Assert(Path.GetFullPath(PathDefine.SAVE_PATH).StartsWith(ctx.RunDirectory, StringComparison.OrdinalIgnoreCase),
                    "Save root was not redirected.");
                SaveMgr.SetPref("sah-validation", 123);
                ctx.Assert(SaveMgr.GetPref("sah-validation", 0) == 123, "Preference overlay failed.");
                SaveMgr.DelPref("sah-validation");
                ctx.Assert(SaveMgr.GetPref("sah-validation", 7) == 7, "Preference deletion failed.");
                ctx.Assert(new SaveMgr().Save(PathDefine.SAVE_PATH, "validation.save"), "Isolated save failed.");
                string folder = ctx.Fixtures.CreateLocalMod("validation");
                ctx.Assert(folder.StartsWith(ctx.RunDirectory, StringComparison.OrdinalIgnoreCase), "Fixture escaped run.");
                Type paths = typeof(Main).Assembly.GetType("Sdk.ResPath", true);
                string redirected = (string)paths.GetMethod("ToLocalLowModUrl").Invoke(null, new object[] { "" });
                ctx.Assert(Path.GetFullPath(redirected).TrimEnd('\\', '/') ==
                    Path.GetFullPath(ctx.Game.LocalModsDirectory).TrimEnd('\\', '/'), "Mod paths were not redirected.");
                // Verify the guard is installed before exercising a blocked call.
                var method = typeof(SteamUserStats).GetMethod("StoreStats");
                ctx.Assert(Harmony.GetPatchInfo(method).Prefixes.Any(p => p.owner == "com.studentage.harness"),
                    "Steam guard was not installed; refusing to call Steam.");
                ctx.Assert(!SteamUserStats.StoreStats(), "Steam guard failed.");
            });
            yield return HarnessStep.Do("ui_dump", () => ctx.DumpUi("validation-menu")).WithCapture();
            yield return HarnessStep.Routine("real_pointer_settings", () =>
            {
                var settings = ctx.Game.GetViewRoot("EntryView").GetComponentsInChildren<Button>()
                    .First(b => b.name == "btn_option");
                return ctx.Input.Click(settings);
            }).Then(() => ctx.Game.IsViewOpen("SettingView"), 10).WithCapture(0.5f);
            yield return HarnessStep.Routine("close_settings", () => ctx.Game.ClickViewButton("SettingView", "btn_cancel"));
        }
    }

    [HarnessScenario("verify-failure", Tags = new[] { "manual" }, TimeoutSec = 60)]
    public sealed class FailureScenario : IHarnessScenario
    {
        public IEnumerable<HarnessStep> Build(HarnessContext ctx)
        {
            yield return HarnessStep.Routine("menu", () => ctx.Game.EnterMainMenu());
            yield return HarnessStep.Do("intentional_failure", () => ctx.Assert(false, "Expected validation failure."));
            yield return HarnessStep.Do("must_skip", () => ctx.Fail("This step should not execute."));
            yield return HarnessStep.Do("must_cleanup", () => ctx.Report.SetExtension("validation.cleanup", true)).Cleanup();
        }
    }

    [HarnessScenario("verify-save-guard", Tags = new[] { "manual" }, TimeoutSec = 60)]
    public sealed class SaveGuardScenario : IHarnessScenario
    {
        public IEnumerable<HarnessStep> Build(HarnessContext ctx)
        {
            yield return HarnessStep.Do("blocked_save", () =>
            {
                // Both potential destinations remain inside disposable run data even if a guard regresses.
                string outside = Path.Combine(ctx.RunDirectory, "forbidden.save");
                ctx.Assert(!new SaveMgr().Save(ctx.RunDirectory, "forbidden.save"), "Write guard failed.");
                ctx.Assert(!new SaveMgr().Save(PathDefine.SAVE_PATH, @"..\..\escape.save"), "Filename traversal guard failed.");
                ctx.Assert(!File.Exists(outside) && !File.Exists(Path.Combine(ctx.RunDirectory, "escape.save")),
                    "Guard allowed a write outside the isolated profile.");
            });
        }
    }
}
