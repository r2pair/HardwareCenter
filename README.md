# HardwareCenter

A hardware monitor and system dashboard for Windows 10 and 11 (64-bit): every sensor your PC
exposes, in a dark dashboard that is easy to read at a glance.

## Download and run

1. Download `HardwareCenter-<version>-win-x64.zip` from the [Releases](../../releases) page.
2. Unzip it and run `HardwareCenter.exe`. There is no installer and nothing else to install.

Windows SmartScreen may warn that the app is from an unknown publisher, because the exe is not
code-signed. Choose **More info**, then **Run anyway**.

## What it shows

- **Overview**: CPU, GPU and memory gauges, key temperatures, fans, drives and a live network chart
- **Processor, Graphics, Memory, Storage, Network, Motherboard**: headline figures, history charts
  and every sensor on the device
- **Thermals**: every temperature and fan in one place
- **All sensors**: value, minimum, maximum and trend for everything, with a filter
- **System**: what is installed (board, CPU, memory modules, GPUs, displays, drives, adapters, audio)

It can minimize to the notification area, where the icon's tooltip shows CPU and GPU load and
temperature, and it can start with Windows.

## Sensor access

| How it is started | What you get |
|---|---|
| Normally | Load, GPU, memory, network, drive temperature and activity, CPU speed |
| As administrator | Adds drive health (SMART) |
| As administrator, with the PawnIO driver | Adds CPU temperature, power and clocks, and motherboard fans, voltages and temperatures |

[PawnIO](https://pawnio.eu/) is the free signed driver Libre Hardware Monitor uses. Install it from
the app's Settings page, or with `winget install namazso.PawnIO`.

## Tested on

Developed and tested on one PC: AMD Ryzen 7 7800X3D, NVIDIA GeForce RTX 4080 SUPER, Windows 11.
Intel processors, laptops and other graphics cards should work, since the sensor library supports
them, but have not been tested. Reports are welcome.

## Command line

- `--page <name>` opens on a page, for example `--page Thermals`
- `--minimized` starts in the notification area
- `--report <file> [--detailed]` writes a text report and exits
- `--no-elevate` ignores the "always start as administrator" setting once

Settings are stored in `%LOCALAPPDATA%\HardwareCenter`.

## Building

Needs the .NET 10 SDK.

    powershell -ExecutionPolicy Bypass -File build.ps1

This produces a single self-contained `dist\HardwareCenter.exe`.

## Credits and licence

HardwareCenter is released under the [MIT licence](LICENSE).

Sensor readings come from
[LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0).
