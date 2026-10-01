using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;

namespace WindscribeWatchdog
{
    // What the window can ask the app to do.
    class UiActions
    {
        public Action<bool> SetAutoReconnect, SetStartup, SetNotifications;
        public Action RestartWindscribe, OpenLog, Quit;
    }

    // The main window: a dark card in the spirit of Windscribe's own app. The layout lives in
    // MainWindow.xaml (embedded in the exe); this class fills it in and wires it up.
    class MainWindow
    {
        // One brush per colour: handing WPF the same brush again doesn't make it redraw the window.
        // Declared first because the brushes below are created through it.
        static readonly Dictionary<Color, Brush> brushes = new Dictionary<Color, Brush>();
        static readonly Color GoodColor = Color.FromRgb(0x3B, 0xE0, 0xB0);
        static readonly Color BusyColor = Color.FromRgb(0xF7, 0xB5, 0x38);
        static readonly Color IdleColor = Color.FromRgb(0x8D, 0x9B, 0xB0);
        static readonly Color ProblemColor = Color.FromRgb(0xFF, 0x5D, 0x6C);
        static readonly Brush TimeBrush = Frozen(Color.FromRgb(0x6E, 0x84, 0xA3));
        static readonly Brush EntryBrush = Frozen(Color.FromRgb(0xC9, 0xD7, 0xEA));
        const double Corner = 16;
        const int ActivityRows = 4;

        readonly UiActions actions;      // null when only drawing a preview
        readonly FrameworkElement root;
        readonly Window window;          // null when only drawing a preview
        readonly DispatcherTimer clock = new DispatcherTimer();
        Snapshot shown;
        string shownActivity = "";
        bool updating, spinning, placed, closing;

        readonly Border card, pill;
        readonly TextBlock pillText, protocolText, placeText, statusText, ipText, firewallText, internetText, fixesText, lastFixText;
        readonly Run cityRun, nickRun;
        readonly Shapes.Ellipse ring, ringGlow;
        readonly Shapes.Path spinner, powerIcon;
        readonly RotateTransform spinnerTurn;
        readonly GradientStop glowStop, glowEdge;
        readonly CheckBox autoToggle, startupToggle, notifyToggle;
        readonly ToggleButton activityFold;
        readonly StackPanel activityList;
        readonly Button ringButton;

        public MainWindow(UiActions actions)
        {
            this.actions = actions;
            using (Stream xaml = Assembly.GetExecutingAssembly().GetManifestResourceStream("WindscribeWatchdog.MainWindow.xaml"))
                root = (FrameworkElement)XamlReader.Load(xaml);

            card = Find<Border>("Card");
            pill = Find<Border>("Pill");
            pillText = Find<TextBlock>("PillText");
            protocolText = Find<TextBlock>("ProtocolText");
            placeText = Find<TextBlock>("PlaceText");
            statusText = Find<TextBlock>("StatusText");
            ipText = Find<TextBlock>("IpText");
            firewallText = Find<TextBlock>("FirewallText");
            internetText = Find<TextBlock>("InternetText");
            fixesText = Find<TextBlock>("FixesText");
            lastFixText = Find<TextBlock>("LastFixText");
            cityRun = Find<Run>("CityRun");
            nickRun = Find<Run>("NickRun");
            ring = Find<Shapes.Ellipse>("Ring");
            ringGlow = Find<Shapes.Ellipse>("RingGlow");
            spinner = Find<Shapes.Path>("Spinner");
            powerIcon = Find<Shapes.Path>("PowerIcon");
            spinnerTurn = Find<RotateTransform>("SpinnerTurn");
            glowStop = Find<GradientStop>("GlowStop");
            glowEdge = Find<GradientStop>("GlowEdge");
            autoToggle = Find<CheckBox>("AutoToggle");
            startupToggle = Find<CheckBox>("StartupToggle");
            notifyToggle = Find<CheckBox>("NotifyToggle");
            activityFold = Find<ToggleButton>("ActivityFold");
            activityList = Find<StackPanel>("ActivityList");
            ringButton = Find<Button>("RingButton");

            // Round the card's corners for everything inside it too (the glow would spill out).
            card.SizeChanged += delegate { ClipCard(); };

            ringButton.Click += delegate { if (actions != null && shown != null) actions.SetAutoReconnect(!shown.AutoReconnect); };
            Wire(autoToggle, on => actions.SetAutoReconnect(on));
            Wire(startupToggle, on => actions.SetStartup(on));
            Wire(notifyToggle, on => actions.SetNotifications(on));
            activityFold.Checked += delegate { activityList.Visibility = Visibility.Visible; };
            activityFold.Unchecked += delegate { activityList.Visibility = Visibility.Collapsed; };
            Find<Button>("RestartButton").Click += delegate { if (actions != null) actions.RestartWindscribe(); };
            Find<Button>("LogButton").Click += delegate { if (actions != null) actions.OpenLog(); };
            Find<Button>("QuitButton").Click += delegate { if (actions != null) actions.Quit(); };

            clock.Interval = TimeSpan.FromSeconds(1);
            clock.Tick += delegate { if (shown != null) statusText.Text = StatusLine(shown); };

            Apply(new Snapshot(), new List<LogEntry>());
            if (actions == null) return;

            window = new Window();
            window.Title = "Windscribe Watchdog";
            window.WindowStyle = WindowStyle.None;
            window.AllowsTransparency = true;
            window.Background = Brushes.Transparent;
            window.ResizeMode = ResizeMode.NoResize;
            window.SizeToContent = SizeToContent.WidthAndHeight;
            window.Content = root;
            using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("WindscribeWatchdog.watchdog.ico"))
                if (icon != null) window.Icon = BitmapFrame.Create(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            window.Closing += (s, e) => { if (!closing) { e.Cancel = true; window.Hide(); } };
            window.IsVisibleChanged += delegate
            {
                if (shown != null) SetSpinning(shown.Health == Health.Busy);
                if (!window.IsVisible) { clock.Stop(); return; }
                clock.Start();
                if (shown != null) statusText.Text = StatusLine(shown);
            };
            window.SizeChanged += delegate { KeepOnScreen(); };
            // Keep the blurred layers as bitmaps so the spinner doesn't make WPF blur them again every
            // frame. Only for the real window: off-screen previews hang with a bitmap cache.
            Find<Border>("Shadow").CacheMode = new BitmapCache();
            ringGlow.CacheMode = new BitmapCache();
            ElementHost.EnableModelessKeyboardInterop(window);

            Find<FrameworkElement>("TitleBar").MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) window.DragMove();
            };
            Find<Button>("MinimizeButton").Click += delegate { window.WindowState = WindowState.Minimized; };
            Find<Button>("CloseButton").Click += delegate { window.Hide(); };
        }

        public bool IsOpen
        {
            get { return window.IsVisible && window.WindowState != WindowState.Minimized; }
        }

        public void Show()
        {
            if (!placed)
            {
                // The first time, open next to the tray (bottom right).
                root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Rect area = SystemParameters.WorkArea;
                window.Left = area.Right - root.DesiredSize.Width + 6;
                window.Top = area.Bottom - root.DesiredSize.Height + 6;
                placed = true;
            }
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }

        public void Hide()
        {
            window.Hide();
        }

        public void Close()
        {
            closing = true;
            window.Close();
        }

        public void Apply(Snapshot s, List<LogEntry> activity)
        {
            shown = s;
            Color color = ColorFor(s);
            Brush brush = Frozen(color);
            pill.Background = Frozen(Color.FromArgb(0x26, color.R, color.G, color.B));
            pill.BorderBrush = brush;
            pillText.Foreground = brush;
            pillText.Text = PillFor(s);
            ring.Stroke = brush;
            ringGlow.Stroke = brush;
            powerIcon.Stroke = brush;
            Color glow = Color.FromArgb(0x46, color.R, color.G, color.B);
            if (glowStop.Color != glow)
            {
                glowStop.Color = glow;
                glowEdge.Color = Color.FromArgb(0x00, color.R, color.G, color.B);
            }
            SetSpinning(s.Health == Health.Busy);
            string ringLabel = s.AutoReconnect ? "Pause auto-reconnect" : "Turn auto-reconnect back on";
            ringButton.ToolTip = ringLabel;
            AutomationProperties.SetName(ringButton, ringLabel);

            WsStatus st = s.Status;
            bool connected = st != null && st.State == VpnState.Connected;
            protocolText.Text = st != null ? st.Protocol.Replace(':', ' ') : "";
            string where = s.Location.Length > 0 ? s.Location : "Windscribe";
            int dash = where.IndexOf(" - ", StringComparison.Ordinal);
            cityRun.Text = dash > 0 ? where.Substring(0, dash) : where;
            nickRun.Text = dash > 0 ? where.Substring(dash + 3) : "";
            placeText.Opacity = connected ? 1 : 0.6;
            statusText.Text = StatusLine(s);
            ipText.Text = connected && st.Ip.Length > 0 ? st.Ip : "—";
            firewallText.Text = st != null && st.Firewall.Length > 0 ? st.Firewall : "—";
            internetText.Text = st == null ? "—" : st.InternetUp ? "Available" : "Offline";

            updating = true;
            autoToggle.IsChecked = s.AutoReconnect;
            startupToggle.IsChecked = s.StartWithWindows;
            notifyToggle.IsChecked = s.Notifications;
            updating = false;

            fixesText.Text = s.FixesToday.ToString(CultureInfo.InvariantCulture);
            lastFixText.Text = s.LastFix == DateTime.MinValue ? "None yet"
                : s.LastFix.ToString(s.LastFix.Date == DateTime.Today ? "HH:mm" : "MMM d, HH:mm", CultureInfo.InvariantCulture) +
                  (s.LastFixText.Length > 0 ? "  ·  " + s.LastFixText : "");
            ShowActivity(activity);
        }

        void ShowActivity(List<LogEntry> entries)
        {
            var key = new StringBuilder();
            foreach (LogEntry e in entries) key.Append(e.Time.Ticks).Append(e.Text);
            if (key.ToString() == shownActivity) return;
            shownActivity = key.ToString();

            activityList.Children.Clear();
            if (entries.Count == 0)
                activityList.Children.Add(new TextBlock { Text = "Nothing yet.", FontSize = 12, Foreground = TimeBrush });
            for (int i = 0; i < entries.Count && i < ActivityRows; i++)
            {
                LogEntry e = entries[i];
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new TextBlock
                {
                    Text = e.Time.ToString(e.Time.Date == DateTime.Today ? "HH:mm" : "MMM d", CultureInfo.InvariantCulture),
                    FontSize = 12, Foreground = TimeBrush
                });
                var text = new TextBlock { Text = e.Text, FontSize = 12, Foreground = EntryBrush, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = e.Text };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                activityList.Children.Add(row);
            }
        }

        void SetSpinning(bool on)
        {
            spinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (window == null) return; // previews are still images
            on = on && window.IsVisible; // no animation while hidden
            if (on == spinning) return;
            spinning = on;
            DoubleAnimation turn = null;
            if (on)
            {
                turn = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.1))) { RepeatBehavior = RepeatBehavior.Forever };
                Timeline.SetDesiredFrameRate(turn, 24); // smooth enough, and far cheaper than 60
            }
            spinnerTurn.BeginAnimation(RotateTransform.AngleProperty, turn);
        }

        void ClipCard()
        {
            card.Clip = new RectangleGeometry(new Rect(card.RenderSize), Corner, Corner);
        }

        void KeepOnScreen()
        {
            // The window grows when the activity list opens; keep its bottom edge above the taskbar.
            Rect area = SystemParameters.WorkArea;
            if (window.Top + window.ActualHeight > area.Bottom + 14)
                window.Top = Math.Max(area.Top, area.Bottom + 14 - window.ActualHeight);
        }

        void Wire(CheckBox toggle, Action<bool> change)
        {
            RoutedEventHandler handler = delegate
            {
                if (!updating && actions != null) change(toggle.IsChecked == true);
            };
            toggle.Checked += handler;
            toggle.Unchecked += handler;
        }

        T Find<T>(string name) where T : class
        {
            var element = root.FindName(name) as T;
            if (element == null) throw new InvalidOperationException("MainWindow.xaml has no " + typeof(T).Name + " named " + name);
            return element;
        }

        static string StatusLine(Snapshot s)
        {
            if (s.Health != Health.Good) return s.Headline;
            if (s.ConnectedSince == DateTime.MinValue) return "Connected";
            TimeSpan up = DateTime.Now - s.ConnectedSince;
            return "Connected for " + (up.TotalMinutes < 1 ? "under a minute"
                : up.TotalHours < 1 ? (int)up.TotalMinutes + " min"
                : (int)up.TotalHours + " h " + up.Minutes + " min");
        }

        static string PillFor(Snapshot s)
        {
            switch (s.Health)
            {
                case Health.Good: return "ON";
                case Health.Busy: return "FIXING";
                case Health.Problem: return "PROBLEM";
                default: return s.AutoReconnect ? "STANDBY" : "PAUSED";
            }
        }

        static Color ColorFor(Snapshot s)
        {
            switch (s.Health)
            {
                case Health.Good: return GoodColor;
                case Health.Busy: return BusyColor;
                case Health.Problem: return ProblemColor;
                default: return IdleColor;
            }
        }

        static Brush Frozen(Color color)
        {
            Brush brush;
            if (!brushes.TryGetValue(color, out brush))
            {
                brush = new SolidColorBrush(color);
                brush.Freeze();
                brushes[color] = brush;
            }
            return brush;
        }

        // Draws the window with made-up data into a PNG (for the README); needs no Windscribe.
        public static void RenderPreview(string file, string state)
        {
            var w = new MainWindow(null);
            w.activityFold.IsChecked = true;
            w.Apply(Demo(state), DemoActivity());
            if (state == "fixing") w.spinnerTurn.Angle = 40;
            w.root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            w.root.Arrange(new Rect(w.root.DesiredSize));
            w.root.UpdateLayout();
            w.ClipCard();

            const double scale = 2;
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(w.root.ActualWidth * scale), (int)Math.Ceiling(w.root.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(w.root);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream output = File.Create(file)) png.Save(output);
        }

        static Snapshot Demo(string state)
        {
            DateTime now = DateTime.Now;
            var st = new WsStatus { Answered = true, LoggedIn = true, LoginText = "Logged in", InternetUp = true,
                                    Protocol = "IKEv2:500", Ip = "198.51.100.23", Firewall = "On", Location = "Toronto - Maple" };
            var s = new Snapshot { Location = "Toronto - Maple", Status = st, FixesToday = 3, LastFix = now.AddMinutes(-2),
                                   LastFixText = "down 21 s", StartWithWindows = true, Notifications = true };
            switch (state)
            {
                case "fixing":
                    s.Health = Health.Busy;
                    s.Headline = "Windscribe error 11 – reconnecting in 6 s";
                    st.State = VpnState.Error;
                    st.ErrorCode = 11;
                    break;
                case "paused":
                    s.Health = Health.Idle;
                    s.AutoReconnect = false;
                    s.Headline = "Paused – auto-reconnect is off";
                    st.State = VpnState.Connected;
                    s.ConnectedSince = now.AddMinutes(-47);
                    break;
                case "problem":
                    s.Health = Health.Problem;
                    s.Headline = "Windscribe isn't answering";
                    s.Status = null;
                    break;
                default:
                    s.Health = Health.Good;
                    s.Headline = "Connected – Toronto - Maple";
                    st.State = VpnState.Connected;
                    s.ConnectedSince = now.AddHours(-2).AddMinutes(-13);
                    break;
            }
            return s;
        }

        static List<LogEntry> DemoActivity()
        {
            DateTime now = DateTime.Now;
            return new List<LogEntry>
            {
                new LogEntry { Time = now.AddMinutes(-2), Text = "Connected – Toronto - Maple" },
                new LogEntry { Time = now.AddMinutes(-2), Text = "Back online; the VPN was down about 21 s." },
                new LogEntry { Time = now.AddMinutes(-2), Text = "Pressed \"Yes\" on Windscribe's message \"Read-Only File\": Your hosts file is read-only." },
                new LogEntry { Time = now.AddMinutes(-3), Text = "Windscribe stopped with error 11 (it couldn't modify the Windows hosts file) – will reconnect it." },
                new LogEntry { Time = now.AddHours(-2), Text = "Watchdog started." },
            };
        }
    }
}
