using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace WindscribeWatchdog
{
    public class WsDialog
    {
        public IntPtr Handle;
        public string Title = "";
        public string Text = "";
        public List<string> Buttons = new List<string>();
    }

    // Finds Windscribe's message boxes and answers the ones that are safe to answer.
    public static class Dialogs
    {
        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        const int GWL_STYLE = -16;
        const int WS_CAPTION = 0x00C00000;
        const uint GW_OWNER = 4;

        static readonly string[] Affirmative = { "Yes", "OK", "Fix Issue", "Fix", "Fix it", "Fix now" };
        static readonly string[] Acknowledge = { "OK", "Close" };
        static readonly string[] ErrorWords = { "connect", "adapter", "protocol", "configuration", "tunnel", "service", "error", "failed" };

        // Visible windows of the process other than Windscribe's frameless main window. This sends
        // no messages to Windscribe, so it's safe even if Windscribe is frozen.
        public static List<IntPtr> Candidates(int pid)
        {
            var found = new List<IntPtr>();
            EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
            {
                uint windowPid;
                GetWindowThreadProcessId(hwnd, out windowPid);
                if (windowPid != pid || !IsWindowVisible(hwnd)) return true;
                bool hasTitleBar = (GetWindowLong(hwnd, GWL_STYLE) & WS_CAPTION) == WS_CAPTION;
                if (hasTitleBar || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero || WindowText(hwnd) != "Windscribe")
                    found.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // Reads a window's title, message and buttons through UI Automation.
        public static WsDialog Inspect(IntPtr hwnd)
        {
            var d = new WsDialog();
            d.Handle = hwnd;
            d.Title = WindowText(hwnd);
            AutomationElement window = AutomationElement.FromHandle(hwnd);
            foreach (AutomationElement e in window.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                string name = Clean(e.Current.Name);
                if (name.Length == 0) continue;
                ControlType type = e.Current.ControlType;
                if (type == ControlType.Button && !InTitleBar(e)) d.Buttons.Add(name);
                else if (type == ControlType.Text) d.Text = d.Text.Length == 0 ? name : d.Text + " " + name;
            }
            return d;
        }

        // A message box: some text and a few buttons.
        public static bool IsMessage(WsDialog d)
        {
            return d.Text.Length > 0 && d.Buttons.Count > 0 && d.Buttons.Count <= 4;
        }

        // The button to press, or null to leave the window for you.
        public static string Choose(WsDialog d)
        {
            if (!IsMessage(d)) return null;
            string all = (d.Title + " " + d.Text).ToLowerInvariant();
            // Windscribe's own fix for its hosts-file error; pressing it is what you'd do by hand.
            if (d.Title == "Read-Only File" || all.Contains("hosts file is read-only"))
                return Pick(d, Affirmative);
            // A notice with a single OK only acknowledges an error; pressing it changes nothing.
            if (d.Buttons.Count == 1 && Pick(d, Acknowledge) != null)
                foreach (string word in ErrorWords)
                    if (all.Contains(word)) return d.Buttons[0];
            return null;
        }

        public static bool Press(WsDialog d, string button)
        {
            AutomationElement window = AutomationElement.FromHandle(d.Handle);
            var buttons = window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement e in buttons)
            {
                if (Clean(e.Current.Name) != button || InTitleBar(e)) continue;
                object pattern;
                if (!e.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) continue;
                try { ((InvokePattern)pattern).Invoke(); }
                catch (ElementNotAvailableException) { } // the window closed in response
                catch (COMException) { }                 // or the app exited ("Windscribe will now exit")
                return true;
            }
            return false;
        }

        static string Pick(WsDialog d, string[] wanted)
        {
            foreach (string w in wanted)
                foreach (string b in d.Buttons)
                    if (string.Equals(b, w, StringComparison.OrdinalIgnoreCase)) return b;
            return null;
        }

        static bool InTitleBar(AutomationElement e)
        {
            AutomationElement parent = TreeWalker.ControlViewWalker.GetParent(e);
            return parent != null && parent.Current.ControlType == ControlType.TitleBar;
        }

        static string Clean(string name)
        {
            return (name ?? "").Replace("&", "").Trim();
        }

        static string WindowText(IntPtr hwnd)
        {
            var text = new StringBuilder(256);
            GetWindowText(hwnd, text, text.Capacity);
            return text.ToString();
        }
    }
}
