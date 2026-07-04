# App version display and tray header layout

Date: 2026-07-04
Status: Approved

## Goal

Give RAIVEN a visible version number, sourced from a single file so a future
GitHub Actions build can bump it, and rebalance the tray menu's header panel
to make room for it: logo on the left, "RAIVEN" and the version stacked to
its right.

## 1. Version source

- New file `VERSION` at the repo root: a single line, exactly `1.0.0`
  (three-part major.minor.patch; no prefix, suffix, or required trailing
  newline beyond what a normal text editor writes).
- Checked into git like any other source file. A future CI workflow can
  bump it (e.g. rewrite the file before `dotnet build`/`publish`) - not
  built as part of this work.

## 2. Build-time wiring

`Directory.Build.props` already exists at the repo root (it currently only
sets `Nullable`/`ImplicitUsings`/`LangVersion` for every project). MSBuild
auto-applies it to every project below it, so adding a version property
group there reaches both `Raiven.Core` and `Raiven.App` with no `.csproj`
edits and no change to its existing settings:

```xml
<Project>
  <PropertyGroup>
    <RaivenVersionFile>$(MSBuildThisFileDirectory)VERSION</RaivenVersionFile>
    <Version Condition="Exists('$(RaivenVersionFile)')">$([System.IO.File]::ReadAllText('$(RaivenVersionFile)').Trim())</Version>
    <Version Condition="!Exists('$(RaivenVersionFile)')">1.0.0</Version>
  </PropertyGroup>
</Project>
```

Setting `<Version>` is what feeds .NET's standard `AssemblyVersion` /
`FileVersion` / `InformationalVersion` generation during compilation - no
other MSBuild changes needed. A missing `VERSION` file falls back to
`1.0.0` instead of breaking the build.

## 3. Runtime read - via assembly metadata, not the file

New `AppVersion` static helper in `Raiven.App`:

```csharp
namespace Raiven.App;

public static class AppVersion
{
    public static string Display { get; } =
        typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
```

The running app never opens the `VERSION` file itself - it reads back the
version .NET already baked into the assembly at build time
(`Version.ToString(3)` turns the 4-part assembly version `1.0.0.0` back
into the 3-part `"1.0.0"`). This sidesteps the launch-directory fragility
this codebase already hit once with Kokoro's model path - RAIVEN launches
from different working directories (dev, install folder, Windows
autostart), so runtime file reads relative to the process are exactly the
kind of thing to avoid.

## 4. Tray header layout

`TrayContext.CreateHeaderItem` (`src/Raiven.App/TrayContext.cs`) changes
from today's top-logo/bottom-label vertical stack to a left-logo,
right-text-stack layout. Panel size is unchanged (200x78):

- **Logo** (`PictureBox`, 48x48): left-aligned, vertically centered -
  `Location = (12, 15)` (`(78-48)/2 = 15`).
- **"RAIVEN" label**: to the right of the logo at `Location = (70, 18)`,
  `Size = (120, 22)`, bold, `Color.FromArgb(196, 152, 255)` (today's purple,
  unchanged), `TextAlign = ContentAlignment.MiddleLeft`.
- **New "v1.0.0" label**: directly below it at `Location = (70, 40)`,
  `Size = (120, 18)`, regular weight, `Color.FromArgb(150, 150, 150)` (dim
  gray - reads as secondary info next to the bold purple name), same font
  family as the RAIVEN label but not bold, `TextAlign =
  ContentAlignment.MiddleLeft`, text = `$"v{AppVersion.Display}"`.
- All three controls (`picture`, RAIVEN label, new version label) keep the
  existing click-to-open-repository behavior - the version label is wired
  to `OpenRepository` alongside the other two.
- Both labels move from `Dock = DockStyle.Bottom`/implicit positioning to
  explicit `Location`/`Size` (manual layout, `Dock` removed). Everything
  else about the panel and its controls carries over unchanged: panel
  `BackColor = Color.Black`, `Cursor = Hand` on all three clickable
  controls, transparent label backgrounds.

## 5. Testing

- No new automated tests: this is build configuration
  (`Directory.Build.props`) plus a one-line `Assembly.GetName().Version`
  read and a WinForms layout rearrangement - no branching logic worth a
  unit test, and layout can't be meaningfully asserted without a UI test
  harness this project doesn't have.
- Manual verification: build, confirm the tray header shows "RAIVEN" /
  "v1.0.0" in the new left-logo/right-text layout, confirm all three
  header controls (logo, RAIVEN text, version text) still open the GitHub
  repo on click, and confirm Windows Explorer's file-properties "Details"
  tab for `Raiven.App.exe` reports version 1.0.0.
