using System;
using System.Collections.Generic;
using System.Linq;
using Android.Content;

namespace MlbbServerPicker.Core
{
    /// <summary>Настройки (SharedPreferences). Порты/пакеты/DNS применяются при следующем запуске фильтра.</summary>
    public static class AppSettings
    {
        const string Prefs = "settings";

        public static volatile bool RstBlocked = true;
        public static volatile bool StrictMode = false;
        public static volatile bool HelpShown = false;
        public static volatile bool BatteryAsked = false;

        /// <summary>"" = как в системе, "ru", "en".</summary>
        public static string Language = "";
        public static volatile bool AutoPing = true;
        public static int AutoPingInterval = 10;   // секунд
        public static int SortMode = 0;            // 0 — по пингу, 1 — по игровому RTT, 2 — по активности

        public static string Packages = "com.mobile.legends,com.mobilelegends.hwag,com.mobilelegends.mi";
        public static string Dns = "8.8.8.8,1.1.1.1";
        public static string MatchPortsText = "5003,5053";
        public static string ProbePortsText = "30190";
        public static string LobbyPortsText = "30101,30021,30220";

        public static HashSet<int> MatchPorts = new HashSet<int> { 5003, 5053 };
        public static HashSet<int> ProbePorts = new HashSet<int> { 30190 };
        public static HashSet<int> LobbyPorts = new HashSet<int> { 30101, 30021, 30220 };

        static bool _loaded;

        public static IEnumerable<string> PackageList =>
            Packages.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim());

        public static IEnumerable<string> DnsList =>
            Dns.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim());

        public static void Load(Context c)
        {
            if (_loaded) return;
            _loaded = true;
            var p = c.GetSharedPreferences(Prefs, FileCreationMode.Private);
            RstBlocked = p.GetBoolean("rst", true);
            StrictMode = p.GetBoolean("strict", false);
            HelpShown = p.GetBoolean("help_shown", false);
            BatteryAsked = p.GetBoolean("battery_asked", false);
            Language = p.GetString("lang", "");
            AutoPing = p.GetBoolean("autoping", true);
            AutoPingInterval = p.GetInt("autoping_int", 10);
            SortMode = p.GetInt("sort", 0);
            Packages = p.GetString("packages", Packages);
            Dns = p.GetString("dns", Dns);
            MatchPortsText = p.GetString("match_ports", MatchPortsText);
            ProbePortsText = p.GetString("probe_ports", ProbePortsText);
            LobbyPortsText = p.GetString("lobby_ports", LobbyPortsText);
            Rebuild();
        }

        public static void Save(Context c)
        {
            var e = c.GetSharedPreferences(Prefs, FileCreationMode.Private).Edit();
            e.PutBoolean("rst", RstBlocked);
            e.PutBoolean("strict", StrictMode);
            e.PutBoolean("help_shown", HelpShown);
            e.PutBoolean("battery_asked", BatteryAsked);
            e.PutString("lang", Language ?? "");
            e.PutBoolean("autoping", AutoPing);
            e.PutInt("autoping_int", AutoPingInterval);
            e.PutInt("sort", SortMode);
            e.PutString("packages", Packages);
            e.PutString("dns", Dns);
            e.PutString("match_ports", MatchPortsText);
            e.PutString("probe_ports", ProbePortsText);
            e.PutString("lobby_ports", LobbyPortsText);
            e.Apply();
            Rebuild();
        }

        static void Rebuild()
        {
            MatchPorts = ParsePorts(MatchPortsText);
            ProbePorts = ParsePorts(ProbePortsText);
            LobbyPorts = ParsePorts(LobbyPortsText);
        }

        static HashSet<int> ParsePorts(string s)
        {
            var set = new HashSet<int>();
            foreach (var part in s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part.Trim(), out int v) && v > 0 && v < 65536) set.Add(v);
            return set;
        }
    }
}
