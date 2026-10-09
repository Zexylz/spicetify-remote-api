# Spicetify Remote API

A local HTTP API for controlling the Spotify desktop client through a Spicetify extension.

```
any app  --HTTP-->  spicetify-remote-api.exe (127.0.0.1:7979)  <--WebSocket--  extension/remote-api.js (inside Spotify)
```

## Install

Windows only. Requires [Spicetify](https://spicetify.app). Paste this into PowerShell:

```powershell
iwr -useb https://raw.githubusercontent.com/Zexylz/spicetify-remote-api/main/install.ps1 | iex
```

Or download the repository and double-click `install.cmd`. Either way the installer:

1. builds the bridge with the C# compiler that ships with Windows (no Node needed)
2. installs it to `%LOCALAPPDATA%\SpicetifyRemoteApi`, starts it hidden and adds it to Windows startup
3. installs the extension and runs `spicetify apply`

Run the installer again to update.

## Uninstall

```powershell
iwr -useb https://raw.githubusercontent.com/Zexylz/spicetify-remote-api/main/uninstall.ps1 | iex
```

Or double-click `uninstall.cmd`.

Open `http://127.0.0.1:7979/remote` for the built-in web remote.

When Spotify updates itself it removes the Spicetify patch; run `spicetify backup apply` to restore it.

Environment: `SPICE_API_PORT` (default 7979), `SPICE_API_HOST` (default 127.0.0.1), `SPICE_API_TOKEN` (if set, requires `Authorization: Bearer <token>` or `?token=`).

## Endpoints

Arguments can be sent as a JSON body or query string. Action routes accept GET and POST and return the new player state.

| Route | Arguments |
| --- | --- |
| `GET /status` | |
| `GET /lyrics` | full lyrics, `sync_type` is `LINE_SYNCED`, `UNSYNCED` or `NONE` |
| `GET /lyrics/current` | previous / current / next line at the current position |
| `GET /events` | Server-Sent Events stream of state updates |
| `/play` | optional `uri` |
| `/pause`, `/toggle`, `/next`, `/previous` | |
| `/seek` | `position_ms`, `percent` (0-100) or `delta_ms` |
| `/volume` | `volume` (0-100) or `delta` |
| `/mute`, `/shuffle`, `/like` | optional `state` (true/false), toggles otherwise |
| `/repeat` | optional `mode` (`off`, `context`, `track`), cycles otherwise |

`state.can` reports which of next, previous, seek, shuffle and repeat Spotify currently allows; blocked actions return an error.

```
curl -X POST localhost:7979/volume -d "{\"volume\": 40}"
curl -X POST "localhost:7979/seek?delta_ms=-10000"
curl localhost:7979/lyrics/current
```

## License

GPL-3.0, see [LICENSE](LICENSE).
