using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WindscribeWatchdog
{
    // The tray icon, its menu and the window. Runs the watchdog loop and keeps it running.
    class TrayApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "WindscribeWatchdog";
        const string TaskName = "WindscribeWatchdog"; // the keepalive task registered by install.ps1
        const string ShowSignalName = @"Local\WindscribeWatchdog.Show";

        readonly SynchronizationContext ui;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem autoItem = new ToolStripMenuItem("Auto-reconnect") { Checked = true, CheckOnClick = true };
        readonly Dictionary<Health, Icon> icons = new Dictionary<Health, Icon>();
        readonly System.Windows.Forms.Timer heartbeat = new System.Windows.Forms.Timer();
        readonly UiActions actions = new UiActions();
        Watchdog watchdog;
        MainWindow window;
        Snapshot latest = new Snapshot();
        bool autoReconnect = true;
        bool startWithWindows = Settings.StartWithWindows, notifications = Settings.Notifications;
        string trayShows;
        DateTime lastHeartbeatCheck = DateTime.MinValue;

        public TrayApp(bool quiet)
        {
            ui = new WindowsFormsSynchronizationContext();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException += (s, e) =>
            {
                Log.Write("Window error: " + e.Exception.Message);
                e.Handled = true;
            };

            int size = SystemInformation.SmallIconSize.Width;
            icons[Health.Good] = Shield.MakeIcon(Color.FromArgb(59, 224, 176), size);
            icons[Health.Busy] = Shield.MakeIcon(Color.FromArgb(247, 181, 56), size);
            icons[Health.Idle] = Shield.MakeIcon(Color.FromArgb(141, 155, 176), size);
            icons[Health.Problem] = Shield.MakeIcon(Color.FromArgb(255, 93, 108), size);

            actions.SetAutoReconnect = SetAutoReconnect;
            actions.SetStartup = on =>
            {
                Settings.StartWithWindows = startWithWindows = on;
                ThreadPool.QueueUserWorkItem(_ => ApplyStartup(on));
                Refresh();
            };
            actions.SetNotifications = on =>
            {
                Settings.Notifications = notifications = on;
                Refresh();
            };
            actions.RestartWindscribe = () => watchdog.RequestRestart();
            actions.OpenLog = OpenLog;
            actions.Quit = Quit;

            autoItem.CheckedChanged += delegate { SetAutoReconnect(autoItem.Checked); };
            var open = new ToolStripMenuItem("Open Watchdog");
            open.Font = new Font(open.Font, FontStyle.Bold);
            open.Click += delegate { ShowWindow(); };
            var restart = new ToolStripMenuItem("Restart Windscribe");
            restart.Click += delegate { watchdog.RequestRestart(); };
            var openLog = new ToolStripMenuItem("Open activity log");
            openLog.Click += delegate { OpenLog(); };
            var quit = new ToolStripMenuItem("Quit");
            quit.Click += delegate { Quit(); };

            var menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] { open, new ToolStripSeparator(), autoItem, restart, openLog, new ToolStripSeparator(), quit });

            tray.ContextMenuStrip = menu;
            tray.Icon = icons[Health.Idle];
            tray.Text = "Windscribe Watchdog";
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleWindow(); };
            tray.BalloonTipClicked += delegate { ShowWindow(); };
            tray.Visible = true;

            ThreadPool.QueueUserWorkItem(_ => ApplyStartup(startWithWindows));
            watchdog = NewWatchdog();
            watchdog.Start();

            // If the check loop ever stops responding, start a fresh one.
            heartbeat.Interval = 60000;
            heartbeat.Tick += delegate { CheckHeartbeat(); };
            heartbeat.Start();

            ListenForShowRequests();
            if (!quiet) ShowWindow();
        }

        // Called by a second copy that was opened by hand: the running one shows its window instead.
        public static void AskRunningCopyToShowWindow()
        {
            EventWaitHandle signal;
            if (EventWaitHandle.TryOpenExisting(ShowSignalName, out signal))
                using (signal) signal.Set();
        }

        void ListenForShowRequests()
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
            var listener = new Thread(delegate()
            {
                while (true)
                {
                    signal.WaitOne();
                    ui.Post(_ => ShowWindow(), null);
                }
            });
            listener.IsBackground = true;
            listener.Start();
        }

        Watchdog NewWatchdog()
        {
            var w = new Watchdog();
            w.Enabled = autoReconnect;
            w.OnSnapshot = s => ui.Post(_ => { latest = s; Refresh(); }, null);
            w.OnNotify = (heading, text, warning) => ui.Post(_ =>
            {
                if (notifications) tray.ShowBalloonTip(10000, heading, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }, null);
            return w;
        }

        void SetAutoReconnect(bool on)
        {
            if (on == autoReconnect) return;
            autoReconnect = on;
            if (autoItem.Checked != on) autoItem.Checked = on;
            watchdog.Enabled = on;
            watchdog.Poke();
            Refresh();
        }

        // Shows the latest snapshot in the tray and the window. Your switches come from here, not
        // from the snapshot, which may be a moment old.
        void Refresh()
        {
            Snapshot s = latest;
            s.AutoReconnect = autoReconnect;
            s.StartWithWindows = startWithWindows;
            s.Notifications = notifications;
            string shows = s.Health + "|" + s.Headline;
            if (shows != trayShows)
            {
                trayShows = shows;
                tray.Icon = icons[s.Health];
                string tip = "Windscribe Watchdog\n" + s.Headline;
                tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; // NotifyIcon's limit
            }
            if (window != null && window.IsOpen) window.Apply(s, Log.Recent(5)); // a hidden window catches up when shown
        }

        void ShowWindow()
        {
            try
            {
                if (window == null) window = new MainWindow(actions);
                window.Show();
                Refresh();
            }
            catch (Exception ex) { Log.Write("Couldn't open the window: " + ex.Message); }
        }

        void ToggleWindow()
        {
            if (window != null && window.IsOpen) window.Hide();
            else ShowWindow();
        }

        void CheckHeartbeat()
        {
            DateTime now = DateTime.UtcNow;
            bool justWoke = lastHeartbeatCheck != DateTime.MinValue && now - lastHeartbeatCheck > TimeSpan.FromMinutes(3);
            lastHeartbeatCheck = now;
            if (justWoke) return; // the PC slept; give the loop a chance to catch up
            DateTime beat = watchdog.LastBeat;
            if (beat.Ticks == 0 || now - beat < TimeSpan.FromMinutes(6)) return;
            Log.Write("The check loop stopped responding; starting a new one.");
            watchdog.Stop();
            watchdog = NewWatchdog();
            watchdog.Start();
        }

        static void OpenLog()
        {
            if (!File.Exists(Log.FilePath)) Log.Write("Log created.");
            Process.Start("notepad.exe", "\"" + Log.FilePath + "\"");
        }

        // Keeps the startup entry and the keepalive task in line with your choice. The app does this
        // itself on every start, so the entry comes back if something removes it.
        static void ApplyStartup(bool on)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    string command = "\"" + Application.ExecutablePath + "\" --autostart";
                    if (!on) key.DeleteValue(RunValue, false);
                    else if (command != key.GetValue(RunValue) as string) key.SetValue(RunValue, command);
                }
                var psi = new ProcessStartInfo("schtasks.exe", "/change /tn " + TaskName + (on ? " /enable" : " /disable"));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi)) p.WaitForExit(10000);
            }
            catch (Exception ex) { Log.Write("Couldn't update the startup settings: " + ex.Message); }
        }

        void Quit()
        {
            Settings.UserExited = true;
            ExitThread();
        }

        protected override void ExitThreadCore()
        {
            heartbeat.Stop();
            watchdog.Stop();
            Log.Write("Watchdog stopped.");
            if (window != null) window.Close();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }
}
