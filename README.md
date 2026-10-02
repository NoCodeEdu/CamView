# CamView

A lightweight, low-resource webcam viewer for Windows — built for always-on monitoring. Single self-contained `.exe`, no installer, no dependencies to set up.

## Features

- **Live webcam view** with flip (horizontal / vertical) and 90° rotation
- **Low-usage controls** — adjustable frame rate and resolution to keep CPU use minimal
- **Adjustable opacity** — fade the video to see through to your desktop, while the controls stay visible (great as an overlay)
- **Frameless mode** — hide the title bar for a clean, video-only window
- **Always on top**, minimize / maximize, and fullscreen
- **Aspect-ratio lock** — keep the window matched to the camera's aspect while resizing
- **Auto-hiding controls** — the menu and stats fade out when idle and return on mouse movement
- **Run at login** — start automatically with Windows
- **Remembers your settings** between sessions
- **Resilient** — pauses capture when minimized, recovers automatically if the camera is unplugged or taken by another app, and works with cameras that only output compressed (H.264 / MJPEG) streams

## Requirements

- 64-bit **Windows 10** (version 2004 / build 19041) or **Windows 11**
- A webcam
- Camera access enabled for desktop apps: **Settings → Privacy & security → Camera → Let desktop apps access your camera**

The runtime is bundled into the `.exe`, so nothing else needs to be installed.

## Download & run

1. Go to the [**Releases**](../../releases) page and download `CamView.exe`.
2. Double-click to run.

On first launch, Windows SmartScreen may show "Windows protected your PC" because the app isn't code-signed. Click **More info → Run anyway**. (This only happens for files downloaded from the internet.)

## Controls

Click the **☰** menu (top-left), or use keyboard shortcuts:

| Key | Action |
|-----|--------|
| M | Open / close menu |
| H / V | Flip horizontal / vertical |
| R | Rotate 90° |
| [ / ] | Lower / raise frame rate |
| F | Fullscreen (Esc exits) |
| B | Frameless (hide title bar) |
| T | Always on top |
| U | Hide all UI (right-click the video to restore) |

Drag the title bar — or the video itself in frameless mode — to move the window.

## Building from source

Requires **Visual Studio 2022** with the **.NET desktop development** workload (includes the .NET 8 SDK).

Open `CamView.csproj` and press F5, or build a standalone executable from a terminal in the project folder:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The result is a single `CamView.exe` in `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\`.

## Tech

WPF • .NET 8 • C# • Windows `MediaCapture` API

## License

MIT
