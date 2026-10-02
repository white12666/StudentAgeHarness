using BepInEx;

namespace StudentAgeHarness.Validation
{
    [BepInPlugin("com.studentage.harness.fixture", "Harness Fixture Mod", HarnessVersion.Value)]
    public sealed class FixturePlugin : BaseUnityPlugin
    {
        private void Awake() => Logger.LogInfo("Fixture mod loaded.");
    }
}
