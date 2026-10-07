using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace MlbbServerPicker.Core
{
    public enum ServerRole { Unknown = 0, Match = 1, Probe = 2, Lobby = 3, Service = 4 }

    /// <summary>Накопленная статистика по одному IP. Сохраняется в JSON.</summary>
    public sealed class ServerStat
    {
        public string Ip { get; set; } = "";
        public string Label { get; set; } = "";

        public long UdpPktsSent { get; set; }
        public long UdpPktsRecv { get; set; }
        public long UdpBytesSent { get; set; }
        public long UdpBytesRecv { get; set; }

        public long TcpPktsSent { get; set; }
        public long TcpPktsRecv { get; set; }
        public long TcpBytesSent { get; set; }
        public long TcpBytesRecv { get; set; }

        public long IcmpSent { get; set; }
        public long IcmpRecv { get; set; }

        // Игровой RTT (по пробам игры на UDP 30190) — «честный» пинг в бою
        public double RttAvg { get; set; } = -1;
        public double RttMin { get; set; } = -1;
        public double RttLast { get; set; } = -1;
        public int RttSamples { get; set; }

        // Наш ICMP-пинг (кнопка «Пинг» / авто-пинг избранного) — хранится отдельно
        public double PingAvg { get; set; } = -1;     // EMA по раундам
        public double PingLast { get; set; } = -1;    // медиана последнего раунда, -1 = нет ответа
        public double PingMin { get; set; } = -1;
        public double PingJitter { get; set; }
        public double PingLoss { get; set; }          // потери в последнем раунде 0..1
        public int PingRounds { get; set; }
        public int PingFails { get; set; }            // раундов подряд без ответа
        public DateTime PingUpdated { get; set; }

        public SortedSet<int> UdpPorts { get; set; } = new SortedSet<int>();
        public SortedSet<int> TcpPorts { get; set; } = new SortedSet<int>();

        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public DateTime LastMatch { get; set; }
        public int Matches { get; set; }

        public bool Blocked { get; set; }
        public bool BlockPending { get; set; }
        public bool AutoBlocked { get; set; }
        public bool Favorite { get; set; }

        public long BlockedDrops { get; set; }

        public string GeoCountry { get; set; } = "";
        public string GeoCountryCode { get; set; } = "";
        public string GeoCity { get; set; } = "";
        public string GeoIsp { get; set; } = "";
        public string GeoOrg { get; set; } = "";
        public string GeoAsn { get; set; } = "";
        public string GeoSource { get; set; } = "";
        public DateTime GeoUpdated { get; set; }
        public int GeoAttempts { get; set; }

        [JsonIgnore] public bool HasRtt => RttSamples > 0;
        [JsonIgnore] public bool IcmpOk => PingRounds > 0 && PingLast >= 0 && PingAvg >= 0;
        [JsonIgnore] public bool IcmpDead => PingRounds > 0 && PingLast < 0;

        /// <summary>Лучшая доступная оценка для сортировки: ICMP, иначе игровой RTT, иначе «в конец».</summary>
        [JsonIgnore] public double BestMs => IcmpOk ? PingAvg : HasRtt ? RttAvg : 9999;
        [JsonIgnore] public double GameMs => HasRtt ? RttAvg : 9999;

        [JsonIgnore]
        public double UdpLoss => UdpPktsSent == 0 ? 0 : Math.Max(0, 1.0 - (double)UdpPktsRecv / UdpPktsSent);

        [JsonIgnore]
        public bool IsGame =>
            Matches > 0 || UdpPorts.Overlaps(AppSettings.MatchPorts) || UdpPorts.Overlaps(AppSettings.ProbePorts);

        [JsonIgnore]
        public bool HasGeo =>
            !string.IsNullOrWhiteSpace(GeoCountry) || !string.IsNullOrWhiteSpace(GeoCity) ||
            !string.IsNullOrWhiteSpace(GeoIsp) || !string.IsNullOrWhiteSpace(GeoOrg) || !string.IsNullOrWhiteSpace(GeoAsn);

        [JsonIgnore]
        public string Provider => !string.IsNullOrWhiteSpace(GeoIsp) ? GeoIsp : !string.IsNullOrWhiteSpace(GeoOrg) ? GeoOrg : "";

        [JsonIgnore]
        public string GeoLine
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(GeoCity)) parts.Add(GeoCity);
                if (!string.IsNullOrWhiteSpace(GeoCountry)) parts.Add(GeoCountry);
                var location = string.Join(", ", parts);
                var provider = Provider;
                if (!string.IsNullOrWhiteSpace(location) && !string.IsNullOrWhiteSpace(provider)) return location + " · " + provider;
                if (!string.IsNullOrWhiteSpace(location)) return location;
                if (!string.IsNullOrWhiteSpace(provider)) return provider;
                if (!string.IsNullOrWhiteSpace(GeoAsn)) return GeoAsn;
                return null;
            }
        }

        [JsonIgnore]
        public string Flag
        {
            get
            {
                var cc = GeoCountryCode?.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(cc) || cc.Length != 2) return "";
                int a = char.ConvertToUtf32(cc, 0) - 'A' + 0x1F1E6;
                int b = char.ConvertToUtf32(cc, 1) - 'A' + 0x1F1E6;
                if (a < 0x1F1E6 || a > 0x1F1FF || b < 0x1F1E6 || b > 0x1F1FF) return "";
                return char.ConvertFromUtf32(a) + char.ConvertFromUtf32(b);
            }
        }

        [JsonIgnore]
        public ServerRole Role
        {
            get
            {
                if (Matches > 0 || UdpPorts.Overlaps(AppSettings.MatchPorts)) return ServerRole.Match;
                if (UdpPorts.Overlaps(AppSettings.ProbePorts)) return ServerRole.Probe;
                if (TcpPorts.Overlaps(AppSettings.LobbyPorts)) return ServerRole.Lobby;
                if (TcpPorts.Contains(443) || TcpPorts.Contains(80) || UdpPorts.Contains(53) || UdpPorts.Contains(443)) return ServerRole.Service;
                return ServerRole.Unknown;
            }
        }

        public ServerStat Clone()
        {
            var c = (ServerStat)MemberwiseClone();
            c.UdpPorts = new SortedSet<int>(UdpPorts ?? Enumerable.Empty<int>());
            c.TcpPorts = new SortedSet<int>(TcpPorts ?? Enumerable.Empty<int>());
            return c;
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(List<ServerStat>))]
    internal partial class StatsJsonContext : JsonSerializerContext { }
}
