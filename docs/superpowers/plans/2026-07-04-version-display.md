# App Version Display and Tray Header Layout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give RAIVEN a build-time version number (from a `VERSION` file, future-CI-bumpable) and show it in the tray menu's header panel, which is rearranged to logo-left / name-and-version-stacked-right.

**Architecture:** A `VERSION` file at the repo root feeds MSBuild's `<Version>` property via a new `Directory.Build.props` (applies to both projects automatically); the running app reads that back through .NET's own assembly version metadata via a tiny `AppVersion` helper - no runtime file access. `TrayContext`'s header panel is rearranged to use it.

**Tech Stack:** .NET 10 SDK / MSBuild property functions, WinForms (`System.Drawing`, `System.Windows.Forms`).

**Spec:** `docs/superpowers/specs/2026-07-04-version-display-design.md`

## Global Constraints

- `VERSION` file (repo root): single line, exactly `1.0.0`.
- `Directory.Build.props` (repo root) **already exists** (sets `Nullable`/`ImplicitUsings`/`LangVersion` for the whole repo) - this plan adds a new `<PropertyGroup>` to it, it does not replace the file. It sets MSBuild `<Version>` from the `VERSION` file's trimmed contents, falling back to `1.0.0` if the file is missing.
- `AppVersion.Display`: `typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"` - reads assembly metadata, never touches the `VERSION` file at runtime.
- Header panel stays 200x78. Logo 48x48 at `Location = (12, 15)`. "RAIVEN" label at `Location = (70, 18)`, `Size = (120, 22)`, bold, `Color.FromArgb(196, 152, 255)`, `TextAlign = ContentAlignment.MiddleLeft`. Version label at `Location = (70, 40)`, `Size = (120, 18)`, regular weight, `Color.FromArgb(150, 150, 150)`, `TextAlign = ContentAlignment.MiddleLeft`, text `$"v{AppVersion.Display}"`. Both labels use explicit `Location`/`Size` (no `Dock`). Panel `BackColor = Color.Black` and `Cursor = Cursors.Hand` on all three clickable controls (logo, RAIVEN label, version label) are unchanged from today.
- No new automated tests (per spec section 5: build config + a one-line reflection read + a WinForms layout change have no branching logic worth unit testing, and this project has no UI test harness). Verification is manual: build, inspect the tray header, click each control, check the exe's file-properties Version tab.
- Commit style: `feat:`/`docs:` prefix, ends with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. Never `--no-verify`.
- Run tests with `dotnet test tests/Raiven.Core.Tests` (must stay green - this plan doesn't add to it, but Task 1/2 must not break it). Build the app with `dotnet build src/Raiven.App`.

---

### Task 1: VERSION file, Directory.Build.props, and AppVersion helper

**Files:**
- Create: `VERSION` (repo root)
- Modify: `Directory.Build.props` (repo root - existing file, add a `PropertyGroup`, do not remove the existing one)
- Create: `src/Raiven.App/AppVersion.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `public static class Raiven.App.AppVersion { public static string Display { get; } }` - Task 2 uses `AppVersion.Display`.

- [ ] **Step 1: Create the VERSION file**

Create `E:\Repos\RAIVEN\VERSION` with exactly this content (one line, no leading/trailing blank lines beyond the file's own newline):

```
1.0.0
```

- [ ] **Step 2: Add the Version property group to the existing Directory.Build.props**

`E:\Repos\RAIVEN\Directory.Build.props` already exists with this content:

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

Replace it with (adds a second `PropertyGroup`; the first is unchanged):

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>

  <PropertyGroup>
    <RaivenVersionFile>$(MSBuildThisFileDirectory)VERSION</RaivenVersionFile>
    <Version Condition="Exists('$(RaivenVersionFile)')">$([System.IO.File]::ReadAllText('$(RaivenVersionFile)').Trim())</Version>
    <Version Condition="!Exists('$(RaivenVersionFile)')">1.0.0</Version>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Create the AppVersion helper**

Create `E:\Repos\RAIVEN\src\Raiven.App\AppVersion.cs`:

```csharp
namespace Raiven.App;

public static class AppVersion
{
    public static string Display { get; } =
        typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
```

- [ ] **Step 4: Build and verify the version flowed through**

Run: `dotnet build src/Raiven.App`
Expected: build succeeds, 0 warnings, 0 errors.

Run: `dotnet build src/Raiven.App -getProperty:Version`
Expected output: `1.0.0`

- [ ] **Step 5: Run the existing test suite to confirm nothing broke**

Run: `dotnet test tests/Raiven.Core.Tests`
Expected: PASS (same counts as before this change - `Directory.Build.props` applies to `Raiven.Core.Tests` too, but only sets a version property, nothing test-visible).

- [ ] **Step 6: Commit**

```bash
git add VERSION Directory.Build.props src/Raiven.App/AppVersion.cs
git commit -m "feat: build-time version from VERSION file, read back via AppVersion

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Tray header layout - logo left, RAIVEN + version stacked right

**Files:**
- Modify: `src/Raiven.App/TrayContext.cs:184-248` (`CreateHeaderItem` method)

**Interfaces:**
- Consumes: `Raiven.App.AppVersion.Display` (Task 1).
- Produces: nothing new for later tasks - this is the plan's last task.

- [ ] **Step 1: Replace `CreateHeaderItem`**

In `src/Raiven.App/TrayContext.cs`, replace the entire method (currently lines 184-248, from `private static ToolStripControlHost CreateHeaderItem(ContextMenuStrip owner)` through its closing brace, just before `private static Bitmap? LoadHeaderBitmap(int size)`) with:

```csharp
    private static ToolStripControlHost CreateHeaderItem(ContextMenuStrip owner)
    {
        const int logoSize = 48;
        const int panelWidth = 200;
        const int panelHeight = 78;
        const int textLeft = 70;
        const int textWidth = 120;

        var panel = new Panel
        {
            Width = panelWidth,
            Height = panelHeight,
            BackColor = Color.Black,
            Cursor = Cursors.Hand,
        };

        var picture = new PictureBox
        {
            Image = LoadHeaderBitmap(logoSize),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(logoSize, logoSize),
            Location = new Point(12, (panelHeight - logoSize) / 2),
            Cursor = Cursors.Hand,
            BackColor = Color.Transparent,
        };

        var label = new Label
        {
            Text = "RAIVEN",
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
            ForeColor = Color.FromArgb(196, 152, 255),
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(textLeft, 18),
            Size = new Size(textWidth, 22),
            Cursor = Cursors.Hand,
        };

        var versionLabel = new Label
        {
            Text = $"v{AppVersion.Display}",
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Regular),
            ForeColor = Color.FromArgb(150, 150, 150),
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(textLeft, 40),
            Size = new Size(textWidth, 18),
            Cursor = Cursors.Hand,
        };

        panel.Controls.Add(picture);
        panel.Controls.Add(label);
        panel.Controls.Add(versionLabel);

        void OpenRepository(object? sender, EventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(RepositoryUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                FileLog.Error("Failed to open RAIVEN repository link", ex);
            }
            owner.Close();
        }

        panel.Click += OpenRepository;
        picture.Click += OpenRepository;
        label.Click += OpenRepository;
        versionLabel.Click += OpenRepository;

        return new ToolStripControlHost(panel)
        {
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            AutoSize = false,
            Size = new Size(panelWidth, panelHeight),
        };
    }
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Raiven.App`
Expected: build succeeds, 0 warnings, 0 errors.

- [ ] **Step 3: Run the existing test suite**

Run: `dotnet test tests/Raiven.Core.Tests`
Expected: PASS, same counts as before this task (this change is App-only WinForms layout - no Core code touched).

- [ ] **Step 4: Manual verification**

Launch the built app (quit any already-running RAIVEN instance first - it holds a single-instance mutex and the loopback port):

```
& "E:\Repos\RAIVEN\src\Raiven.App\bin\Debug\net10.0-windows10.0.17763.0\Raiven.App.exe"
```

Right-click the tray icon to open the menu. Confirm:
- The header panel shows the logo on the left, "RAIVEN" (bold, purple) to its right, and "v1.0.0" (smaller, gray) directly beneath "RAIVEN".
- Clicking the logo, the "RAIVEN" text, or the "v1.0.0" text each closes the menu and opens the GitHub repo in the browser.
- Right-click the exe in Windows Explorer → Properties → Details tab → "File version"/"Product version" reads `1.0.0`.

Quit the test instance afterward (tray menu → Quit).

- [ ] **Step 5: Commit**

```bash
git add src/Raiven.App/TrayContext.cs
git commit -m "feat: tray header shows version, logo left of name

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```
