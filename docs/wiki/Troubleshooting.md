# Troubleshooting

**The True Multiplayer tab doesn't appear.**
Experimental Mode must be on (WorldBox settings, then restart). Check that
`NeoModLoader.dll` is in `worldbox_Data/StreamingAssets/mods/` and the mod is in
`Mods/WorldBoxMultiplayer/` with `mod.json` next to `WorldboxMP.Client.dll`.

**"Version mismatch: this server speaks WBMP/x, your mod speaks WBMP/y."**
Your mod and the server are different versions. Run the installer again to update.

**"Can't reach the server. Retrying..."**
The server is down or blocked. If you host it yourself, open the port for **both UDP and
TCP**. Otherwise check **Server settings** points where you expect.

**"Signed in from another game - this one was disconnected."**
The same account logged in somewhere else. Only one game per account can be online.

**The world looks different from my friend's.**
Press **Resync** in the room window. The Owner's world is the reference; resync fetches it
again. Followers also re-check everything on their own every 30 seconds.

**I was kicked or banned.**
The reason is shown in the window. Bans are set by the server's operators; ask them.

**NeoModLoader shows errors about LiteNetLib or WorldboxMP.Shared.**
Those two DLLs must be inside `Mods/WorldBoxMultiplayer/lib/`, not next to
`WorldboxMP.Client.dll`. Reinstall with the installer to fix the layout.

**Where are the logs?**
WorldBox's own log: `%USERPROFILE%\AppData\LocalLow\mkarpenko\WorldBox\Player.log`. Lines
from the mod start with `[WBMP]`.
