# CamView — low-usage webcam monitor (WPF / .NET 8)

Native Windows app, no NuGet packages. Camera capture uses the built-in Windows
MediaCapture API, tries every stream/format the camera exposes (works with
cameras that only output H.264/MJPEG), and falls back gracefully.

## Build & run
Open `CamView.csproj` in Visual Studio 2022 (.NET desktop workload) and press F5.

If the video is blank: Windows Settings → Privacy & security → Camera →
turn on "Let desktop apps access your camera".

## Standalone exe
From a Developer PowerShell in the project folder:

    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

Output: `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\CamView.exe`
(single ~90 MB file, runs on any 64-bit Windows 10/11 with nothing installed).
Use `--self-contained false` for a ~1 MB exe needing the .NET 8 Desktop Runtime.

## Window & title bar
The window opens with a slim custom title bar (app name + close button); drag it
to move the window. Turn on **Frameless** in the menu (or press B) to hide the
bar for a clean video-only look — then drag the video itself to move. Resize from
the grip at the bottom-right corner.

## Controls
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

## Opacity
The Opacity slider (1–100) fades **only the video** — see-through to the desktop
behind it — while the menu, stats, and title bar stay fully visible. Great for
overlaying a monitor feed on your work.

## Settings & startup
Everything saves to `%AppData%\CamView\settings.json` and restores on launch
(camera, flips, rotation, fps, resolution, opacity, frameless, on-top, aspect
lock, window position/size). **Run at login** registers the app to start with
Windows; **Quit** (bottom of menu) closes it.

## App icon
Put an `app.ico` next to `CamView.csproj`, then uncomment the
`<ApplicationIcon>app.ico</ApplicationIcon>` line in the csproj. Add
`Icon="app.ico"` to the `<Window>` tag in MainWindow.xaml for the title-bar icon.
An `.ico` (not `.png`) is required; make one at icoconvert.com or in GIMP, ideally
containing 16/32/48/256 px sizes.

## Behavior notes
- **Single instance:** launching a second copy just brings the running one
  forward (they can't both hold the camera).
- **Pauses when minimized:** capture stops while the window is minimized and
  resumes on restore, so CPU drops to near zero when hidden.
- **Self-healing:** if the camera is unplugged or grabbed by another app, CamView
  retries automatically and recovers when it's available again.

## Smaller build (optional)
Add `-p:PublishTrimmed=true` to the publish command to cut unused framework code
(smaller exe). Test the camera afterward — trimming occasionally removes something
the Windows Runtime needs via reflection; if capture breaks, drop the flag.
