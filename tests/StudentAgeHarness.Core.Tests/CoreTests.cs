using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using StudentAgeHarness.Engine;
using Xunit;

namespace StudentAgeHarness.Core.Tests
{
    public sealed class CoreTests
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "sah-test-root");

        private static JObject SettingsJson() => new JObject
        {
            ["format"] = RunSettings.FormatId,
            ["runId"] = "test-run"
        };

        private static RunSettings Settings() => RunSettings.Parse(SettingsJson().ToString(), Root);

        [Fact]
        public void SettingsDefaultToSafeSmoke()
        {
            RunSettings value = Settings();
            Assert.Equal(new[] { "startup" }, value.Scenarios);
            Assert.True(value.Mute);
            Assert.True(value.RedactPaths);
            Assert.Equal(Path.Combine(Root, "profile"), value.ProfileDirectory);
        }

        [Theory]
        [InlineData("runId", "")]
        [InlineData("window", "hidden")]
        [InlineData("format", "old-format")]
        public void InvalidSettingsFailClosed(string key, string value)
        {
            JObject json = SettingsJson();
            json[key] = value;
            Assert.ThrowsAny<Exception>(() => RunSettings.Parse(json.ToString(), Root));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(double.PositiveInfinity)]
        public void TimeoutMustBeFinitePositive(double timeout)
        {
            JObject json = SettingsJson();
            json["timeoutSec"] = timeout;
            Assert.Throws<InvalidDataException>(() => RunSettings.Parse(json.ToString(), Root));
        }

        [Fact]
        public void SettingsSupportNumericWorkshopIdsAndPluginFolders()
        {
            JObject json = SettingsJson();
            json["workshopMods"] = new JArray(123456, "987654");
            json["pluginsUnderTest"] = new JArray(new JObject
            {
                ["path"] = "BepInEx/plugins/MyMod", ["kind"] = "folder"
            });
            json["packs"] = new JArray("packs/Example.dll");
            var settings = RunSettings.Parse(json.ToString(), Root);
            Assert.Equal(new[] { "123456", "987654" }, settings.WorkshopMods);
            Assert.True(settings.PluginsUnderTest.Single().IsFolder);
        }

        [Fact]
        public void WorkshopInheritanceIsExplicit()
        {
            JObject json = SettingsJson();
            json["workshopMods"] = "inherit";
            Assert.True(RunSettings.Parse(json.ToString(), Root).InheritWorkshopMods);
            Assert.False(Settings().InheritWorkshopMods);
        }

        [Fact]
        public void PathContainmentDoesNotConfuseSiblingPrefixes()
        {
            Assert.True(RunPaths.IsInside(Path.Combine(Root, "file"), Root));
            Assert.False(RunPaths.IsInside(Root + "-other", Root));
        }

        [Fact]
        public void RedactionIsRecursiveAndLongestPrefixFirst()
        {
            string game = @"D:\Games\StudentAge";
            string run = game + @"\runs\one";
            PathRedactor redactor = PathRedactor.Create(run, game, @"C:\Private\LocalLow", @"C:\Private");
            var json = new JObject
            {
                ["path"] = run + @"\report.json",
                ["nested"] = new JArray(@"d:/games/studentage/file", @"C:\Private\config")
            };
            redactor.Apply(json);
            Assert.Equal(@"<run>\report.json", (string)json["path"]);
            Assert.Equal("<game>/file", (string)json["nested"][0]);
            Assert.Equal(@"%USERPROFILE%\config", (string)json["nested"][1]);
        }

        private static ScenarioEntry Entry(string name, string[] tags = null, bool alone = false) => new ScenarioEntry
        {
            Name = name,
            Attribute = new HarnessScenarioAttribute(name) { Tags = tags, RunAlone = alone }
        };

        [Fact]
        public void SelectionHonorsTagsManualAndDeduplicates()
        {
            var auto = Entry("auto", new[] { "smoke" });
            var manual = Entry("manual", new[] { "manual" });
            var report = new HarnessReport(Settings());
            var selected = ScenarioCatalog.Select(new List<ScenarioEntry> { auto, manual },
                new[] { "*", "tag:smoke", "manual", "auto" }, report, out var explicitSelection);
            Assert.Equal(new[] { auto, manual }, selected);
            Assert.Contains(manual, explicitSelection);
        }

        [Fact]
        public void UnknownSelectionCannotSilentlyPass()
        {
            var report = new HarnessReport(Settings());
            ScenarioCatalog.Select(new List<ScenarioEntry> { Entry("good") },
                new[] { "good", "typo", "tag:missing" }, report, out _);
            Assert.Equal(2, report.CountBySeverity(HarnessSeverity.Error));
        }

        [Fact]
        public void RunAloneConflictIsReported()
        {
            var report = new HarnessReport(Settings());
            var result = ScenarioCatalog.Select(new List<ScenarioEntry> { Entry("one", alone: true), Entry("two") },
                new[] { "*" }, report, out _);
            Assert.Single(result);
            Assert.Equal(1, report.CountBySeverity(HarnessSeverity.Error));
        }

        [Fact]
        public void ReportCarriesStatusesAndExtensions()
        {
            var report = new HarnessReport(Settings());
            var scenario = report.BeginScenario("demo", "example", "test", new[] { "smoke" });
            scenario.Status = "passed";
            var step = report.FindOrCreateStep("check");
            step.Status = "passed";
            step.Screenshots.Add("screenshots/001.png");
            report.SetExtension("mymod.result", new { answer = 42 });
            JObject json = report.ToJson();
            Assert.Equal(HarnessReport.SchemaId, (string)json["schema"]);
            Assert.Equal(1, (int)json["summary"]["passedScenarios"]);
            Assert.Equal(1, (int)json["summary"]["screenshots"]);
            Assert.Equal(42, (int)json["extensions"]["mymod.result"]["answer"]);
        }

        [Fact]
        public void FixtureCleanupHonorsKeep()
        {
            string root = Path.Combine(Path.GetTempPath(), "sah-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var fixtures = new HarnessFixtures(root, "current", Path.Combine(root, "profile", "Mods"));
                string first = fixtures.CreateLocalMod("one");
                string kept = fixtures.CreateLocalMod("two");
                fixtures.Keep(kept);
                var report = new HarnessReport(Settings());
                fixtures.CleanupAll(report);
                Assert.False(Directory.Exists(first));
                Assert.True(Directory.Exists(kept));
                Assert.Equal(0, report.CountBySeverity(HarnessSeverity.Warning));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void NestedRoutinesAreDisposedOnAbort()
        {
            bool disposed = false;
            IEnumerator Child()
            {
                try { yield return null; yield return null; }
                finally { disposed = true; }
            }
            IEnumerator Parent() { yield return Child(); }
            IEnumerator flat = RoutineDriver.Flatten(Parent());
            Assert.True(flat.MoveNext());
            ((IDisposable)flat).Dispose();
            Assert.True(disposed);
        }
    }
}
