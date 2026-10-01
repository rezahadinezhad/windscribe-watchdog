using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace WindscribeWatchdog
{
    public class LogEntry
    {
        public DateTime Time;
        public string Text;
    }

    // The activity log: watchdog.log next to the exe, plus the latest entries in memory for the window.
    static class Log
    {
        public static readonly string FilePath = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "watchdog.log");
        const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
        const int Keep = 300;
        static readonly object gate = new object();
        static readonly List<LogEntry> recent = new List<LogEntry>();
        static bool loaded;

        public static void Write(string message)
        {
            DateTime now = DateTime.Now;
            lock (gate)
            {
                Load();
                Remember(now, message);
                try
                {
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > 1024 * 1024)
                    {
                        string old = FilePath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, now.ToString(TimeFormat, CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        // The latest entries, newest first, leaving out indented detail lines.
        public static List<LogEntry> Recent(int count)
        {
            lock (gate)
            {
                Load();
                var result = new List<LogEntry>();
                for (int i = recent.Count - 1; i >= 0 && result.Count < count; i--)
                    if (!recent[i].Text.StartsWith(" ", StringComparison.Ordinal)) result.Add(recent[i]);
                return result;
            }
        }

        // Picks up where the file left off, so the window shows history from before this start.
        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Seek(Math.Max(0, stream.Length - 64 * 1024), SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            DateTime time;
                            if (line.Length > TimeFormat.Length + 2 &&
                                DateTime.TryParseExact(line.Substring(0, TimeFormat.Length), TimeFormat,
                                    CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
                                Remember(time, line.Substring(TimeFormat.Length + 2));
                        }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static void Remember(DateTime time, string text)
        {
            recent.Add(new LogEntry { Time = time, Text = text });
            if (recent.Count > Keep) recent.RemoveRange(0, recent.Count - Keep);
        }
    }
}
