using System;
using System.Threading;
using System.Windows;

namespace CamView
{
    public partial class App : Application
    {
        // Named mutex prevents a second copy from opening (two windows would
        // fight over the camera). Especially relevant with "Run at login".
        static Mutex _instanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            const string name = "CamView_SingleInstance_9F2A";
            _instanceMutex = new Mutex(true, name, out bool isNew);
            if (!isNew)
            {
                // Already running — bring the existing window forward, then exit.
                NativeMethods.ActivateExisting();
                Shutdown();
                return;
            }
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch { }
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }
    }

    static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        const int SW_RESTORE = 9;

        public static void ActivateExisting()
        {
            try
            {
                var me = System.Diagnostics.Process.GetCurrentProcess();
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
                {
                    if (p.Id != me.Id && p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(p.MainWindowHandle);
                        break;
                    }
                }
            }
            catch { }
        }
    }
}
