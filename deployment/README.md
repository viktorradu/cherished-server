# Windows service deployment

Deployment now follows the cherished-pnl model. Use the root
[`package-service.ps1`](../package-service.ps1) generator and
[`packaging/windows/INSTALL.md`](../packaging/windows/INSTALL.md) instructions.
Packages include separate install/update/uninstall entrypoints, an installation
settings template, versioned releases and a file-hash manifest.

The previous in-place installer and LocalService identity have been replaced.
Existing installations require deliberate migration; see the installation guide.
No existing service, configuration or state is changed by generating a package.
