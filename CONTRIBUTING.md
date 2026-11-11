# Contributing to WorldBox Multiplayer

Thanks for helping! Whatever you have time for is welcome:

- **Play and report.** Bugs, crashes and especially worlds that look different for different
  players. [Open an issue](https://github.com/AlizerUncaged/worldbox-true-multiplayer/issues/new/choose).
- **Code.** Sync coverage, UI, the server, the admin panel, the installer.
- **Docs.** The wiki sources live in [`docs/wiki/`](docs/wiki); fixes there are pull requests too.
- **Art.** Icons, banners and UI graphics (pixel art that fits WorldBox).
- **Mods.** Build something on the [SDK](https://github.com/AlizerUncaged/worldbox-true-multiplayer/wiki/SDK-Overview)
  and tell us what the API is missing.

Questions and ideas go to [Discussions](https://github.com/AlizerUncaged/worldbox-true-multiplayer/discussions).

## Setup

You need Windows, WorldBox on Steam, NeoModLoader (started once, so it generates the
assemblies the mod compiles against), the .NET 10 SDK and Python 3.

```bash
git clone https://github.com/AlizerUncaged/worldbox-true-multiplayer
cd worldbox-true-multiplayer
export WORLDBOX_DIR="/path/to/steamapps/common/worldbox"   # or keep the repo at <WorldBox>/Projects/

./run_server.sh                                                 # local server on 127.0.0.1:40441
dotnet build src/Client/WorldboxMP.Client.csproj -c Debug       # builds and installs the mod into the game
```

Debug builds connect to your local server. Server settings go in a git-ignored
`appsettings.Local.json` (see [Hosting a Server](https://github.com/AlizerUncaged/worldbox-true-multiplayer/wiki/Hosting-a-Server)).

## Testing sync with two game instances

Most changes touch what players see, so test with two clients:

1. Create an empty `.dev` file in the WorldBox folder. Debug clients then sign in as
   `testp1`, `testp2`, ... automatically (add a line `console` for a debug console window).
2. Start WorldBox from Steam, then start `worldbox.exe` a second time.
3. Each instance serves a dev bridge on `http://localhost:8750`, `8751`, ... Join both to a
   lobby (`/joinroom?id=0001`), drive the world (`/spawn`, `/speed`, `/power`), then pause
   the Owner (`/speed?n=0`) and compare `/actors?deep=1` and `/meta` from both. They should
   match.

The full list of bridge endpoints and how the sync works is in
[Extending the Mod](https://github.com/AlizerUncaged/worldbox-true-multiplayer/wiki/Extending-the-Mod).

## Pull requests

- Branch from `main`, keep each pull request focused on one change.
- Match the surrounding code: naming, comment style, and the existing patterns for hooks
  (`GameHooks`), sending (`MirrorManager.OnKing...`) and applying (`Apply...`).
- If you change what goes over the wire in an incompatible way, bump
  `NetProtocol.ConnectKey`.
- Say how you tested it (two instances, which scenario, what you compared).
- Never commit secrets: settings with passwords belong in `appsettings.Local.json`,
  which git ignores.

By contributing you agree that your work is released under the [MIT License](LICENSE).

## Be kind

Be respectful and constructive with everyone. We're here to make a fun game more fun.
