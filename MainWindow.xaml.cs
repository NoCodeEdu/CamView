using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace CamView
{
    public class AppSettings
    {
        public string CameraName { get; set; } = "";
        public int Fps { get; set; } = 15;
        public int Width { get; set; } = 640;
        public int Height { get; set; } = 480;
        public bool FlipH { get; set; }
        public bool FlipV { get; set; }
        public int Rotation { get; set; }          // 0 / 90 / 180 / 270
        public bool HideUi { get; set; }
        public bool Frameless { get; set; }
        public bool Topmost { get; set; }
        public int Opacity { get; set; } = 100;    // 1-100
        public bool LockAspect { get; set; }
        public double WinX { get; set; } = 100;
        public double WinY { get; set; } = 100;
        public double WinW { get; set; } = 820;
        public double WinH { get; set; } = 600;
    }

    public partial class MainWindow : Window
    {
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "CamView";

        static readonly int[] FpsSteps = { 1, 2, 5, 10, 15, 24, 30 };
        static readonly (int W, int H)[] ResSteps =
            { (320, 240), (640, 480), (1280, 720), (1920, 1080) };

        readonly string _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CamView", "settings.json");

        AppSettings S = new AppSettings();

        MediaCapture _capture;
        MediaFrameReader _reader;
        MediaFrameSource _source;
        DeviceInformationCollection _cams;
        string _activeDeviceId;          // camera currently in use (for restart/auto-heal)
        bool _pausedForMinimize;         // capture stopped while minimized
        bool _starting;                  // guards overlapping StartCameraAsync calls

        WriteableBitmap _wb;
        byte[] _pixels;
        SoftwareBitmap _pendingFrame;
        int _uiBusy;
        long _lastFrameTicks;
        int _vidW, _vidH;               // native size of the current video frames

        // ---- Win32 interop: aspect-locked resize + frameless maximize ----
        const int WM_SIZING = 0x0214;
        const int WM_GETMINMAXINFO = 0x0024;
        const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MINMAXINFO
        {
            public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        bool _suppressEvents;
        bool _uiHidden;
        bool _idleHidden;
        bool _fullscreen;
        Rect _preFullBounds;

        readonly DispatcherTimer _idleTimer =
            new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        readonly DispatcherTimer _toastTimer =
            new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        readonly DispatcherTimer _formatTimer =
            new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };

        public MainWindow()
        {
            InitializeComponent();
            _idleTimer.Tick += (s, e) =>
            {
                if (MenuTgl.IsChecked == true) { ResetIdleTimer(); return; }
                _idleHidden = true;
                UpdateUiVisibility();
            };
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };

            _formatTimer.Tick += async (s, e) =>
            {
                _formatTimer.Stop();
                await RestartForFormatChangeAsync();
            };

            StateChanged += MainWindow_StateChanged;
        }

        async void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                // Stop decoding frames while hidden — drops CPU to near zero.
                if (_reader != null || _capture != null)
                {
                    _pausedForMinimize = true;
                    await StopCameraAsync();
                }
            }
            else if (_pausedForMinimize)
            {
                _pausedForMinimize = false;
                await StartCameraAsync(_activeDeviceId ?? SelectedDeviceId());
            }

            UpdateMaxButtonGlyph();
        }

        void UpdateMaxButtonGlyph()
        {
            // Swap between "maximize" (□) and "restore" (❐) glyphs.
            if (MaxBtn != null)
            {
                bool max = WindowState == WindowState.Maximized;
                MaxBtn.Content = max ? "\u2750" : "\u25A1";
                MaxBtn.ToolTip = max ? "Restore" : "Maximize";
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var src = (HwndSource)PresentationSource.FromVisual(this);
            src?.AddHook(WndProc);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                // Frameless + AllowsTransparency windows otherwise cover the taskbar
                // when maximized. Clamp the maximized size to the monitor work area.
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (mon != IntPtr.Zero)
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    if (GetMonitorInfo(mon, ref mi))
                    {
                        RECT work = mi.rcWork, area = mi.rcMonitor;
                        mmi.ptMaxPosition.X = work.Left - area.Left;
                        mmi.ptMaxPosition.Y = work.Top - area.Top;
                        mmi.ptMaxSize.X = work.Right - work.Left;
                        mmi.ptMaxSize.Y = work.Bottom - work.Top;
                        Marshal.StructureToPtr(mmi, lParam, false);
                    }
                }
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WM_SIZING && S.LockAspect && _vidW > 0 && _vidH > 0 && !_fullscreen)
            {
                var r = Marshal.PtrToStructure<RECT>(lParam);
                GetWindowRect(hwnd, out RECT wr);
                GetClientRect(hwnd, out RECT cr);
                int dx = (wr.Right - wr.Left) - (cr.Right - cr.Left);   // chrome width
                int dy = (wr.Bottom - wr.Top) - (cr.Bottom - cr.Top);   // chrome height
                int barH = TitleBar.Visibility == Visibility.Visible ? (int)Math.Round(TitleBar.ActualHeight) : 0;
                dy += barH;   // title bar sits inside the client area, above the video

                bool rotated = S.Rotation % 180 != 0;
                double aspect = rotated ? (double)_vidH / _vidW : (double)_vidW / _vidH;

                int edge = wParam.ToInt32();   // WMSZ_*: 1 L, 2 R, 3 T, 4 TL, 5 TR, 6 B, 7 BL, 8 BR
                int cw = Math.Max(160, (r.Right - r.Left) - dx);
                int ch = Math.Max(120, (r.Bottom - r.Top) - dy);

                if (edge == 3 || edge == 6)          // top/bottom edge: height drives width
                {
                    cw = (int)Math.Round(ch * aspect);
                    r.Right = r.Left + cw + dx;
                }
                else                                  // side/corner: width drives height
                {
                    ch = (int)Math.Round(cw / aspect);
                    if (edge == 1 || edge == 4 || edge == 7)
                        r.Left = r.Right - cw - dx;
                    else
                        r.Right = r.Left + cw + dx;
                    if (edge == 3 || edge == 4 || edge == 5)
                        r.Top = r.Bottom - ch - dy;
                    else
                        r.Bottom = r.Top + ch + dy;
                }

                Marshal.StructureToPtr(r, lParam, false);
                handled = true;
                return (IntPtr)1;
            }
            return IntPtr.Zero;
        }

        // ---------------- lifecycle ----------------

        async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSettings();

            // window state from settings
            if (S.WinW > 100 && S.WinH > 100)
            {
                Left = S.WinX; Top = S.WinY; Width = S.WinW; Height = S.WinH;
            }
            Topmost = S.Topmost;
            _uiHidden = S.HideUi;
            ApplyChrome();
            ApplyFrameless();
            ApplyOpacity();
            ApplyTransforms();
            PopulateFpsAndRes();
            SyncToggles();
            UpdateMaxButtonGlyph();
            UpdateUiVisibility();
            ResetIdleTimer();

            await RefreshCamerasAsync();
            await StartCameraAsync(SelectedDeviceId());
        }

        void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_fullscreen && WindowState == WindowState.Normal)
            {
                S.WinX = Left; S.WinY = Top; S.WinW = Width; S.WinH = Height;
            }
            SaveSettings();
        }

        async void Window_Closed(object sender, EventArgs e)
        {
            _idleTimer.Stop();
            _toastTimer.Stop();
            _formatTimer.Stop();
            await StopCameraAsync();
        }

        // ---------------- settings ----------------

        void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath));
                    if (loaded != null) S = loaded;
                }
            }
            catch { /* corrupt file -> defaults */ }

            if (!FpsSteps.Contains(S.Fps)) S.Fps = 15;
            if (S.Rotation % 90 != 0) S.Rotation = 0;
            S.Rotation = ((S.Rotation % 360) + 360) % 360;
            S.Opacity = Math.Max(1, Math.Min(100, S.Opacity));
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath));
                File.WriteAllText(_settingsPath,
                    JsonSerializer.Serialize(S, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* non-fatal */ }
        }

        // ---------------- camera ----------------

        async Task RefreshCamerasAsync()
        {
            try { _cams = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture); }
            catch { _cams = null; }

            _suppressEvents = true;
            CamSel.Items.Clear();
            if (_cams != null && _cams.Count > 0)
            {
                int select = 0;
                for (int i = 0; i < _cams.Count; i++)
                {
                    CamSel.Items.Add(_cams[i].Name);
                    if (_cams[i].Name == S.CameraName) select = i;
                }
                CamSel.SelectedIndex = select;
            }
            else
            {
                CamSel.Items.Add("No cameras found");
                CamSel.SelectedIndex = 0;
            }
            _suppressEvents = false;
        }

        string SelectedDeviceId()
        {
            if (_cams == null || _cams.Count == 0) return null;
            int i = Math.Max(0, CamSel.SelectedIndex);
            return i < _cams.Count ? _cams[i].Id : _cams[0].Id;
        }

        async Task StartCameraAsync(string deviceId)
        {
            if (_starting) return;      // avoid overlapping starts
            _starting = true;
            try
            {
                await StopCameraAsync();
                if (deviceId == null)
                {
                    ShowError("No camera was found. Plug one in, then try again.");
                    return;
                }
                _activeDeviceId = deviceId;

                try
                {
                    _capture = new MediaCapture();
                    _capture.Failed += Capture_Failed;
                    await _capture.InitializeAsync(new MediaCaptureInitializationSettings
                    {
                        VideoDeviceId = deviceId,
                        StreamingCaptureMode = StreamingCaptureMode.Video,
                        MemoryPreference = MediaCaptureMemoryPreference.Cpu
                    });

                // Try every video stream this camera exposes, most compatible first.
                int BestRank(MediaFrameSource s) =>
                    s.SupportedFormats.Count == 0 ? 9 :
                    s.SupportedFormats.Min(f => SubtypeRank(f.Subtype));

                var candidates = _capture.FrameSources.Values
                    .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                    .ToList();
                if (candidates.Count == 0)
                    candidates = _capture.FrameSources.Values.ToList();

                candidates = candidates
                    .OrderBy(BestRank)
                    .ThenBy(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview ? 0 :
                                 s.Info.MediaStreamType == MediaStreamType.VideoRecord ? 1 : 2)
                    .ToList();

                if (candidates.Count == 0)
                    throw new InvalidOperationException("This camera exposes no video stream.");

                Exception lastErr = null;
                foreach (var src in candidates)
                {
                    try
                    {
                        _source = src;
                        var fmt = PickFormat(src);
                        if (fmt != null)
                        {
                            try { await src.SetFormatAsync(fmt); } catch { /* keep default */ }
                        }

                        var reader = await CreateReaderWithFallbackAsync(src);
                        reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                        reader.FrameArrived += Reader_FrameArrived;

                        var status = await reader.StartAsync();
                        if (status == MediaFrameReaderStartStatus.Success)
                        {
                            _reader = reader;
                            break;
                        }

                        reader.FrameArrived -= Reader_FrameArrived;
                        reader.Dispose();
                        lastErr = new InvalidOperationException(
                            "Could not start the camera stream (" + status + ").");
                    }
                    catch (Exception ex) { lastErr = ex; }
                }

                if (_reader == null)
                    throw lastErr ?? new InvalidOperationException("No compatible video stream found.");

                var name = (_cams?.FirstOrDefault(c => c.Id == deviceId)?.Name) ?? "";
                if (name.Length > 0) S.CameraName = name;
                SaveSettings();
                HideError();
                UpdateOsd();
            }
            catch (UnauthorizedAccessException)
            {
                ShowError("Camera access is blocked for desktop apps. Open Windows Settings > " +
                          "Privacy & security > Camera, and turn on \u201CLet desktop apps access your camera\u201D.");
            }
            catch (Exception ex)
            {
                string hint = (ex.Message ?? "").IndexOf("format", StringComparison.OrdinalIgnoreCase) >= 0
                    ? " If this PC runs a Windows \u201CN\u201D edition, install the free Media Feature Pack " +
                      "(Settings > Apps > Optional features), then restart."
                    : " It may be in use by another program.";

                string formats = "";
                try
                {
                    if (_source != null)
                        formats = "\n\nCamera offers: " + string.Join(", ",
                            _source.SupportedFormats
                                .Select(f => f.Subtype + " " + f.VideoFormat.Width + "x" + f.VideoFormat.Height)
                                .Distinct()
                                .Take(12));
                }
                catch { }

                ShowError("Couldn\u2019t start the camera." + hint + " (" + ex.Message + ")" + formats);
                }
            }
            finally
            {
                _starting = false;
            }
        }

        void Capture_Failed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs)
        {
            // Camera dropped (unplugged, driver hiccup, taken by another app).
            // Try to recover automatically after a short delay.
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (_pausedForMinimize) return;
                await StopCameraAsync();
                await RefreshCamerasAsync();
                await Task.Delay(1200);
                await StartCameraAsync(_activeDeviceId ?? SelectedDeviceId());
            }));
        }

        async Task StopCameraAsync()
        {
            try
            {
                if (_reader != null)
                {
                    _reader.FrameArrived -= Reader_FrameArrived;
                    try { await _reader.StopAsync(); } catch { }
                    _reader.Dispose();
                    _reader = null;
                }
                _source = null;
                if (_capture != null)
                {
                    _capture.Failed -= Capture_Failed;
                    _capture.Dispose();
                    _capture = null;
                }
                var pending = Interlocked.Exchange(ref _pendingFrame, null);
                pending?.Dispose();
            }
            catch { }
        }

        static int SubtypeRank(string subtype)
        {
            switch ((subtype ?? "").ToUpperInvariant())
            {
                case "YUY2":
                case "NV12":
                case "RGB24":
                case "RGB32":
                case "ARGB32":
                    return 0;   // uncompressed — most compatible
                case "MJPG":
                    return 1;   // needs a decoder (missing on Windows N editions)
                case "H264":
                case "H264ES":
                case "HEVC":
                    return 3;   // compressed streams the frame reader can't decode
                default:
                    return 2;
            }
        }

        MediaFrameFormat PickFormat(MediaFrameSource src)
        {
            double Fps(MediaFrameFormat f) =>
                f.FrameRate.Denominator == 0 ? 0 :
                (double)f.FrameRate.Numerator / f.FrameRate.Denominator;

            return src.SupportedFormats
                .OrderBy(f => Math.Abs((int)f.VideoFormat.Width - S.Width) +
                              Math.Abs((int)f.VideoFormat.Height - S.Height))
                .ThenBy(f => SubtypeRank(f.Subtype))
                .ThenBy(f => Math.Abs(Fps(f) - S.Fps))
                .ThenBy(f => Fps(f))
                .FirstOrDefault();
        }

        async Task<MediaFrameReader> CreateReaderWithFallbackAsync(MediaFrameSource src)
        {
            try
            {
                return await TryReaderSubtypesAsync(src);
            }
            catch (Exception)
            {
                // Last resort: switch the camera itself to an uncompressed format and retry.
                var alt = src.SupportedFormats
                    .Where(f => SubtypeRank(f.Subtype) == 0)
                    .OrderBy(f => Math.Abs((int)f.VideoFormat.Width - S.Width) +
                                  Math.Abs((int)f.VideoFormat.Height - S.Height))
                    .FirstOrDefault();
                if (alt == null) throw;
                await src.SetFormatAsync(alt);
                return await TryReaderSubtypesAsync(src);
            }
        }

        async Task<MediaFrameReader> TryReaderSubtypesAsync(MediaFrameSource src)
        {
            string[] subtypes =
            {
                MediaEncodingSubtypes.Bgra8,
                MediaEncodingSubtypes.Nv12,
                MediaEncodingSubtypes.Yuy2,
                null    // camera's native format; frames get converted per-frame anyway
            };
            Exception last = null;
            foreach (string st in subtypes)
            {
                try
                {
                    return st == null
                        ? await _capture.CreateFrameReaderAsync(src)
                        : await _capture.CreateFrameReaderAsync(src, st);
                }
                catch (Exception ex) { last = ex; }
            }
            throw last;
        }

        // Runs on a worker thread.
        void Reader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            int fps = Math.Max(1, S.Fps);
            long now = Environment.TickCount64;
            if (now - _lastFrameTicks < 900 / fps) return;   // software throttle (~90% of interval)

            using (var frameRef = sender.TryAcquireLatestFrame())
            {
                var sb = frameRef?.VideoMediaFrame?.SoftwareBitmap;
                if (sb == null) return;
                _lastFrameTicks = now;

                SoftwareBitmap converted =
                    SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);

                var old = Interlocked.Exchange(ref _pendingFrame, converted);
                old?.Dispose();
            }

            if (Interlocked.CompareExchange(ref _uiBusy, 1, 0) == 0)
                Dispatcher.BeginInvoke(new Action(RenderPendingFrame));
        }

        void RenderPendingFrame()
        {
            try
            {
                var sb = Interlocked.Exchange(ref _pendingFrame, null);
                if (sb == null) return;
                using (sb)
                {
                    int w = sb.PixelWidth, h = sb.PixelHeight;
                    int len = w * h * 4;

                    if (_wb == null || _wb.PixelWidth != w || _wb.PixelHeight != h)
                    {
                        _wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                        VideoImage.Source = _wb;
                        _vidW = w; _vidH = h;
                        UpdateOsd();
                        if (S.LockAspect) FitToAspect();
                    }
                    if (_pixels == null || _pixels.Length != len)
                        _pixels = new byte[len];

                    var buffer = new Windows.Storage.Streams.Buffer((uint)len);
                    sb.CopyToBuffer(buffer);
                    using (var dr = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
                        dr.ReadBytes(_pixels);

                    _wb.WritePixels(new Int32Rect(0, 0, w, h), _pixels, w * 4, 0);
                }
            }
            catch { /* skip bad frame */ }
            finally
            {
                Interlocked.Exchange(ref _uiBusy, 0);
            }
        }

        // ---------------- controls ----------------

        void PopulateFpsAndRes()
        {
            _suppressEvents = true;
            FpsSel.Items.Clear();
            foreach (int f in FpsSteps) FpsSel.Items.Add(f + " fps");
            FpsSel.SelectedIndex = Math.Max(0, Array.IndexOf(FpsSteps, S.Fps));

            ResSel.Items.Clear();
            int resIdx = 1;
            for (int i = 0; i < ResSteps.Length; i++)
            {
                ResSel.Items.Add(ResSteps[i].W + "\u00D7" + ResSteps[i].H);
                if (ResSteps[i].W == S.Width && ResSteps[i].H == S.Height) resIdx = i;
            }
            ResSel.SelectedIndex = resIdx;
            _suppressEvents = false;
        }

        void SyncToggles()
        {
            _suppressEvents = true;
            FlipHTgl.IsChecked = S.FlipH;
            FlipVTgl.IsChecked = S.FlipV;
            FramelessTgl.IsChecked = S.Frameless;
            TopTgl.IsChecked = S.Topmost;
            AspectTgl.IsChecked = S.LockAspect;
            OpacitySld.Value = S.Opacity;
            OpacityLbl.Text = S.Opacity.ToString();
            BootTgl.IsChecked = IsBootEnabled();
            _suppressEvents = false;
        }

        void ApplyTransforms()
        {
            RotTf.Angle = S.Rotation;
            FlipTf.ScaleX = S.FlipH ? -1 : 1;
            FlipTf.ScaleY = S.FlipV ? -1 : 1;
        }

        void ApplyChrome()
        {
            // Window is frameless at the OS level from launch (WindowStyle=None +
            // AllowsTransparency). Our own title bar is shown/hidden by ApplyFrameless.
        }

        void ApplyFrameless()
        {
            // Title bar visibility is centralized in UpdateUiVisibility
            // (accounts for Frameless, fullscreen, and Hide-UI together).
            UpdateUiVisibility();
            if (S.LockAspect) FitToAspect();
        }

        void Frameless_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            S.Frameless = FramelessTgl.IsChecked == true;
            SaveSettings();
            ApplyFrameless();
            if (S.Frameless) ShowToast("Frameless \u2014 drag the video to move");
        }

        void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_fullscreen) return;
            if (e.ClickCount == 2)
            {
                MaxRestore_Click(null, null);
                return;
            }
            try { DragMove(); } catch { }
        }

        void UpdateOsd()
        {
            var parts = new List<string>();
            var fmt = _source?.CurrentFormat;
            if (fmt != null)
                parts.Add(fmt.VideoFormat.Width + "\u00D7" + fmt.VideoFormat.Height);
            parts.Add(S.Fps + " fps");
            if (S.Rotation != 0) parts.Add("ROT " + S.Rotation + "\u00B0");
            if (S.FlipH) parts.Add("FLIP H");
            if (S.FlipV) parts.Add("FLIP V");
            Osd.Text = string.Join("  \u00B7  ", parts);
        }

        void FlipH_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            S.FlipH = FlipHTgl.IsChecked == true;
            ApplyTransforms(); SaveSettings(); UpdateOsd();
        }

        void FlipV_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            S.FlipV = FlipVTgl.IsChecked == true;
            ApplyTransforms(); SaveSettings(); UpdateOsd();
        }

        void Rotate_Click(object sender, RoutedEventArgs e)
        {
            S.Rotation = (S.Rotation + 90) % 360;
            ApplyTransforms(); SaveSettings(); UpdateOsd();
            if (S.LockAspect) FitToAspect();
            ShowToast("Rotation " + S.Rotation + "\u00B0");
        }

        void Aspect_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            S.LockAspect = AspectTgl.IsChecked == true;
            SaveSettings();
            if (S.LockAspect)
            {
                FitToAspect();
                ShowToast("Window locked to video aspect ratio");
            }
        }

        void FitToAspect()
        {
            if (_vidW <= 0 || _vidH <= 0 || _fullscreen ||
                WindowState != WindowState.Normal) return;

            bool rotated = S.Rotation % 180 != 0;
            double aw = rotated ? _vidH : _vidW;
            double ah = rotated ? _vidW : _vidH;

            double chromeW = Math.Max(0, ActualWidth - Root.ActualWidth);
            double chromeH = Math.Max(0, ActualHeight - Root.ActualHeight);
            double barH = TitleBar.Visibility == Visibility.Visible ? TitleBar.ActualHeight : 0;
            double clientW = Math.Max(160, ActualWidth - chromeW);

            Width = clientW + chromeW;
            Height = clientW * ah / aw + chromeH + barH;
        }

        void OpacitySld_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || !IsLoaded) return;
            S.Opacity = (int)Math.Round(e.NewValue);
            OpacityLbl.Text = S.Opacity.ToString();
            ApplyOpacity();
            SaveSettings();
        }

        void ApplyOpacity()
        {
            // Fade the video layer (black backing + frame together) so low values
            // reveal the desktop behind the window. Menu/OSD stay solid; the title
            // bar tracks the same opacity so it matches the video.
            double o = Math.Max(0.0, S.Opacity / 100.0);
            VideoLayer.Opacity = o;
            TitleBar.Opacity = Math.Max(0.15, o);   // floor so the close button stays usable
        }

        void FpsSel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || FpsSel.SelectedIndex < 0) return;
            S.Fps = FpsSteps[FpsSel.SelectedIndex];
            SaveSettings(); UpdateOsd();
            ShowToast(S.Fps + " fps");
            DebounceFormatChange();
        }

        void ResSel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || ResSel.SelectedIndex < 0) return;
            (S.Width, S.Height) = ResSteps[ResSel.SelectedIndex];
            SaveSettings();
            DebounceFormatChange();
        }

        void DebounceFormatChange()
        {
            // Coalesce rapid fps/resolution changes into one camera restart.
            _formatTimer.Stop();
            _formatTimer.Start();
        }

        async Task RestartForFormatChangeAsync()
        {
            // Re-pick the closest hardware capture format; software throttle covers the rest.
            if (_capture != null && !_pausedForMinimize)
                await StartCameraAsync(_activeDeviceId ?? SelectedDeviceId());
        }

        async void CamSel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            await StartCameraAsync(SelectedDeviceId());
        }

        async void Retry_Click(object sender, RoutedEventArgs e)
        {
            await RefreshCamerasAsync();
            await StartCameraAsync(SelectedDeviceId());
        }

        void Full_Click(object sender, RoutedEventArgs e) { ToggleFullscreen(); }

        void ToggleFullscreen()
        {
            if (!_fullscreen)
            {
                _preFullBounds = new Rect(Left, Top, Width, Height);
                _fullscreen = true;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;
                ApplyFrameless();
            }
            else
            {
                _fullscreen = false;
                WindowState = WindowState.Normal;
                ResizeMode = ResizeMode.CanResizeWithGrip;
                Left = _preFullBounds.X; Top = _preFullBounds.Y;
                Width = _preFullBounds.Width; Height = _preFullBounds.Height;
                ApplyChrome();
                ApplyFrameless();
            }
        }

        void Top_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            S.Topmost = TopTgl.IsChecked == true;
            Topmost = S.Topmost;
            SaveSettings();
        }

        void HideUi_Click(object sender, RoutedEventArgs e) { SetUiHidden(true); }

        void Quit_Click(object sender, RoutedEventArgs e) { Close(); }

        void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        void MaxRestore_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;
        }

        void SetUiHidden(bool hidden)
        {
            _uiHidden = hidden;
            S.HideUi = hidden;
            SaveSettings();
            UpdateUiVisibility();
            if (hidden) ShowToast("Controls hidden \u2014 press U to bring them back");
        }

        void UpdateUiVisibility()
        {
            bool visible = !_uiHidden && !_idleHidden;
            MenuTgl.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            Osd.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) CloseMenu();
            Root.Cursor = visible ? Cursors.Arrow : Cursors.None;

            // Full Hide-UI (U) also hides the title bar; idle auto-hide keeps it.
            bool showBar = !S.Frameless && !_fullscreen && !_uiHidden;
            TitleBar.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        }

        void MenuTgl_Click(object sender, RoutedEventArgs e)
        {
            MenuPanel.Visibility = MenuTgl.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;
        }

        void CloseMenu()
        {
            MenuTgl.IsChecked = false;
            MenuPanel.Visibility = Visibility.Collapsed;
        }

        // ---------------- run at login ----------------

        bool IsBootEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                    return key?.GetValue(RunValueName) != null;
            }
            catch { return false; }
        }

        void Boot_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool enable = BootTgl.IsChecked == true;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) throw new InvalidOperationException("Registry key unavailable.");
                    if (enable)
                    {
                        string exe = Environment.ProcessPath ?? "";
                        key.SetValue(RunValueName, "\"" + exe + "\"");
                        ShowToast("Will start at login: " + Path.GetFileName(exe));
                    }
                    else
                    {
                        key.DeleteValue(RunValueName, false);
                        ShowToast("Removed from login startup");
                    }
                }
            }
            catch (Exception ex)
            {
                ShowToast("Couldn\u2019t update startup: " + ex.Message);
                _suppressEvents = true;
                BootTgl.IsChecked = !enable;
                _suppressEvents = false;
            }
        }

        // ---------------- input ----------------

        void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is ComboBox || e.OriginalSource is ComboBoxItem ||
                e.OriginalSource is TextBox) return;

            switch (e.Key)
            {
                case Key.H: FlipHTgl.IsChecked = !(FlipHTgl.IsChecked == true); FlipH_Click(null, null); break;
                case Key.V: FlipVTgl.IsChecked = !(FlipVTgl.IsChecked == true); FlipV_Click(null, null); break;
                case Key.R: Rotate_Click(null, null); break;
                case Key.F: ToggleFullscreen(); break;
                case Key.B:
                    FramelessTgl.IsChecked = !(FramelessTgl.IsChecked == true);
                    Frameless_Click(null, null); break;
                case Key.T:
                    TopTgl.IsChecked = !(TopTgl.IsChecked == true);
                    Top_Click(null, null); break;
                case Key.U: SetUiHidden(!_uiHidden); break;
                case Key.OemOpenBrackets: StepFps(-1); break;
                case Key.OemCloseBrackets: StepFps(+1); break;
                case Key.M:
                    MenuTgl.IsChecked = !(MenuTgl.IsChecked == true);
                    MenuTgl_Click(null, null);
                    break;
                case Key.Escape:
                    if (MenuPanel.Visibility == Visibility.Visible) CloseMenu();
                    else if (_fullscreen) ToggleFullscreen();
                    break;
                default: return;
            }
            e.Handled = true;
        }

        void StepFps(int dir)
        {
            int i = Array.IndexOf(FpsSteps, S.Fps);
            if (i < 0) i = 4;
            i = Math.Max(0, Math.Min(FpsSteps.Length - 1, i + dir));
            FpsSel.SelectedIndex = i;   // triggers FpsSel_SelectionChanged
        }

        void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Click on the video (outside the menu) closes an open menu.
            if (MenuPanel.Visibility == Visibility.Visible)
                CloseMenu();

            if (!_fullscreen)
            {
                try { DragMove(); } catch { }
            }
        }

        void Root_MouseMove(object sender, MouseEventArgs e)
        {
            if (_idleHidden) { _idleHidden = false; UpdateUiVisibility(); }
            ResetIdleTimer();
        }

        void Root_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Right-click the video to bring the UI back when it's hidden.
            if (!_uiHidden) return;

            var menu = new ContextMenu();
            var item = new MenuItem { Header = "Show UI" };
            item.Click += (s, ev) => SetUiHidden(false);
            menu.Items.Add(item);
            menu.IsOpen = true;
            e.Handled = true;
        }

        void ResetIdleTimer()
        {
            _idleTimer.Stop();
            _idleTimer.Start();
        }

        // ---------------- feedback ----------------

        void ShowToast(string msg)
        {
            ToastText.Text = msg;
            Toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        void ShowError(string msg)
        {
            ErrorText.Text = msg;
            ErrorPanel.Visibility = Visibility.Visible;
        }

        void HideError()
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
        }
    }
}
