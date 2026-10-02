using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace WindscribeWatchdog
{
    public enum Health { Good, Busy, Idle, Problem }

    // What the watchdog last saw and did, for the tray icon and the window.
    public class Snapshot
    {
        public Health Health = Health.Idle;
        public string Headline = "Starting…";
        public bool AutoReconnect = true;
        public WsStatus Status;                       // last usable answer from windscribe-cli, or null
        public string Location = "";                  // last connected location, e.g. "Kansas City - Barbecue"
        public DateTime ConnectedSince = DateTime.MinValue;
        public int FixesToday;
        public DateTime LastFix = DateTime.MinValue;
        public string LastFixText = "";               // e.g. "down 21 s"
        public bool StartWithWindows, Notifications;  // filled in by the tray app
    }

    class DialogOutcome
    {
        public WsDialog Dialog;
        public string Pressed;   // the button pressed, or null if it was left for you
        public string Error;
    }

    // The check loop. Every few seconds it asks Windscribe how it's doing and fixes what it can:
    //   - Windscribe dropping and not retrying, or stopping on an error (e.g. 11): reconnects it,
    //     retrying with growing pauses.
    //   - Windscribe's "Read-Only File" (hosts file) prompt: presses Yes, as you would; Windscribe then
    //     fixes the file itself and reconnects. Error notices with only an OK button: dismisses them.
    //   - Windscribe frozen (not answering) or stuck connecting for a long time: restarts the Windscribe
    //     app (at most 3 times per 30 minutes). Not for reconnects that simply fail: a restarted
    //     Windscribe has to sign in again, and a filtered network can block that ("SSL error"), which
    //     would leave the VPN down for longer.
    //   - Windscribe closing unexpectedly, or its service stopping: starts them again.
    // It leaves to you: a Disconnect you press (Windscribe logs "clickDisconnect()" for it), quitting
    // Windscribe yourself, signing in, and any question that would change a Windscribe setting.
    class Watchdog
    {
        const int SlowPollMs = 15000;   // while idle
        const int FastPollMs = 5000;    // while something is wrong, and the quick look while connected
        // While connected, each check only looks at the size of Windscribe's log (which grows whenever
        // anything happens to the connection) and asks windscribe-cli only when it grew, or this often.
        // That keeps the watchdog light and Windscribe's log small: every status check adds a line to it.
        static readonly TimeSpan QuietCheckInterval = TimeSpan.FromSeconds(60);
        const int GraceSeconds = 8;     // lets Windscribe finish what it's doing before stepping in
        static readonly int[] RetryDelaysSec = { 15, 30, 60, 120, 180 };
        static readonly TimeSpan StuckLimit = TimeSpan.FromMinutes(10);      // connecting or disconnecting that long
        static readonly TimeSpan SilentLimit = TimeSpan.FromSeconds(90);     // no usable answer that long
        static readonly TimeSpan RelaunchDelay = TimeSpan.FromSeconds(20);   // after the app closes unexpectedly
        static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(30);
        const int MaxRestartsPerWindow = 3;

        public volatile bool Enabled = true;
        public Action<Snapshot> OnSnapshot;             // raised on the worker thread
        public Action<string, string, bool> OnNotify;   // title, text, isWarning
        long lastBeat;                                  // UTC ticks at the start of each check

        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool stopping, restartRequested;

        // An outage runs from spotting a problem we should fix until the VPN is back.
        DateTime outageSince = DateTime.MinValue;
        DateTime nextAttempt;
        int attempts;
        bool acted;              // we did something during this outage, so tell you when it's over
        bool userDisconnected;
        DateTime lastCheck = DateTime.MinValue;
        DateTime busySince = DateTime.MinValue, silentSince = DateTime.MinValue, appGoneSince = DateTime.MinValue;
        readonly List<DateTime> restarts = new List<DateTime>();
        readonly HashSet<string> dialogsAnnounced = new HashSet<string>();
        Thread dialogThread;     // UI Automation can hang on a frozen window, so it runs on its own thread
        bool signedOutAnnounced, loginErrorAnnounced;
        string lastReport, lastNote;
        DateTime lastFullCheck = DateTime.MinValue;
        long logLengthSeen = -1;   // -2: take a fresh baseline at the next quick look
        bool lookedQuickly;        // the last check only looked at the log size, so there's nothing new to show

        // What the window shows.
        Health health = Health.Idle;
        string headline = "Starting…";
        WsStatus lastStatus;
        string lastLocation = "";
        DateTime connectedSince = DateTime.MinValue;
        int fixesToday;
        DateTime fixesDay = DateTime.Today, lastFix = DateTime.MinValue;
        string lastFixText = "";

        public DateTime LastBeat
        {
            get { return new DateTime(Interlocked.Read(ref lastBeat), DateTimeKind.Utc); }
        }

        public void Start()
        {
            Log.Write("Watchdog started.");
            if (!Directory.Exists(Windscribe.LogDir))
                Log.Write("Windscribe's log folder wasn't found, so a Disconnect you press can't be told apart " +
                          "from a drop. Turn off Auto-reconnect when you want the VPN off.");
            CountTodaysFixes();
            var thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Start();
        }

        public void Stop()
        {
            stopping = true;
            wake.Set();
        }

        public void Poke()
        {
            wake.Set();
        }

        public void RequestRestart()
        {
            restartRequested = true;
            wake.Set();
        }

        void Loop()
        {
            while (!stopping)
            {
                int waitMs;
                lookedQuickly = false;
                try { waitMs = Tick(); }
                catch (Exception ex) { Log.Write("Error: " + ex.Message); waitMs = SlowPollMs; }
                if (stopping) break;
                if (!lookedQuickly) Publish();
                wake.WaitOne(waitMs);
            }
        }

        int Tick()
        {
            Interlocked.Exchange(ref lastBeat, DateTime.UtcNow.Ticks);
            DateTime now = DateTime.Now;
            if (lastCheck != DateTime.MinValue && now - lastCheck > TimeSpan.FromMinutes(2))
            {
                // The PC was asleep: that time doesn't count against Windscribe.
                Log.Write("Back from sleep after " + Duration(now - lastCheck) + ".");
                busySince = silentSince = appGoneSince = DateTime.MinValue;
                if (outageSince != DateTime.MinValue) nextAttempt = now.AddSeconds(20);
            }
            lastCheck = now;

            if (!Enabled)
            {
                Reset();
                return Show(Health.Idle, "Paused – auto-reconnect is off", SlowPollMs, true);
            }
            string cli = Windscribe.FindCli();
            if (cli == null)
                return Show(Health.Problem, "windscribe-cli.exe not found", SlowPollMs, true);
            if (restartRequested)
            {
                restartRequested = false;
                return Restart(cli, "you asked for it");
            }
            if (health == Health.Good && now - lastFullCheck < QuietCheckInterval)
            {
                long length = Windscribe.LogLength();
                if (logLengthSeen == -2) logLengthSeen = length; // our own last check has been written by now
                if (length >= 0 && length == logLengthSeen)
                {
                    lookedQuickly = true;
                    return FastPollMs; // nothing happened
                }
            }
            lastFullCheck = now;
            logLengthSeen = -2;
            if (!Windscribe.AppRunning())
            {
                lastStatus = null;
                connectedSince = DateTime.MinValue;
                return AppGone(cli, now);
            }
            appGoneSince = DateTime.MinValue;

            if (HandleDialogs())
                return 3000; // let Windscribe act on the answer

            WsStatus st = Windscribe.Status(cli);
            if (!st.Answered)
                return Silent(cli, st, now);
            silentSince = DateTime.MinValue;
            lastStatus = st;
            if (st.State != VpnState.Connected) connectedSince = DateTime.MinValue;

            if (st.LoginText.StartsWith("Logged out", StringComparison.OrdinalIgnoreCase) ||
                st.LoginText.StartsWith("Logging out", StringComparison.OrdinalIgnoreCase) || st.LoginText.Length == 0)
            {
                busySince = DateTime.MinValue;
                Reset();
                if (!signedOutAnnounced)
                    Notify("Windscribe is signed out", "Sign in to Windscribe again; the watchdog can't do that for you.", true);
                signedOutAnnounced = true;
                return Show(Health.Idle, "Windscribe: " + (st.LoginText.Length > 0 ? st.LoginText : "not signed in"), SlowPollMs, true);
            }
            signedOutAnnounced = false;
            if (st.LoginText.StartsWith("Logging in", StringComparison.OrdinalIgnoreCase))
                return Show(Health.Busy, "Windscribe is signing in…", FastPollMs, true);
            if (!st.LoggedIn)
            {
                // Signed in, but Windscribe can't reach its servers right now, e.g. "Error: SSL error" on a
                // filtered network. The VPN still works with the session it has, so keep looking after it.
                if (!loginErrorAnnounced)
                {
                    Log.Write("Windscribe can't reach its servers (" + st.LoginText + "); still keeping the VPN connected.");
                    Notify("Windscribe can't reach its servers", "It reports \"" + st.LoginText + "\". The watchdog keeps " +
                           "reconnecting as usual. If Windscribe asks whether to ignore SSL errors, that's your call.", true);
                    loginErrorAnnounced = true;
                }
            }
            else loginErrorAnnounced = false;

            switch (st.State)
            {
                case VpnState.Connected:
                    if (connectedSince == DateTime.MinValue) connectedSince = now;
                    if (st.Location.Length > 0) lastLocation = st.Location;
                    if (acted && outageSince != DateTime.MinValue)
                    {
                        TimeSpan down = DateTime.Now - outageSince;
                        Log.Write("Back online; the VPN was down about " + Duration(down) + ".");
                        Notify("VPN reconnected", "Windscribe had dropped. It's connected again to " + st.Location + ".", false);
                        CountFix(DateTime.Now, "down " + Duration(down));
                    }
                    Reset();
                    busySince = DateTime.MinValue;
                    return Show(Health.Good, "Connected – " + st.Location, FastPollMs, true);

                case VpnState.Connecting:
                case VpnState.Disconnecting:
                {
                    bool connecting = st.State == VpnState.Connecting;
                    if (connecting) userDisconnected = false;
                    if (busySince == DateTime.MinValue) busySince = now;
                    else if (now - busySince > StuckLimit && RestartAllowed())
                        return Restart(cli, "it was stuck " + (connecting ? "connecting" : "disconnecting") +
                                            " for " + Duration(now - busySince));
                    return Show(Health.Busy, connecting ? "Windscribe is connecting…" : "Windscribe is disconnecting…",
                                FastPollMs, true);
                }

                case VpnState.Disconnected:
                case VpnState.Error:
                    busySince = DateTime.MinValue;
                    return Down(cli, st);

                default:
                    Note("Unexpected answer from windscribe-cli: " + OneLine(st.Raw));
                    return Show(Health.Problem, "Can't read Windscribe's status", FastPollMs, true);
            }
        }

        // Disconnected, or stopped on an error such as 11 – either way Windscribe has given up.
        int Down(string cli, WsStatus st)
        {
            if (!userDisconnected && Windscribe.LastDisconnectWasByUser())
            {
                Reset();
                userDisconnected = true;
            }
            if (userDisconnected)
                return Show(Health.Idle, "You disconnected – standing by", SlowPollMs, true);

            DateTime now = DateTime.Now;
            if (outageSince == DateTime.MinValue)
            {
                outageSince = now;
                nextAttempt = now.AddSeconds(GraceSeconds);
                Log.Write(st.State == VpnState.Error
                    ? "Windscribe stopped with " + ErrorText(st) + " – will reconnect it."
                    : "VPN is down and Windscribe isn't retrying – will reconnect it.");
            }
            if (!st.InternetUp)
                return Show(Health.Busy, "No internet – waiting for it", FastPollMs, true);
            if (now < nextAttempt)
            {
                int secs = (int)Math.Ceiling((nextAttempt - now).TotalSeconds);
                string text = attempts > 0 ? "Attempt " + attempts + " failed – retrying in " + secs + " s"
                    : st.State == VpnState.Error ? "Windscribe error " + st.ErrorCode + " – reconnecting in " + secs + " s"
                    : "VPN dropped – reconnecting in " + secs + " s";
                return Show(Health.Busy, text, Math.Min(FastPollMs, secs * 1000), false);
            }
            attempts++;
            acted = true;
            Show(Health.Busy, "Reconnecting… (attempt " + attempts + ")", 0, true);
            int exitCode;
            string said = Windscribe.Run(cli, "connect", 120000, out exitCode);
            Log.Write("  windscribe-cli connect (exit " + exitCode + "): " + OneLine(said));
            nextAttempt = DateTime.Now.AddSeconds(RetryDelaysSec[Math.Min(attempts, RetryDelaysSec.Length) - 1]);
            if (attempts == 3)
                Notify("Windscribe won't reconnect yet",
                       "Tried 3 times. The watchdog keeps retrying every few minutes.", true);
            return 1000; // check the result right away
        }

        // windscribe-cli gave no usable answer: Windscribe may be starting, busy, or frozen.
        int Silent(string cli, WsStatus st, DateTime now)
        {
            Note("No usable answer from windscribe-cli: " + (st.Raw.Trim().Length > 0 ? OneLine(st.Raw) : "(empty)"));
            if (silentSince == DateTime.MinValue) silentSince = now;
            else if (now - silentSince > SilentLimit && RestartAllowed())
                return Restart(cli, "it stopped answering for " + Duration(now - silentSince));
            return Show(Health.Problem, "Windscribe isn't answering", FastPollMs, true);
        }

        // The Windscribe app isn't running. If you quit it, leave it closed; if it crashed or its
        // service stopped, start them again.
        int AppGone(string cli, DateTime now)
        {
            busySince = silentSince = DateTime.MinValue;
            userDisconnected = false;
            if (Windscribe.InstallerRunning())
            {
                appGoneSince = DateTime.MinValue;
                return Show(Health.Idle, "Windscribe is being installed or updated", SlowPollMs, true);
            }
            bool serviceUp = Windscribe.ServiceRunning();
            if (serviceUp && Windscribe.LastExitWasClean())
            {
                Reset();
                appGoneSince = DateTime.MinValue;
                return Show(Health.Idle, "Windscribe app is closed", SlowPollMs, true);
            }
            if (appGoneSince == DateTime.MinValue)
            {
                appGoneSince = now;
                if (outageSince == DateTime.MinValue) outageSince = now;
                Log.Write(serviceUp ? "Windscribe closed unexpectedly." : "Windscribe's service stopped, so the app closed.");
            }
            if (now - appGoneSince < RelaunchDelay)
                return Show(Health.Busy, "Windscribe closed – starting it again shortly", FastPollMs, true);
            if (!RestartAllowed())
                return Show(Health.Problem, "Windscribe keeps closing – waiting before trying again", SlowPollMs, true);

            if (!serviceUp) Log.Write("Starting Windscribe's service: " + Windscribe.StartService() + ".");
            Log.Write("Starting the Windscribe app.");
            Windscribe.LaunchApp(cli);
            restarts.Add(now);
            appGoneSince = DateTime.MinValue;
            acted = true;
            nextAttempt = now.AddSeconds(30);
            Notify("Windscribe started again", serviceUp ? "It had closed unexpectedly." : "Its service had stopped.", true);
            return 10000;
        }

        int Restart(string cli, string reason)
        {
            Log.Write("Restarting Windscribe: " + reason + ".");
            Notify("Restarting Windscribe", "Because " + reason + ".", true);
            Windscribe.KillApp();
            if (!Windscribe.ServiceRunning()) Log.Write("Starting Windscribe's service: " + Windscribe.StartService() + ".");
            Thread.Sleep(2000);
            Windscribe.LaunchApp(cli);
            DateTime now = DateTime.Now;
            restarts.Add(now);
            busySince = silentSince = appGoneSince = DateTime.MinValue;
            acted = true;
            if (outageSince == DateTime.MinValue) outageSince = now;
            nextAttempt = now.AddSeconds(30); // Windscribe needs a moment to start and sign in
            return 10000;
        }

        bool RestartAllowed()
        {
            DateTime cutoff = DateTime.Now - RestartWindow;
            restarts.RemoveAll(t => t < cutoff);
            return restarts.Count < MaxRestartsPerWindow;
        }

        // Answers Windscribe message boxes that are safe to answer and tells you about the rest.
        // Returns true if it pressed something.
        bool HandleDialogs()
        {
            int pid = Windscribe.AppPid();
            List<IntPtr> windows = pid == 0 ? new List<IntPtr>() : Dialogs.Candidates(pid);
            if (windows.Count == 0)
            {
                dialogsAnnounced.Clear();
                return false;
            }
            if (dialogThread != null && dialogThread.IsAlive) return false; // the last check is still stuck

            var outcomes = new List<DialogOutcome>();
            dialogThread = new Thread(delegate()
            {
                foreach (IntPtr hwnd in windows)
                {
                    var o = new DialogOutcome();
                    try
                    {
                        o.Dialog = Dialogs.Inspect(hwnd);
                        string choice = Dialogs.Choose(o.Dialog);
                        if (choice != null)
                        {
                            if (Dialogs.Press(o.Dialog, choice)) o.Pressed = choice;
                            else o.Error = "couldn't press \"" + choice + "\"";
                        }
                    }
                    catch (Exception ex) { o.Error = ex.Message; }
                    lock (outcomes) outcomes.Add(o);
                }
            });
            dialogThread.IsBackground = true;
            dialogThread.Start();
            if (!dialogThread.Join(15000))
            {
                Note("Reading Windscribe's windows took too long; will try again.");
                return false;
            }

            bool pressed = false;
            foreach (DialogOutcome o in outcomes)
            {
                if (o.Dialog == null || !Dialogs.IsMessage(o.Dialog))
                {
                    if (o.Error != null) Note("Couldn't read a Windscribe window: " + o.Error);
                    continue;
                }
                string what = "\"" + o.Dialog.Title + "\": " + o.Dialog.Text;
                if (o.Pressed != null)
                {
                    pressed = acted = true;
                    if (outageSince == DateTime.MinValue) outageSince = DateTime.Now;
                    Log.Write("Pressed \"" + o.Pressed + "\" on Windscribe's message " + what);
                    Notify("Answered a Windscribe message", "Pressed \"" + o.Pressed + "\" on: " + Shorten(o.Dialog.Text, 150), false);
                }
                else if (dialogsAnnounced.Add(o.Dialog.Handle + what))
                {
                    Log.Write("Left for you to answer – Windscribe's message " + what +
                              (o.Error != null ? " (" + o.Error + ")" : ""));
                    Notify("Windscribe needs your answer", Shorten(o.Dialog.Title + ": " + o.Dialog.Text, 200), true);
                }
            }
            return pressed;
        }

        void Reset()
        {
            outageSince = DateTime.MinValue;
            attempts = 0;
            acted = false;
            userDisconnected = false;
        }

        // Fixes from earlier today, so the count survives a restart of the watchdog.
        void CountTodaysFixes()
        {
            List<LogEntry> entries = Log.Recent(300);
            for (int i = entries.Count - 1; i >= 0; i--) // oldest first
            {
                LogEntry e = entries[i];
                if (e.Time.Date != DateTime.Today || !e.Text.StartsWith("Back online", StringComparison.Ordinal)) continue;
                int about = e.Text.IndexOf("about ", StringComparison.Ordinal);
                CountFix(e.Time, about >= 0 ? "down " + e.Text.Substring(about + 6).TrimEnd('.') : "");
            }
        }

        void CountFix(DateTime when, string text)
        {
            if (fixesDay != DateTime.Today)
            {
                fixesDay = DateTime.Today;
                fixesToday = 0;
            }
            fixesToday++;
            lastFix = when;
            lastFixText = text;
        }

        void Publish()
        {
            Action<Snapshot> handler = OnSnapshot;
            if (handler == null) return;
            if (fixesDay != DateTime.Today)
            {
                fixesDay = DateTime.Today;
                fixesToday = 0;
            }
            var s = new Snapshot();
            s.Health = health;
            s.Headline = headline;
            s.AutoReconnect = Enabled;
            s.Status = lastStatus;
            s.Location = lastLocation;
            s.ConnectedSince = connectedSince;
            s.FixesToday = fixesToday;
            s.LastFix = lastFix;
            s.LastFixText = lastFixText;
            handler(s);
        }

        int Show(Health h, string text, int waitMs, bool log)
        {
            string key = h + "|" + text;
            if (key != lastReport)
            {
                lastReport = key;
                health = h;
                headline = text;
                if (log) Log.Write(text);
                Publish();
            }
            return waitMs;
        }

        void Note(string text)
        {
            if (text == lastNote) return;
            lastNote = text;
            Log.Write(text);
        }

        void Notify(string title, string text, bool warning)
        {
            if (OnNotify != null) OnNotify(title, text, warning);
        }

        // Windscribe only reports a number; 11 is the one its log explains ("Can't modify hosts file").
        static string ErrorText(WsStatus st)
        {
            return "error " + st.ErrorCode + (st.ErrorCode == 11 ? " (it couldn't modify the Windows hosts file)" : "");
        }

        static string Duration(TimeSpan span)
        {
            if (span.TotalSeconds < 90) return (int)span.TotalSeconds + " s";
            if (span.TotalMinutes < 90) return (int)span.TotalMinutes + " min";
            return (int)span.TotalHours + " h " + span.Minutes + " min";
        }

        static string Shorten(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }

        static string OneLine(string text)
        {
            return string.Join(" | ", text.Trim().Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
