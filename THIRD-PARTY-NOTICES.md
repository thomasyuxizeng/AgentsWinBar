# Third-party notices

CodexBarWin is released under the MIT License (see `LICENSE.txt`). It builds on the work below. Each component keeps
its own license; the notices here are for convenience, and the license files shipped with each package are the
authoritative text.

## Code and data this project ports or bundles

| Component | Used for | License |
| --- | --- | --- |
| [CodexBar](https://github.com/steipete/CodexBar) by Peter Steinberger | The usage readers in `Services/Native/` are ports of CodexBar's fetchers and normalizers, and `Services/Native/Cost/Pricing/pricing-overlay.json` is a port of its built-in price tables. The optional WSL route runs the CodexBar CLI itself. | MIT |
| [models.dev](https://models.dev) | `Services/Native/Cost/Pricing/models-dev-prices.json` is a snapshot of its model prices, refreshed at run time. | MIT |
| [CodexBarWin](https://github.com/nek0der/CodexBarWin) by nek0der | This project started as a fork of it. | MIT |

## NuGet packages shipped with the app

| Package | License |
| --- | --- |
| CommunityToolkit.Mvvm | MIT |
| H.NotifyIcon.WinUI, H.NotifyIcon, H.GeneratedIcons.System.Drawing | MIT |
| ZstdSharp.Port | MIT |
| Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Logging (and .Abstractions, .Debug) | MIT |
| System.Drawing.Common | MIT |
| Microsoft.WindowsAppSDK (and its component packages) | Microsoft Software License Terms for the Windows App SDK (in the package's `license.txt`) |
| Microsoft.Web.WebView2 (dependency of the Windows App SDK) | BSD-style Microsoft license (in the package's `LICENSE.txt`) |

## Build-time only (not shipped)

| Package | License |
| --- | --- |
| MinVer | Apache-2.0 |
| Microsoft.Windows.SDK.BuildTools | Microsoft Windows SDK license |
| MSTest, Moq, AwesomeAssertions (tests) | MIT, BSD-3-Clause, Apache-2.0 |
