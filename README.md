<p align="center">
  <img src="Assets/app.png" alt="TaskbarStats" width="128">
</p>

<h1 align="center">TaskbarStats</h1>

<p align="center">
  A lightweight system monitor that lives on the Windows 11 taskbar.<br>
  CPU, GPU, memory, disks, network and every sensor you own — usage and temperature at a glance.
</p>

<p align="center">
  <b>English</b> · <a href="README_KR.md">한국어</a>
</p>

<p align="center">
  <img src="docs/widget.png" alt="Widget on the taskbar">
</p>

## Features

- **Taskbar widget** with two lanes (top / bottom). Each module is built from items you choose: label, bar, slim bar, history graph, circle graph, core bars, percent, temperature, speeds, and your own sensor readouts.
- **Modules**: CPU, GPU, Memory, Disks, Network, Sensors, plus adjustable spacers.
- **Click popups** per module, with an Acrylic backdrop that follows the widget theme:
  - CPU: per-core bars, history, frequency (all / efficiency / performance cores), top processes, uptime
  - GPU: temperature, VRAM, clocks, fan, power, history, top GPU processes
  - Memory: used / free / cache / commit / swap, history, top processes
  - Disks: every drive, read / write history, top file-I/O processes, drive temperature
  - Network: local IP, up / down history, connected adapters
  - Sensors: every sensor LibreHardwareMonitor can read, grouped by hardware, live
- **Sections page** in the settings turns each popup section on or off and sets how many processes are listed.
- **Any sensor on the taskbar**: pick any temperature, fan, load, clock or power sensor for a readout, bar, graph or ring.
- **Fluent settings window** (Mica) with drag-and-drop layout, font, size and weight, °C / °F, and rpm units.
- **Light on resources**: sensors are read only while something shows them, popups collect data only while open, and the idle footprint stays small.
- **Boot Camp Macs**: temperatures and fans through Apple's SMC driver (read only). Switched on automatically the first time the driver is detected.

## Install

1. Download `TaskbarStats-Setup-<version>.exe` from [Releases](../../releases). A portable zip is also provided.
2. Run the installer. It can download and install the [PawnIO](https://pawnio.eu) driver for you (recommended, selected by default) and register a startup task.

CPU and motherboard sensors need administrator rights and the PawnIO driver. Windows counters (usage, memory, disks, network) and NVIDIA GPU data work without either.

**Run as administrator** (settings → General) is on by default: when you open the app it asks for administrator rights, and with the startup task enabled it starts elevated without a UAC prompt.

## Build

Requires the .NET 10 SDK on Windows.

```
dotnet build -c Release
```

Installer (requires [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```
dotnet publish TaskbarStats.csproj -c Release -r win-x64 --self-contained -o publish
iscc /DAppVersion=1.0.0 /DSourceDir=..\publish installer\TaskbarStats.iss
```

Pushing a `v*` tag builds the installer and portable zip on GitHub Actions and attaches them to the release.

## License

Apache License 2.0 — Copyright 2026 NSteven. See [LICENSE](LICENSE) and [NOTICE](NOTICE).

Third-party software: [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0), HidSharp (Apache-2.0), System.Management (MIT), the Xanmono font (SIL OFL 1.1). The optional [PawnIO](https://pawnio.eu) driver is a separate project by namazso and is downloaded from its official source, never bundled. Licenses are in [`licenses/`](licenses/).

Apple, NVIDIA, AMD, Intel and Windows are trademarks of their owners. This project is not affiliated with them.
