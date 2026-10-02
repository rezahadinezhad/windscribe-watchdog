using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace WindscribeWatchdog
{
    public enum VpnState { Unknown, Connected, Connecting, Disconnecting, Disconnected, Error }

    public class WsStatus
    {
        public bool Answered;   // the CLI reached the app and printed its status
        public VpnState State;
        public int ErrorCode;   // set when State is Error, e.g. 11 = Windscribe couldn't modify the hosts file
        public string Location = "";
        public string Protocol = "";   // e.g. "IKEv2:500"
        public string Ip = "";         // the VPN's IP while connected
        public string Firewall = "";   // "On" / "Off"
        public bool InternetUp = true;
        public bool LoggedIn;
        public string LoginText = "";
        public string Raw = "";
    }

    // Everything the watchdog knows about Windscribe: its CLI, logs, app and service.
    public static class Windscribe
    {
        public const string ServiceName = "WindscribeService";
        public static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Windscribe\Windscribe2");

        static readonly string[] OwnProcesses = { "windscribe", "windscribeservice", "windscribe-cli", "windscribeopenvpn",
            "windscribewstunnel", "windscribectrld", "wireguardservice", "windscribewatchdog" };

        public static string FindCli()
        {
            foreach (string variable in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
            {
                string root = Environment.GetEnvironmentVariable(variable);
                if (string.IsNullOrEmpty(root)) continue;
                string path = Path.Combine(root, @"Windscribe\windscribe-cli.exe");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        public static int AppPid()
        {
            int pid = 0;
            foreach (Process p in Process.GetProcessesByName("Windscribe"))
            {
                if (pid == 0) pid = p.Id;
                p.Dispose();
            }
            return pid;
        }

        public static bool AppRunning()
        {
            return AppPid() != 0;
        }

        public static void KillApp()
        {
            foreach (Process p in Process.GetProcessesByName("Windscribe"))
            {
                try { p.Kill(); p.WaitForExit(10000); }
                catch (Exception) { }
                p.Dispose();
            }
        }

        public static void LaunchApp(string cli)
        {
            // The same way Windscribe starts with Windows.
            var psi = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(cli), "Windscribe.exe"), "--autostart");
            psi.UseShellExecute = true;
            psi.WorkingDirectory = Path.GetDirectoryName(cli);
            using (Process.Start(psi)) { }
        }

        public static bool ServiceRunning()
        {
            try
            {
                using (var service = new ServiceController(ServiceName))
                    return service.Status == ServiceControllerStatus.Running;
            }
            catch (Exception) { return true; } // can't tell, so don't act on it
        }

        // Windscribe's service lets signed-in users start it, so this works without admin rights.
        public static string StartService()
        {
            try
            {
                using (var service = new ServiceController(ServiceName))
                {
                    if (service.Status == ServiceControllerStatus.Running) return "it was already running";
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    return "started";
                }
            }
            catch (Exception ex) { return "couldn't start it: " + ex.Message; }
        }

        // Windscribe's installer or updater (the app is closed while they run).
        public static bool InstallerRunning()
        {
            bool found = false;
            foreach (Process p in Process.GetProcesses())
            {
                string name = p.ProcessName.ToLowerInvariant();
                if ((name.Contains("windscribe") && Array.IndexOf(OwnProcesses, name) < 0) || name == "installhelper")
                    found = true;
                p.Dispose();
            }
            return found;
        }

        public static WsStatus Status(string cli)
        {
            int exitCode;
            return Parse(Run(cli, "status", 15000, out exitCode));
        }

        // Parses `windscribe-cli status` output such as:
        //   Internet connectivity: available
        //   Login state: Logged in
        //   Firewall state: On
        //   Connect state: Connected: Ashburn - Bles Park
        //   Protocol: IKEv2:500
        //   VPN IP: 192.0.2.10
        // The CLI sometimes prefixes a line with "*" ("*Connect state: ..."), reports a failed
        // connection as "Connect state: Error: 11", and may say "Public IP" instead of "VPN IP".
        public static WsStatus Parse(string output)
        {
            var s = new WsStatus();
            s.Raw = output ?? "";
            string publicIp = "";
            foreach (string rawLine in s.Raw.Split('\n'))
            {
                string line = rawLine.Trim().TrimStart('*').Trim();
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string key = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (key == "Connect state")
                {
                    s.Answered = true;
                    int sep = value.IndexOf(':');
                    string head = sep >= 0 ? value.Substring(0, sep) : value;
                    string detail = sep >= 0 ? value.Substring(sep + 1).Trim() : "";
                    if (head.StartsWith("Connected", StringComparison.Ordinal)) s.State = VpnState.Connected;
                    else if (head.StartsWith("Connecting", StringComparison.Ordinal)) s.State = VpnState.Connecting;
                    else if (head.StartsWith("Disconnecting", StringComparison.Ordinal)) s.State = VpnState.Disconnecting;
                    else if (head.StartsWith("Disconnected", StringComparison.Ordinal)) s.State = VpnState.Disconnected;
                    else if (head.StartsWith("Error", StringComparison.Ordinal))
                    {
                        s.State = VpnState.Error;
                        int.TryParse(detail, out s.ErrorCode);
                    }
                    if (s.State == VpnState.Connected || s.State == VpnState.Connecting) s.Location = detail;
                }
                else if (key == "Internet connectivity")
                {
                    s.Answered = true;
                    s.InternetUp = !value.StartsWith("unavailable", StringComparison.OrdinalIgnoreCase);
                }
                else if (key == "Login state")
                {
                    s.Answered = true;
                    s.LoginText = value;
                    s.LoggedIn = value.Equals("Logged in", StringComparison.OrdinalIgnoreCase);
                }
                else if (key == "Firewall state") s.Firewall = value;
                else if (key == "Protocol") s.Protocol = value;
                else if (key == "VPN IP") s.Ip = value;
                else if (key == "Public IP") publicIp = value;
            }
            if (s.Ip.Length == 0 && s.State == VpnState.Connected) s.Ip = publicIp;
            return s;
        }

        public static bool LastDisconnectWasByUser()
        {
            return LastDisconnectWasByUser(Path.Combine(LogDir, "client.log"), Path.Combine(LogDir, "client.1.log"));
        }

        // Looks at Windscribe's own logs, newest first, for the most recent of: the Disconnect button
        // being used, a connection being started or made, or the app starting. Only the first means
        // the VPN is off on purpose.
        public static bool LastDisconnectWasByUser(params string[] logsNewestFirst)
        {
            foreach (string path in logsNewestFirst)
            {
                int marker = LastMarker(path);
                if (marker != 0) return marker > 0;
            }
            return false;
        }

        // How far each log has been read, so later calls only read what was added since.
        class LogScan
        {
            public long Position;
            public string Head = "";   // the file's first line; it changes when Windscribe starts a new log
            public int Marker;
            public DateTime LastConnectStart = DateTime.MinValue;
        }
        static readonly TimeSpan ReplacedConnectWindow = TimeSpan.FromSeconds(3);

        // The time at the start of a log line: {"tm": "2026-10-02 18:28:53.120", ...
        static DateTime LineTime(string line)
        {
            int at = line.IndexOf("\"tm\": \"", StringComparison.Ordinal);
            DateTime time;
            if (at >= 0 && line.Length >= at + 30 &&
                DateTime.TryParseExact(line.Substring(at + 7, 23), "yyyy-MM-dd HH:mm:ss.fff",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out time))
                return time;
            return DateTime.MaxValue; // unknown: treat a Disconnect as yours
        }
        static readonly Dictionary<string, LogScan> scans = new Dictionary<string, LogScan>();

        // +1: Disconnect pressed; -1: connect started/connected/app started; 0: neither found.
        static int LastMarker(string path)
        {
            lock (scans)
            {
                LogScan scan;
                if (!scans.TryGetValue(path, out scan)) scans[path] = scan = new LogScan();
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        string head = ReadHead(stream);
                        if (head != scan.Head || stream.Length < scan.Position)
                        {
                            scan.Head = head;
                            scan.Position = 0;
                            scan.Marker = 0;
                        }
                        stream.Seek(scan.Position, SeekOrigin.Begin);
                        var chunk = new byte[stream.Length - scan.Position];
                        int read = 0, n;
                        while (read < chunk.Length && (n = stream.Read(chunk, read, chunk.Length - read)) > 0) read += n;
                        int end = read == 0 ? 0 : Array.LastIndexOf(chunk, (byte)'\n', read - 1) + 1; // whole lines only
                        foreach (string line in Encoding.UTF8.GetString(chunk, 0, end).Split('\n'))
                        {
                            if (line.IndexOf("ConnectionManager::clickDisconnect()", StringComparison.Ordinal) >= 0)
                            {
                                // Windscribe also logs this when a new connect request replaces one in
                                // progress (it cancels and starts again right away); nobody clicks that fast.
                                if (LineTime(line) - scan.LastConnectStart > ReplacedConnectWindow) scan.Marker = 1;
                            }
                            else if (line.IndexOf("Connecting to \\\"", StringComparison.Ordinal) >= 0)
                            {
                                scan.Marker = -1;
                                scan.LastConnectStart = LineTime(line);
                            }
                            else if (line.IndexOf("onConnectionConnected", StringComparison.Ordinal) >= 0
                                  || line.IndexOf("=== Started ===", StringComparison.Ordinal) >= 0)
                                scan.Marker = -1;
                        }
                        scan.Position += end;
                    }
                }
                catch (IOException) { scan.Position = 0; scan.Marker = 0; scan.Head = ""; }
                catch (UnauthorizedAccessException) { scan.Position = 0; scan.Marker = 0; scan.Head = ""; }
                return scan.Marker;
            }
        }

        static string ReadHead(FileStream stream)
        {
            var buffer = new byte[Math.Min(120, (int)Math.Min(int.MaxValue, stream.Length))];
            stream.Seek(0, SeekOrigin.Begin);
            int read = stream.Read(buffer, 0, buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }

        // The real size of Windscribe's current log (the size Explorer shows can lag behind).
        // It grows whenever anything happens to the connection, so it tells the watchdog when to look.
        public static long LogLength()
        {
            try
            {
                using (var stream = new FileStream(Path.Combine(LogDir, "client.log"), FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete))
                    return stream.Length;
            }
            catch (IOException) { return -1; }
            catch (UnauthorizedAccessException) { return -1; }
        }

        public static bool LastExitWasClean()
        {
            return LastExitWasClean(Path.Combine(LogDir, "client.log"));
        }

        // While Windscribe isn't running: did its last session end with its normal shutdown steps
        // (you quit it, or Windows shut down) rather than a crash?
        public static bool LastExitWasClean(string path)
        {
            string tail = Tail(path, 32 * 1024);
            if (tail == null) return true; // nothing to judge by, so don't second-guess you
            return tail.Contains("Backend::cleanup()") || tail.Contains("Cleanup started") || tail.Contains("onShouldTerminate");
        }

        static string Tail(string path, int maxBytes)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Seek(Math.Max(0, stream.Length - maxBytes), SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public static string Run(string cli, string args, int timeoutMs, out int exitCode)
        {
            exitCode = -1;
            var output = new StringBuilder();
            var psi = new ProcessStartInfo(cli, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            // Not disposed: a reader may still signal them after we've given up waiting.
            var outDone = new ManualResetEvent(false);
            var errDone = new ManualResetEvent(false);
            using (var p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) outDone.Set(); else lock (output) output.AppendLine(e.Data);
                };
                p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) errDone.Set(); else lock (output) output.AppendLine(e.Data);
                };
                try { p.Start(); }
                catch (Exception ex) { return "could not start windscribe-cli: " + ex.Message; }
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (p.WaitForExit(timeoutMs))
                {
                    exitCode = p.ExitCode;
                    // Let the readers finish, but never wait forever: if the CLI started another
                    // process that inherited its output pipe, the pipe would never close.
                    outDone.WaitOne(3000);
                    errDone.WaitOne(3000);
                }
                else
                {
                    try { p.Kill(); } catch (Exception) { }
                    lock (output) output.AppendLine("(no answer after " + timeoutMs / 1000 + " s)");
                }
            }
            lock (output) return output.ToString();
        }
    }
}
