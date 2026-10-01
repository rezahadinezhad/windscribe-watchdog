using System;
using Microsoft.Win32;

namespace WindscribeWatchdog
{
    // Your choices, kept in HKCU\Software\WindscribeWatchdog.
    static class Settings
    {
        const string KeyPath = @"Software\WindscribeWatchdog";

        // Set when you choose Quit, so the keepalive task doesn't bring it back.
        public static bool UserExited
        {
            get { return Get("UserExited", false); }
            set { Set("UserExited", value); }
        }

        // "Start with Windows"; on unless you turn it off.
        public static bool StartWithWindows
        {
            get { return Get("StartWithWindows", true); }
            set { Set("StartWithWindows", value); }
        }

        // Pop-up notifications; on unless you turn them off.
        public static bool Notifications
        {
            get { return Get("Notifications", true); }
            set { Set("Notifications", value); }
        }

        static bool Get(string name, bool fallback)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    object value = key == null ? null : key.GetValue(name);
                    return value == null ? fallback : Convert.ToInt32(value) != 0;
                }
            }
            catch (Exception) { return fallback; }
        }

        static void Set(string name, bool value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}
