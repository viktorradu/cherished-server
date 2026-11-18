# Cherished slideshow

Run `dotnet run` with the stable .NET 11 SDK and open the configured HTTP endpoint (by default
`http://localhost:8083`). Set `PoolSettings` in `appsettings.json`:

| Setting | Meaning |
| --- | --- |
| `PoolFolder` | Root image directory, including nested directories and known Windows cloud directories. Directory links/junctions are not followed. |
| `PoolFilter` | Comma-separated extensions, matched case-insensitively (for example `.jpg,.jpeg`). |
| `SlideshowIntervalMs` | Display time after each successful image load, 250 to 86400000 ms. |
| `RefreshIntervalMs` | Catalog refresh/recovery interval, 1000 to 86400000 ms; default 10000. |
| `StateFolder` | Writable directory for `.hidden`/`.flagged`; empty uses the application binary directory. |

A scan publishes a complete immutable catalog. Failed scans log an error and retain
the previous catalog; an unavailable folder at startup is retried without stopping
the host. Image IDs are opaque and stable for a given full path, not numeric positions.
`GET image/loadpool` returns `poolSize`, `slideshowIntervalMs`, and
`images: [{ id, label }]`. Use an `id` as the `key` for image, hide, and flag requests.
Metadata/image/action endpoints return 503 before the first successful scan; missing
or hidden images return 404. Original image bytes are served unchanged; directory
labels come from metadata, without requiring an external EXIF script.

Click the slideshow to pause/resume. Arrow keys or navigation buttons browse the
last 100 successfully displayed images; forward navigation reuses forward history
before choosing a new image. Hide and flag always target the visible image, even
when browsing history or another image is loading. Manual navigation is available
while paused and does not resume automatic playback.
The label is right-aligned at the bottom with no reserved pause slot. A separate
pause symbol overlays the foreground photo's top-right corner without shifting the label.
Manual navigation updates that label without resuming;
errors and action results remain visible alongside it.

Only one image/catalog load and one playback/retry/deadline wake-up are authoritative.
Requests have a 15-second deadline. Startup metadata failures retry with exponential
backoff and jitter, capped at 30 seconds. An image advance tries at most five distinct
candidates, then refreshes metadata after 30 seconds. Pausing cancels pending
automatic work; the last successful image stays visible during failures.
While the next image or catalog loads, the current image and status text stay
unchanged; the new image, background and label switch together once ready.
Empty catalogs and load/retry errors still display their status.
Ordinary advances also refresh metadata once it is 30 seconds old, so newly added
images become selectable without reloading the page. No background polling runs
while paused.

Hidden and flagged paths are stored in `.hidden` and `.flagged` in `StateFolder`
(beside the application binaries when unset). Writes are serialized and replaced atomically. Hidden files leave the
selectable catalog immediately and stay hidden across refreshes and restarts.
The application needs read access to the image tree and write access to its state
directory for these actions.

## Home Assistant temperature and humidity

The same web process/Windows service can display **Inside** and **Outside**
temperature and humidity in a small bottom-left panel. It is disabled by default;
HA failures or invalid optional settings never stop photo playback. Doorbell/video
integration is not included.
The panel shows only the Inside/Outside readings, without column headers or a
normal connection-status row; status text appears only for errors/disconnection.

Set this section in `appsettings.json` for local runs, or in the protected installed
`service-settings.json` for the Windows service:

```json
"HomeAssistant": {
  "Enabled": true,
  "Url": "https://ha.example.invalid",
  "TokenFile": "C:\\ProtectedConfiguration\\ha-token.txt",
  "InsideTemperature": "sensor.inside_temperature",
  "InsideHumidity": "sensor.inside_humidity",
  "OutsideTemperature": "sensor.outside_temperature",
  "OutsideHumidity": "sensor.outside_humidity",
  "RequestTimeoutSeconds": 15,
  "HeartbeatIntervalSeconds": 30
}
```

Replace the example URL and four distinct `sensor.*` entity IDs with your own.
The URL is the HTTP(S) base URL, optionally including a reverse-proxy prefix, not
`/api/websocket`; credentials/query parameters/fragments are rejected. Use HTTPS
outside a trusted local network: HTTP transmits the authentication token unencrypted.
TLS certificate validation is never disabled.

Supply a Home Assistant long-lived access token **only on the server**, either as
the process environment variable `HomeAssistant__AccessToken` or as a single line
in `TokenFile` (an absolute local path outside the application content directory).
The environment value takes precedence. Protect that file so only administrators
can write it and the app/service identity can read it; do not use the image/state
folder, `wwwroot`, source control or an installation template. Never paste the
token in chat, browser code, a URL or command-line arguments. A dedicated
non-administrator HA user with read access to these entities is sufficient; its token
inherits that user's permissions, not a special sensor-only scope. Restart the app
after changing configuration, then reload the slideshow.

The server uses the [documented HA WebSocket API](https://developers.home-assistant.io/docs/api/websocket/):
authenticate, subscribe to `state_changed`, then request `get_states`, buffering only
the four configured entities and merging events by HA timestamps to avoid snapshot
races. It resubscribes and fully refreshes on reconnect. HA application ping/pong
checks connection health independently of sensor activity. Setup/heartbeat requests
have a 1..120 second deadline; heartbeat interval is 1..300 seconds. Reconnect delay
starts at 1 second, doubles with jitter and is capped at 60 seconds. Reads are bounded
to 8 MiB per HA frame/message; logs intentionally omit HA response bodies/exception
details, endpoints and secrets.

Browser updates use native **Server-Sent Events**, not polling or slideshow timers:
`GET /sensors/events` sends a cached full snapshot on connection/reconnection.
Publication and subscription are atomic, each browser queue holds only its latest
snapshot, and at most 32 streams are accepted. SSE keepalives run every 15 seconds
with a 10-second write deadline; native browser reconnect uses a 5-second retry.
Reverse proxies must pass streaming responses without buffering and allow idle
connections. `GET /sensors/state` exposes the same safe readings/connectivity snapshot,
never HA URL, entity IDs, raw attributes or token. Like the existing photo endpoints,
these read-only endpoints have no application authentication; keep the listener
loopback/private or use a separately authenticated reverse proxy.

Readings preserve HA's units and HA-supplied `last_reported`/`last_updated` timestamps
(shown in the reading tooltip/accessibility label). Timestamps mean the last values
HA **delivered**: a quiet sensor need not emit a state-change event for every report.
Old timestamps alone do not mark a healthy connection stale. A lost HA/browser
connection retains the latest readings with an explicit stale indication; unknown,
unavailable, removed, missing or non-finite numeric states show **Unavailable**,
never a fabricated zero. The panel stays fixed in the bottom-left corner during
pause/resume and navigation.

## Windows Server service package

Generate a self-contained ZIP without installing a service:

```powershell
.\package-service.ps1 -PackageVersion 1.0.0
```

See [deployment instructions](packaging/windows/INSTALL.md) for the PnL-aligned
versioned-release model, install/update/remove entrypoints, virtual service identity
and protected configuration/state. PowerShell 7 is required for deployment.
The Windows service is named **Cherished Photos**. Installation settings also
accept `PoolFilter`, `SlideshowIntervalMs` and `RefreshIntervalMs`; updates preserve
the installed values. Previous `Cherished` services require the documented migration.
The application targets `net11.0` with Windows service hosting 11.0.0.
`global.json` selects SDK 11.0.100 or a stable servicing patch in that feature band.
Self-contained packages include the latest stable .NET/ASP.NET Core 11.0 patch
from Microsoft's release metadata (11.0.0 as verified November 13, 2026).

## Focused regression checks

No additional test packages are required:

```powershell
dotnet run --project tests\Catalog.Tests\Catalog.Tests.csproj
node --test tests\slideshow.test.mjs tests\sensors.test.mjs
dotnet build
```

The client checks use Node.js 22 or later with deterministic fake images, requests,
and timers; the server checks exercise real temporary directories, the refresh service,
and a controlled local HA WebSocket/SSE mock (no real HA credentials required).
