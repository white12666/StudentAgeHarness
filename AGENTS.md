# StudentAge Harness development

- Core must not reference Assembly-CSharp or any particular mod. Game-specific code belongs in the adapter; mod-specific tests belong in packs.
- Never deploy the harness into the real game plugin directory. Use `tools/sah.cmd`; never bypass its activation/recovery checks.
- Canonical version: `Directory.Build.props`. Before a code-changing local plugin build, run `tools/Set-HarnessVersion.ps1 -Bump patch`. Verify built and isolated deployed DLL versions.
- Build: `dotnet build StudentAgeHarness.sln -c Release`.
- Unit tests: `dotnet test tests/StudentAgeHarness.Core.Tests -c Release --no-build`.
- Launcher tests: `tests/Test-Launcher.ps1`. These must write only a uniquely generated disposable registry key, never the player's key.
- Game tests require a running Steam client, an idle game and a renderable Windows desktop. Preserve user work. Use exit codes plus report/screenshots, not logs alone.
- `tools/Pack-Release.ps1` packages existing version-matched binaries only. Do not include Unity, game or BepInEx DLLs, private run data, or registry snapshots.
- PowerShell 5.1 scripts containing Chinese require UTF-8 BOM. Use PS 5.1-compatible syntax.
- No automatic publishing or pushes. If a push is explicitly authorized, use GitHub account `white12666`, following the parent repository instructions.
