# Cherished Server Windows service

This deployment follows the cherished-pnl service model: a self-contained win-x64
ZIP, a shared `deploy-service.ps1` lifecycle, separate install/update/uninstall
entrypoints, versioned read-only releases, protected external configuration,
a virtual service account, delayed automatic startup/recovery and an idempotent
startup task. Package generation does not deploy anything.

## Requirements and package generation

Windows x64 and **PowerShell 7** are required on the deployment server. No .NET
SDK or separately installed .NET runtime is needed there. Generate the ZIP from
the checkout with the stable .NET **11.0.100** SDK (`global.json` allows servicing
patches in that feature band, not previews). The application targets `net11.0`:

```powershell
.\package-service.ps1 -PackageVersion 1.0.0
# Optional deterministic runtime/offline metadata choice:
.\package-service.ps1 -PackageVersion 1.0.0 -RuntimeVersion 11.0.0 -Force
```

Default output is `artifacts\CherishedServer-WebService-1.0.0-win-x64.zip`.
The runtime defaults to the latest stable .NET 11 servicing release discovered
from Microsoft's release metadata. `package-manifest.json` records package/runtime
versions and SHA-256 file hashes; deployment verifies those hashes before copying.
This detects corruption, not authenticity: use a trusted transfer channel and verify
the ZIP SHA-256 printed at build time. The ZIP/scripts are not signed or an MSI.

No workstation `appsettings*.json`, mark files, source, debug symbols or logs are
packaged. The old `scripts\New-ServicePackage.ps1 -Version 1.0.0` command forwards
to this generator, but package names/layout and the PowerShell requirement changed.
Framework-dependent and ARM64 packages are not part of this PnL-aligned model.

The framework and Windows service hosting dependency use **.NET 11.0.0**, the
latest stable release verified against Microsoft's official release metadata
on November 13, 2026. Preview/RC versions are rejected by SDK and runtime selection.
Self-contained deployments include both .NET and ASP.NET Core; rebuild/redeploy
packages to adopt subsequent .NET 11 servicing patches.

## Install

Extract the ZIP to an ordinary local folder. Review/unblock scripts according to
your execution/signing policy. Open elevated PowerShell 7:

```powershell
Copy-Item .\install-settings.example.json .\install-settings.json
notepad .\install-settings.json
.\Install-Service.ps1 -SettingsPath .\install-settings.json -WhatIf
.\Install-Service.ps1 -SettingsPath .\install-settings.json
```

The template intentionally contains no machine-specific paths. Set:

| Setting | Meaning |
| --- | --- |
| `ImageDirectory` | Absolute photo directory, for example `D:\\Photos`, or a UNC share. Existing images are never moved or copied. |
| `StateDirectory` | Dedicated writable local directory, for example `D:\\CherishedState`. Existing `.hidden`/`.flagged` files are preserved. Created if absent. |
| `Port` | Loopback HTTP port, integer 1024..65535; default 8083. |
| `PoolFilter` | Optional comma-separated extensions beginning with a dot, matched case-insensitively; default `.jpg,.jpeg`. |
| `SlideshowIntervalMs` | Optional display interval in milliseconds, integer 250..86400000; default 3000. |
| `RefreshIntervalMs` | Optional catalog refresh interval in milliseconds, integer 1000..86400000; default 10000. |

These pool settings are copied into `PoolSettings` in the installed
`service-settings.json`. Older installation files omitting them retain the
defaults above; supplied null, non-integer timing or invalid extension values are
rejected before deployment. For example:

```json
{
  "ImageDirectory": "D:\\Photos",
  "StateDirectory": "D:\\CherishedState",
  "Port": 8083,
  "PoolFilter": ".jpg,.jpeg,.png",
  "SlideshowIntervalMs": 5000,
  "RefreshIntervalMs": 30000
}
```

These are first-install settings. Updates preserve the installed filter and timings,
even when the new package template has different defaults. To change an existing
installation, edit its `service-settings.json` and restart the service.

Directories must be separate, non-nested paths, not drive/share roots. Deployment
and state paths must not traverse any reparse points. Image paths may traverse
documented `IO_REPARSE_TAG_CLOUD`/`CLOUD_1`..`CLOUD_F` directories used by OneDrive,
but never junctions, symbolic links, other name-surrogate tags or unknown tags.
The installer reads the actual Windows reparse tag, rather than treating every
ReparsePoint attribute as a junction. The catalog likewise traverses known cloud
subdirectories and skips directory links/unsupported reparse points.
A temporarily unavailable
photo directory is allowed: the host remains up and catalog scanning retries.
Do not use an interactive user's mapped drive.

Default deployment root is `%ProgramData%\CherishedServer\WebService`. All
entrypoints accept `-DeploymentRoot` for a dedicated alternative ending in
`\WebService`; use the same root for every operation. `-Elevate` requests UAC
approval from non-elevated PowerShell 7. `-WhatIf` performs read-only validation;
it does not copy files, register services/tasks, grant ACLs or create event sources.

Installation starts and checks service **Cherished Photos** (both internal and
display name) as the passwordless, non-administrator virtual account
**NT SERVICE\Cherished Photos**. It configures delayed
automatic start, restart-on-failure and task
**\CherishedServer\Cherished Photos-StartAtBoot** as SYSTEM. The task only starts the
service, never a second web process; deployment verifies that the process is unchanged.

The protected deployment root grants full control only to administrators/SYSTEM
and read/execute to the service SID. The service receives Modify only on the chosen
state folder and read/execute on an available local image folder. Existing unrelated
data ACLs are retained. The installer does not change remote share permissions:
virtual accounts authenticate remotely as the **server's computer account**.
Grant that account share and NTFS read access separately, or provision a different
identity through a deliberate operator-managed deployment rather than editing
the managed service account in place. The ownership checks intentionally reject
a service with an unexpected identity.

### OneDrive/cloud-backed photo folders

An image path such as `F:\OneDrive\Pictures Backup` is allowed when its reparse
points have known cloud tags. Cloud-backed folders are not automatic ACL targets:
the installer reports that it skipped image permission changes. Provision read,
list and traverse access for `NT SERVICE\Cherished Photos` separately on the server
after reviewing the cloud folder's permissions. Do not place deployment or writable
state under OneDrive.

Before deploying, mark the photo tree **Always keep on this device** and verify
that all required originals are downloaded. The service runs outside the signed-in
OneDrive user's session; an online-only placeholder may fail to hydrate even when
that user can open it. Accepting cloud directory tags does not guarantee service
access, hydration or continued synchronization. For unattended reliability, use a
fully local photo mirror readable by the service account. Catalog/image failures
remain explicit and recoverable; no installer operation downloads or hydrates photos.

If path validation still fails, its error includes the actual reparse tag and
offending ancestor. Known cloud tags are allowed only for image reads; unsupported
tags require investigation, not disabling the guard. If a previously ACL-granted
local image path becomes cloud-backed, removal refuses to mutate its ACL through
that reparse path; review/revoke the old SID grant manually before completing removal.
After confirming that the old grant has been revoked, an administrator can set
`ImageAccessGranted` to `false` in the protected `deployment.json` and rerun removal.
Do not clear that ownership record while the grant still exists.

Browse **http://127.0.0.1:8083** on the server (or your port). Binding is loopback-only.
There is no application authentication; do not expose image/hide/flag endpoints
to an untrusted network. No firewall rules, URL reservations, certificates or
remote-access configuration are created. Use a separately secured reverse proxy
for remote access.

## Update, settings and status

```powershell
# Extract the newer ZIP elsewhere, then:
.\Update-Service.ps1
.\deploy-service.ps1 -Action Status
Get-Service 'Cherished Photos'
Restart-Service 'Cherished Photos'
```

Updates preserve installed settings, paths, port, mark files and service identity.
They copy binaries into a new `releases\<timestamp>-<suffix>` directory, switch the
owned service to that release, start it and verify HTTP plus the startup task.
Every update starts the service, matching PnL's lifecycle, even if it was stopped.
Old releases are retained; they are not overwritten or pruned.

Protected `service-settings.json` and `deployment.json` live outside releases.
The former contains `PoolSettings` (folder/filter/timings/state), Kestrel's loopback
endpoint, logging and host settings; it is loaded explicitly using `--ServiceSettings`.
The service command also specifies Production and its release content root, so
SCM's `System32` working directory cannot redirect static assets or configuration.
Environment variables and explicit command-line configuration retain precedence.

### Optional Home Assistant readings

The existing **Cherished Photos** service also hosts the optional temperature/humidity
bridge and browser event stream; no extra service, firewall rule or deployment task
is needed. First installations write `HomeAssistant: { Enabled: false }`. Edit the
protected `service-settings.json` after installation to enable it:

```json
"HomeAssistant": {
  "Enabled": true,
  "Url": "https://ha.example.invalid",
  "TokenFile": "C:\\ProgramData\\CherishedServer\\WebService\\ha-token.txt",
  "InsideTemperature": "sensor.inside_temperature",
  "InsideHumidity": "sensor.inside_humidity",
  "OutsideTemperature": "sensor.outside_temperature",
  "OutsideHumidity": "sensor.outside_humidity",
  "RequestTimeoutSeconds": 15,
  "HeartbeatIntervalSeconds": 30
}
```

Use the actual deployment root if different. Create the single-line token file as
an administrator **inside the protected deployment root, outside `releases`**.
It must inherit administrator/SYSTEM write access and read-only access for
`NT SERVICE\Cherished Photos`; verify its ACL before storing the token. Never put
the token in `install-settings.json`, `service-settings.json`, source, `wwwroot`, the
photo/state folder or a package. An alternative is the service process environment
variable `HomeAssistant__AccessToken`, provisioned securely by the operator. Do not
pass a token on the service command line or paste it into chat. It is sent only
server-to-HA; use HTTPS with a trusted certificate or a trusted local HTTP network.

The four distinct sensor IDs and URL are non-secret configuration; a dedicated HA
user with read access suffices, with no administrator/action access required by
this integration. Restart the service and reload the slideshow after configuration.
Missing/invalid configuration, authentication failures and HA outages are isolated
from photo playback, explicitly logged without credentials and shown in the sensor
panel. The bottom-left panel keeps stale readings on disconnect, preserves HA units
and timestamps, and never treats quiet sensors as disconnected.

Installation settings remain the existing photo/path/filter/timing model; HA is
configured in the protected service settings rather than forwarding secrets through
the installer or UAC arguments. Updates preserve **the whole installed settings file
byte-for-byte**, including HomeAssistant and unknown operator additions, and leave
the external token file untouched. A token file is reread on HA reconnect. Do not
place it under a versioned release.

Browser delivery uses same-origin Server-Sent Events (`/sensors/events`) with cached
initial state, bounded client queues and keepalives; `/sensors/state` is a read-only
safe snapshot. Neither endpoint contains HA credentials or private entity attributes.
Secure these endpoints with the same network/reverse-proxy policy as photos; disable
proxy response buffering for SSE. There is no doorbell/video integration.
See the repository README for API semantics, validation bounds and recovery behavior.

Edit filter/timings or the loopback port in `service-settings.json`, then restart.
Do not edit recorded image/state paths or service identity in place; these require
explicit migration of configuration, marks and ACLs. Use the update entrypoint,
not the first-install template. The installer rejects unrelated services/tasks,
unrecognized nonempty roots, unsafe paths and occupied ports before stopping anything.

The extracted ZIP can be removed after installation. An installed copy of
`deploy-service.ps1` and its helper supports `-Action Status`/`Remove` without it.
Source-checkout deployment also works using `deploy-service.ps1 -ImageDirectory
... -StateDirectory ...`; it generates/verifies a temporary package first.

## Verification, failure and removal

```powershell
Invoke-RestMethod 'http://127.0.0.1:8083/image/loadpool'
Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='Cherished Photos' } -MaxEvents 20
.\Uninstall-Service.ps1 -WhatIf
.\Uninstall-Service.ps1
```

Catalog 503 means the first scan has not succeeded; installation reports it as
not-ready rather than a host failure. An empty successful scan returns a zero-sized
catalog. Check image/share permissions and availability; scanning retries without
terminating the host. Startup/verification errors are explicit, not success.
Inspect Application sources `Cherished Photos`, `.NET Runtime`, `Application Error`, and
SCM events in the System log for configuration, port and permission failures.

Removal deletes only the owned service and owned startup task, revokes their
explicit state/local-image SID grants, and restores the private root to
administrators/SYSTEM. Images, mark files, configuration, releases, event source
and event history remain. Do not manually remove shared data or event resources.

There is no automatic rollback. Back up the deployment and state directories
before update. If a failure occurs after switching releases, use the retained
release and recorded settings to restore the previous service command as an
administrator; do not claim deployment success until HTTP/task checks succeed.

### Migration from the earlier in-place installer

The old LocalService registration and `.cherished-install.json` layout are not
silently adopted. Stop/unregister the old service deliberately with its original
package, back up configuration and marks, and install to an empty WebService root.
Point `StateDirectory` to the preserved external marks or copy legacy
binary-adjacent `.hidden`/`.flagged` files there first. Reapply relevant pool
filter/timing settings to the new protected settings file. The new installer refuses
an existing unrelated service rather than replacing it.

### Migration from the previous Cherished service name

Windows service names cannot be renamed in place. The new installer refuses to
proceed while service `Cherished` or task `\CherishedServer\Cherished-StartAtBoot`
exists, and does not adopt its old deployment manifest.

Back up the old deployment and state directory. Use the **previous package's**
uninstaller with its original deployment root to stop/remove the owned old service
and task and revoke its old SID grants. Keep the preserved state directory.
Install this package to a new empty directory ending in `\WebService`, using the
same image/state directories and desired pool settings. After installation, merge
other required operator settings from the backup into the new
`service-settings.json`, keeping `ServiceName` as `Cherished Photos`, then restart.
The deployment root and ZIP names are not renamed automatically. Do not copy the
old `deployment.json` over the new one. The new virtual identity gets its own grants;
remote-share computer-account access must still be provisioned separately.
Retain the old backup/releases/event history until recovery is no longer needed.

## Checks without deploying a service

```powershell
.\tests\Deployment.Tests.ps1 -Package .\artifacts\CherishedServer-WebService-1.0.0-win-x64.zip
dotnet run --project tests\Catalog.Tests\Catalog.Tests.csproj
```

Checks verify ZIP hashes, content, settings/wrapper preservation, owned lifecycle
previews, full install/update/remove logic with in-memory service/task/ACL adapters,
and console-mode HTTP/static/state behavior. They do not register/start
a real Windows service, create tasks, mutate ACLs or change firewall/server settings.
