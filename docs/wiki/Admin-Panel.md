# Admin Panel

An optional web dashboard for server operators, served by the game server itself on its
own port.

<p align="center"><img src="https://raw.githubusercontent.com/AlizerUncaged/worldbox-multiplayer/main/docs/images/screens/admin-light.png" width="720"></p>

## Turn it on

In `appsettings.Local.json` (or your deployment's settings file):

```json
{
  "Admin": {
    "Enabled": true,
    "Bind": "127.0.0.1",
    "Port": 40480,
    "User": "admin",
    "Password": "a-long-random-password"
  }
}
```

`Password` (8+ characters) only **seeds** the first admin on first start; change it later
from the panel's Settings page. To force it back to the configured value, start once with
`"ResetPassword": true`.

## What it does

| Page | |
|---|---|
| **Overview** | players online, rooms, active bans, uptime; send an on-screen announcement to everyone |
| **Online now** | who is connected, their room and role, address, ping; kick from room, disconnect, ban |
| **Accounts** | search all accounts; sign out everywhere, reset password, ban |
| **Rooms** | every lobby and room with members; close or clear it |
| **Bans** | account and address bans (1 hour to permanent), with reasons; lift them |
| **Settings** | change your admin password |

Light and dark themes follow your system and can be toggled.

## Keep it safe

The panel is plain HTTP unless `Https:CertificatePath` is set. Either:

- keep `Bind` at `127.0.0.1` and reach it through an SSH tunnel:
  `ssh -L 40480:127.0.0.1:40480 you@server`, then open <http://localhost:40480>; or
- serve it over HTTPS (a PFX certificate, or a TLS reverse proxy such as nginx or Caddy).

Sessions are HttpOnly, SameSite=Strict cookies; every write needs a custom header, and
strict security headers (CSP, no framing) are set on all pages.
