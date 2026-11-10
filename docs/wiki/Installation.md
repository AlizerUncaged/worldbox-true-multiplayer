# Installation

You need **WorldBox on Steam** (Windows). Everyone in a room must run the same version of
the mod.

## One-click installer (recommended)

1. Download `WorldBoxMultiplayer-Installer.exe` from the
   [latest release](https://github.com/AlizerUncaged/worldbox-multiplayer/releases).
2. Close WorldBox and run the installer. It finds your WorldBox folder through Steam (any
   Steam library drive). If it can't, click **Browse** and pick `worldbox.exe`.
3. Click **Install**. It downloads and installs:
   - the latest **NeoModLoader** into `worldbox_Data/StreamingAssets/mods/`
   - the latest **WorldBox Multiplayer** into `Mods/WorldBoxMultiplayer/`
4. In WorldBox, turn on **Experimental Mode** in the settings and restart the game.

Run the installer again any time to update both. It replaces the old mod folder, so no
stale files are left behind.

The installer needs nothing else installed (it uses the .NET Framework built into
Windows 10 and 11). It talks only to GitHub and sends no personal information.

### Command line

```
WorldBoxMultiplayer-Installer.exe --install [--game "C:\path\to\worldbox"] [--log install.log]
```

Exit code `0` = installed, `1` = failed, `2` = WorldBox not found.

## Manual install

1. **NeoModLoader:** download `NeoModLoader.dll` from the
   [NML releases](https://github.com/WorldBoxOpenMods/ModLoader/releases/latest) and put it in
   `<WorldBox>/worldbox_Data/StreamingAssets/mods/`. If you have `NCMS_memload.dll` there,
   remove it.
2. **The mod:** download `WorldBoxMultiplayer-<version>.zip` from the
   [releases](https://github.com/AlizerUncaged/worldbox-multiplayer/releases) and extract it
   into `<WorldBox>/Mods/WorldBoxMultiplayer/`. The folder should look like this:

   ```
   Mods/WorldBoxMultiplayer/
     WorldboxMP.Client.dll
     mod.json
     icon.png
     shrek.glb
     lib/
       LiteNetLib.dll
       WorldboxMP.Shared.dll
   ```

   Keep `LiteNetLib.dll` and `WorldboxMP.Shared.dll` inside `lib/`. NeoModLoader treats
   every DLL at the top of a mod folder as a separate mod and complains about these two.
3. Turn on **Experimental Mode** in WorldBox's settings and restart.

## Check that it works

A **True Multiplayer** tab appears in the toolbar, and **F9** opens the multiplayer window
anywhere in the game. The mod also shows up with its icon in NeoModLoader's mod list.

Next: [Playing Together](Playing-Together).
