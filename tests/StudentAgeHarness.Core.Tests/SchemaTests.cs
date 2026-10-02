using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using StudentAgeHarness.Engine;
using Xunit;

namespace StudentAgeHarness.Core.Tests
{
    public sealed class SchemaTests
    {
        private static JsonSchema Schema(string name) =>
            JsonSchema.FromText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schemas", name)));

        private static JsonObject Report()
        {
            var report = new HarnessReport(new RunSettings
            {
                RunId = "schema-test", RunDirectory = Path.GetTempPath()
            });
            report.BeginScenario("test", null, "test", new string[0]).Status = "passed";
            report.FindOrCreateStep("check").Status = "passed";
            return JsonNode.Parse(report.ToJson().ToString()).AsObject();
        }

        [Fact]
        public void RealSerializerMatchesSchema()
        {
            var result = Schema("report-v2.schema.json").Evaluate(Report());
            Assert.True(result.IsValid);
        }

        [Theory]
        [InlineData("missing-summary")]
        [InlineData("unknown-status")]
        [InlineData("escaped-artifact")]
        public void InvalidReportsAreRejected(string change)
        {
            JsonObject report = Report();
            switch (change)
            {
                case "missing-summary": report.Remove("summary"); break;
                case "unknown-status": report["scenarios"][0]["status"] = "maybe"; break;
                case "escaped-artifact": report["run"]["screenshots"] = new JsonArray("../secret.png"); break;
            }
            Assert.False(Schema("report-v2.schema.json").Evaluate(report).IsValid);
        }

        [Fact]
        public void ExampleConfigurationMatchesSchema()
        {
            JsonNode config = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "harness.example.json")));
            Assert.True(Schema("harness.schema.json").Evaluate(config).IsValid);
        }

        [Fact]
        public void ValidateGameReportsWhenProvided()
        {
            string paths = Environment.GetEnvironmentVariable("SAH_REPORTS");
            if (string.IsNullOrWhiteSpace(paths)) return;
            var schema = Schema("report-v2.schema.json");
            foreach (string path in paths.Split(';'))
            {
                JsonNode report = JsonNode.Parse(File.ReadAllText(path));
                var result = schema.Evaluate(report, new EvaluationOptions { OutputFormat = OutputFormat.List });
                Assert.True(result.IsValid, Path.GetFileName(Path.GetDirectoryName(path)) + ": " +
                    JsonSerializer.Serialize(result));
            }
        }
    }
}
