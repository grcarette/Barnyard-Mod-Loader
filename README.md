# Barnyard

A mod loader for Ultimate Chicken Horse.

Barnyard lets you browse and install community mods, group them into profiles
for different kinds of play, and keep them updated. If you make mods, you can
upload and manage them from inside the app.

## Installing

### Windows

1. Grab the latest `win-x64` zip from the [releases page](../../releases/latest)
2. Extract it anywhere and run the exe

Windows will probably show a "Windows protected your PC" popup the first time,
since the exe isn't code-signed. Click "More info", then "Run anyway".

When you first open Barnyard it will look for your Steam install of UCH and ask
to install BepInEx if you don't have it. BepInEx is the framework that actually
loads the mods, so say yes. After that you can install mods from the Browse tab.

### Linux / Steam Deck (experimental)

1. Download the `linux-x64` zip from the [releases page](../../releases/latest) and extract it
2. Run:
   ```
   chmod +x Barnyard.App
   ./Barnyard.App
   ```
3. After BepInEx installs, the app shows a launch options string. Copy it into
   Steam under right-click UCH -> Properties -> Launch Options. Mods won't load
   until you do this. You only have to do it once.

### macOS (experimental)

1. Download the Mac zip for your machine from the [releases page](../../releases/latest).
   `osx-arm64` for Apple Silicon, `osx-x64` for Intel.
2. macOS blocks unsigned apps on first open. Right-click the app and choose
   Open, then confirm. It opens normally after that.
3. Same launch options step as Linux, the app will walk you through it.

The Linux and Mac builds run fine but I haven't been able to fully test mod
injection on real hardware yet. If something breaks, please
[open an issue](../../issues).

## Using it

The sidebar has four tabs. Installed shows what you have and lets you
enable/disable things or check for updates. Browse is where you find and
install new mods. The profile button in the top left lets you set up different
sets of mods and switch between them (a "chaos night with friends" profile vs
a "normal" one, for example). Installing a mod pack from Browse adds its mods
to your current profile.

Launch game is always in the top right.

## Uploading mods

Log in with Discord (top left), then go to the Upload tab. You can upload any
BepInEx plugin as a .dll or .zip, give it a name, icon, description, and tags,
and push updates later without worrying about version numbers. There's also an
option to make a mod private and share it with specific people using one-time
keys, or a shareable multi-use key that expires after 48 hours.

Uploads from new accounts get reviewed before they show up publicly. After
your first approval you're verified and future uploads go live immediately.

## Requirements

- Steam version of Ultimate Chicken Horse
- Windows 10/11, or Linux/macOS with the experimental builds
- A Discord account if you want to upload, vote, or use private mods.
  Browsing and installing don't need an account.

## License

MIT, see [LICENSE](LICENSE).
