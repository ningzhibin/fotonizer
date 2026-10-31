# Fotonizer · Automatic Photo Organizer

[![GitHub release](https://img.shields.io/github/v/release/ningzhibin/fotonizer?color=2ea44f)](https://github.com/ningzhibin/fotonizer/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0078D4.svg)](#requirements)

[中文](README.md) | English

Fotonizer is a Windows tray app that automatically organizes photos from USB drives, SD cards, cameras, and phones. It copies and renames files by capture time, removes duplicates, picks the best shot from bursts, enhances photos locally, and can back them up to Google Photos.

![Fotonizer main window](docs/images/home.png)

## Features

- **Automatic device import** — watches removable drives and MTP/PTP cameras/phones, then imports new photos without manual steps.
- **Copy + deduplicate** — SHA-256 and quick fingerprints prevent importing the same photo twice.
- **Rename and archive** — default `2024-05-01-101500.jpg` in a `year\month` folder; templates are customizable.
- **Per-camera folders** — optional root folders such as `Sony ILCE-7M3\` or `Canon EOS R6\`.
- **Burst selection** — scores sharpness, exposure, and faces, then keeps the best frame.
- **Local auto enhance** — exposure, white balance, contrast, saturation, and sharpness, processed entirely on your PC.
- **Google Photos backup** — Google Photos Library API upload with monthly albums; tokens are protected with Windows DPAPI.
- **Chinese / English UI** — switch language instantly in General Settings.
- **Tray, notifications, pause, CLI** — designed to stay out of the way.

## Download

Download the latest self-contained Windows x64 build from [GitHub Releases](https://github.com/ningzhibin/fotonizer/releases/latest).

1. Download and extract `Fotonizer-win-x64-1.0.5.zip`.
2. Run `Fotonizer.exe`.
3. Choose a photo library folder in the welcome screen.
4. Plug in a device — Fotonizer keeps running in the system tray.

## Requirements

- Windows 10 version 2004 (build 19041) or later, x64.
- No .NET runtime installation is required for the released build.
- For building from source: .NET 8 SDK.

The executable is not code-signed. Windows SmartScreen may show a warning; choose **More info → Run anyway**.

## Google Photos

Uploading requires your own Google OAuth client because the app uses the `photoslibrary.appendonly` scope:

1. Create a Google Cloud project and enable the Photos Library API.
2. Configure an OAuth consent screen and create a **Desktop app** OAuth client.
3. Paste the client ID and secret into **Google Photos** in Fotonizer and click **Connect Google Photos**.

Fotonizer can only upload and view content it created; it cannot read your existing Google Photos library.

## Build from source

```powershell
dotnet restore
dotnet build -c Release
dotnet test tests/Fotonizer.Tests/Fotonizer.Tests.csproj -c Release
dotnet publish src/Fotonizer.App/Fotonizer.App.csproj -c Release -r win-x64 --self-contained true -o publish/Fotonizer-win-x64
```

## Privacy

All photo processing happens locally. Photos are only sent to Google Photos when you explicitly connect and enable upload. Settings, the local index database, and logs stay in `%APPDATA%\Fotonizer` and `%LOCALAPPDATA%\Fotonizer`. See [Data, privacy, and uninstall](README.md#数据隐私与卸载) (Chinese) for details.

## License

MIT — see [LICENSE](LICENSE). Third-party notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
