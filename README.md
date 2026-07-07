# UCH mod loader

A BepInEx mod manager for Ultimate Chicken Horse. Avalonia UI over a plain C# core.

## Requirements
- Visual Studio 2022 (17.8+) with the ".NET desktop development" workload
- Optional: the "Avalonia for Visual Studio 2022" extension for the XAML previewer

## Getting started
1. Open `UCHModLoader.sln`
2. Set `UCHModLoader.App` as the startup project (right-click → Set as Startup Project)
3. Press F5. NuGet packages restore automatically on first build.

The app starts against a **mock mod repository** (fake mods, downloads are empty
zips) so everything is clickable before any real infrastructure exists. Once you
set a server URL in the loader (Upload tab → Login with Discord), it switches to
the live server automatically. See the Server section below.

## Layout
- `UCHModLoader.Core` — no UI dependencies. Steam locator, BepInEx manager,
  GitHub index repository, install manager (state tracking, dependency
  resolution, enable/disable), Steam launcher.
- `UCHModLoader.App` — Avalonia UI. `App.axaml.cs` is the composition root
  where interfaces are wired to implementations.

## Conventions
- Mods are zips extracted to `BepInEx/plugins/<ModId>/`
- Install state lives at `%AppData%/UCHModLoader/installed.json`
- Disable = rename `.dll` → `.dll.disabled` (BepInEx skips them)
- Dependencies resolve and install/upgrade silently
- Version constraints: `*`, `1.2.3`, `>=1.2.3`, `>1.2.3`, `<=1.2.3`, `<1.2.3`


## Server (UCHModLoader.Server)

ASP.NET Core API backed by MongoDB (metadata) + GridFS (mod zips and icons).

### Setup
1. Fill in `UCHModLoader.Server/appsettings.json`:
   - `Mongo:ConnectionString` and `Mongo:Database`
   - `Discord:ClientId` / `Discord:ClientSecret` from your app at
     discord.com/developers/applications (OAuth2 → add redirect
     `http://localhost:5178/api/auth/discord/callback`)
   - `PublicBaseUrl` — where clients reach the server
2. Run the server (set as startup project, or `dotnet run` in its folder)
3. In the loader's Upload tab: set the server URL, Login with Discord,
   paste the token shown in the browser, choose a .dll or .zip, add an
   icon/description/dependencies, Upload.
4. Restart the loader (or Check updates) — the mod appears in Browse
   with its icon. The loader switches from the mock repository to the
   server automatically once a server URL is saved in settings.

### Upload pipeline
DLL is inspected via System.Reflection.Metadata (never executed) to read
[BepInPlugin(guid, name, version)] — guid/name/version come from the file,
not from user input. First upload of a guid claims ownership for that
Discord account. Server generates manifest.json, packages the zip, stores
it in GridFS, and records a SHA-256 that the loader verifies on install.

### Moderation
- Set `Hidden: true` on a mod document to pull it from the index instantly
- Set `Banned: true` on a user document to block their uploads

### Tip: run App + Server together
Right-click the solution → Configure Startup Projects → Multiple startup
projects → set both UCHModLoader.App and UCHModLoader.Server to Start.
