# CamView
Low-usage webcam monitor (WPF / .NET 8)

Native Windows app, no NuGet packages. Camera capture uses the built-in Windows
MediaCapture API, tries every stream/format the camera exposes (works with
cameras that only output H.264/MJPEG), and falls back gracefully.

## Features

### UI Settings
- Fullscreen
- Framless
- Always on top
- Lock aspect ratio
- Hide UI

### Opacity
The Opacity slider (1–100) fades **only the video** — see-through to the desktop
behind it — while the menu, stats, and title bar stay fully visible. Great for
overlaying a monitor feed on your work.

### Settings & Startup
Everything saves to `%AppData%\CamView\settings.json` and restores on launch
(camera, flips, rotation, fps, resolution, opacity, frameless, on-top, aspect
lock, window position/size). **Run at login** registers the app to start with
Windows; **Quit** (bottom of menu) closes it.

### Controls
Click ☰ (top-left) for the menu, or use keys:

| Key | Action |
|-----|--------|
| M | Open/close menu |
| H / V | Flip horizontal / vertical |
| R | Rotate 90° |
| [ / ] | Lower / raise frame rate (lower = less CPU) |
| F | Fullscreen (Esc exits) |
| B | Frameless (hide title bar) |
| T | Always on top |
| U | Hide all UI |

The menu/stats auto-hide after ~3 s of no mouse movement; move the mouse to
bring them back.

### Behavior Notes
- **Single instance:** launching a second copy just brings the running one
  forward (they can't both hold the camera).
- **Pauses when minimized:** capture stops while the window is minimized and
  resumes on restore, so CPU drops to near zero when hidden.
- **Self-healing:** if the camera is unplugged or grabbed by another app, CamView
  retries automatically and recovers when it's available again.

## How to install

### Option 1 - Download & run
Download the .exe from the release page. The pre-built exe is not signed and will trigger Microsoft Defender SmartScreen warning. If concerned, follow the steps below to build and run the app using Visual Studio.

### Option 2 - Standalone exe
Open the project (*CamView.csproj*) in Visual Studio. Select **"Tools"**, then **"Command Line"** > **"Developer PowerShell"** from the top toolbar. (*this will open a PowerShell window from the projects top directory*). Enter the command below to build the exe file. 

    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

Output location (the exe file): `bin > Release > net8.0-windows10.0.19041.0 > win-x64 > publish > CamView.exe`
(single ~90 to 200 MB file, runs on any 64-bit Windows 10/11 with nothing installed).

*Optional smaller exe file size: Use `--self-contained false` for a ~1 MB exe needing the .NET 8 Desktop Runtime.*

