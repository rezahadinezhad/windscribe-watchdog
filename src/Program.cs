// Windscribe Watchdog: keeps Windscribe connected without you having to step in.
// See README.md for what it does and doesn't do; Watchdog.cs has the logic.

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Windscribe Watchdog")]
[assembly: AssemblyProduct("Windscribe Watchdog")]
[assembly: AssemblyVersion("2.2.0.0")]

namespace WindscribeWatchdog
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            SetProcessDPIAware();
            int preview = Array.IndexOf(args, "--preview");
            if (preview >= 0)
            {
                // Draws the window with made-up data, as used for the README screenshots:
                //   WindscribeWatchdog.exe --preview out.png [connected|fixing|paused|problem]
                MainWindow.RenderPreview(args.Length > preview + 1 ? args[preview + 1] : "preview.png",
                                         args.Length > preview + 2 ? args[preview + 2] : "connected");
                return;
            }

            bool autostart = Array.IndexOf(args, "--autostart") >= 0;
            bool keepalive = Array.IndexOf(args, "--keepalive") >= 0;
            if (keepalive && Settings.UserExited) return; // you chose Quit, so the keepalive task leaves it closed

            bool firstInstance;
            using (new Mutex(true, @"Local\WindscribeWatchdog", out firstInstance))
            {
                if (!firstInstance)
                {
                    if (!autostart && !keepalive) TrayApp.AskRunningCopyToShowWindow();
                    return;
                }
                Settings.UserExited = false;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log.Write("Error: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Crashed: " + e.ExceptionObject);
                if (keepalive) Log.Write("Started again by the keepalive task.");
                Application.Run(new TrayApp(autostart || keepalive));
            }
        }
    }
}
