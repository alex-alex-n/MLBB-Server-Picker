using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Android.Content;
using Android.Util;

namespace MlbbServerPicker.Core
{
    public sealed class StatsStore
    {
        const string Tag = "MLBB.Stats";
        const int MaxJunk = 200;
        public static StatsStore Instance { get; } = new StatsStore();

        readonly object _lock = new object();
        readonly Dictionary<uint, ServerStat> _byIp = new Dictionary<uint, ServerStat>();
        volatile HashSet<uint> _blocked = new HashSet<uint>();
        volatile HashSet<uint> _favorites = new HashSet<uint>();
        string _path;
        volatile bool _dirty;

        volatile int _activeFlows;
        long _matchIp;
        long _matchTicks;

        public event Action BlockListChanged;

        public int ActiveFlows { get => _activeFlows; set => _activeFlows = value; }
        public bool HasFavorites => _favorites.Count > 0;
        public int FavoriteCount => _favorites.Count;

        public void Init(Context ctx)
        {
            if (_path != null) return;
            _path = Path.Combine(ctx.FilesDir.AbsolutePath, "servers.json");
            Load();
        }

        public bool IsBlocked(uint ip) => _blocked.Contains(ip);
        public bool IsFavorite(uint ip) => _favorites.Contains(ip);

        public string CurrentMatchIp
        {
            get
            {
                if (Net.NowMs - Interlocked.Read(ref _matchTicks) >= 5000) return null;
                return Net.IpToString((uint)Interlocked.Read(ref _matchIp));
            }
        }

        public bool IsMatchActive(uint ip) =>
            (uint)Interlocked.Read(ref _matchIp) == ip && Net.NowMs - Interlocked.Read(ref _matchTicks) < 5000;

        public void TouchMatch(uint ip)
        {
            Interlocked.Exchange(ref _matchIp, ip);
            Interlocked.Exchange(ref _matchTicks, Net.NowMs);
        }

        public bool TryAutoBlock(uint ip, int dport)
        {
            if (!AppSettings.StrictMode) return false;
            var fav = _favorites;
            if (fav.Count == 0 || fav.Contains(ip)) return false;
            if (!AppSettings.ProbePorts.Contains(dport)) return false;
            if (_blocked.Contains(ip)) return true;

            lock (_lock)
            {
                var s = GetOrAdd(ip);
                s.UdpPorts.Add(dport);
                s.Blocked = true; s.AutoBlocked = true; s.BlockPending = false;
                s.BlockedDrops++;
                RebuildLocked();
            }
            BlockListChanged?.Invoke();
            return true;
        }

        public void NoteForeignMatch(uint ip, int dport)
        {
            if (!AppSettings.StrictMode) return;
            var fav = _favorites;
            if (fav.Count == 0 || fav.Contains(ip)) return;
            if (!AppSettings.MatchPorts.Contains(dport)) return;
            lock (_lock)
            {
                var s = GetOrAdd(ip);
                if (!s.Blocked && !s.BlockPending) { s.BlockPending = true; s.AutoBlocked = true; _dirty = true; }
            }
        }

        ServerStat GetOrAdd(uint ip)
        {
            if (!_byIp.TryGetValue(ip, out var s))
            {
                s = new ServerStat { Ip = Net.IpToString(ip), FirstSeen = DateTime.Now };
                _byIp[ip] = s;
            }
            s.LastSeen = DateTime.Now;
            _dirty = true;
            return s;
        }

        public void RecordUdpSent(uint ip, int port, int bytes)
        {
            lock (_lock) { var s = GetOrAdd(ip); s.UdpPktsSent++; s.UdpBytesSent += bytes; s.UdpPorts.Add(port); }
        }

        public void RecordUdpRecv(uint ip, int port, int bytes, double rttMs)
        {
            lock (_lock)
            {
                var s = GetOrAdd(ip); s.UdpPktsRecv++; s.UdpBytesRecv += bytes; s.UdpPorts.Add(port);
                if (rttMs >= 0) AddRtt(s, rttMs);
            }
        }

        public void RecordTcpSent(uint ip, int port, int bytes)
        {
            lock (_lock) { var s = GetOrAdd(ip); s.TcpPktsSent++; s.TcpBytesSent += bytes; s.TcpPorts.Add(port); }
        }

        public void RecordTcpRecv(uint ip, int port, int bytes)
        {
            lock (_lock) { var s = GetOrAdd(ip); s.TcpPktsRecv++; s.TcpBytesRecv += bytes; s.TcpPorts.Add(port); }
        }

        public void RecordIcmpSent(uint ip) { lock (_lock) { GetOrAdd(ip).IcmpSent++; } }

        public void RecordIcmpRecv(uint ip, double rttMs)
        {
            lock (_lock) { var s = GetOrAdd(ip); s.IcmpRecv++; if (rttMs >= 0) AddRtt(s, rttMs); }
        }

        public void RecordBlockedDrop(uint ip) { lock (_lock) { GetOrAdd(ip).BlockedDrops++; } }

        public void RecordMatchStart(uint ip, int port)
        {
            lock (_lock) { var s = GetOrAdd(ip); s.Matches++; s.LastMatch = DateTime.Now; s.UdpPorts.Add(port); }
        }

        static void AddRtt(ServerStat s, double ms)
        {
            s.RttLast = ms;
            s.RttMin = s.RttMin < 0 ? ms : Math.Min(s.RttMin, ms);
            s.RttAvg = s.RttSamples == 0 ? ms : s.RttAvg * 0.8 + ms * 0.2;
            s.RttSamples++;
        }

        /// <summary>Результат нашего ICMP-пинга. Не трогает LastSeen и игровой RTT.</summary>
        public void ApplyPing(PingResult r)
        {
            uint ip;
            try { ip = Net.ParseIp(r.Ip); } catch { return; }
            lock (_lock)
            {
                if (!_byIp.TryGetValue(ip, out var s)) return;
                s.PingUpdated = DateTime.Now;
                s.PingRounds++;
                s.PingLoss = r.Sent == 0 ? 1 : 1.0 - (double)r.Received / r.Sent;
                if (r.Received > 0)
                {
                    s.PingLast = r.MedianMs;
                    s.PingJitter = r.JitterMs;
                    s.PingMin = s.PingMin < 0 ? r.MinMs : Math.Min(s.PingMin, r.MinMs);
                    s.PingAvg = s.PingAvg < 0 ? r.MedianMs : s.PingAvg * 0.6 + r.MedianMs * 0.4;
                    s.PingFails = 0;
                }
                else { s.PingLast = -1; s.PingFails++; }
                _dirty = true;
            }
        }

        public void ApplyGeo(uint ip, GeoInfo g)
        {
            lock (_lock)
            {
                if (!_byIp.TryGetValue(ip, out var s)) return;
                s.GeoUpdated = DateTime.Now;
                if (g == null || g.IsEmpty) { s.GeoAttempts++; }
                else
                {
                    s.GeoCountry = g.Country ?? ""; s.GeoCountryCode = g.CountryCode ?? ""; s.GeoCity = g.City ?? "";
                    s.GeoIsp = g.Isp ?? ""; s.GeoOrg = g.Org ?? ""; s.GeoAsn = g.Asn ?? ""; s.GeoSource = g.Source ?? "";
                    s.GeoAttempts = 0;
                }
                _dirty = true;
            }
        }

        public void ScheduleGeoLookups()
        {
            List<uint> todo = null;
            var now = DateTime.Now;
            lock (_lock)
            {
                foreach (var kv in _byIp)
                {
                    var s = kv.Value;
                    if (!(s.Favorite || s.IsGame || s.Role == ServerRole.Lobby)) continue;
                    if (s.HasGeo) { if ((now - s.GeoUpdated).TotalDays < 30) continue; }
                    else if (s.GeoAttempts > 0)
                    {
                        var wait = s.GeoAttempts >= 5 ? TimeSpan.FromHours(24) : TimeSpan.FromMinutes(5 * s.GeoAttempts);
                        if (now - s.GeoUpdated < wait) continue;
                    }
                    (todo ??= new List<uint>()).Add(kv.Key);
                    if (todo.Count >= 25) break;
                }
            }
            if (todo != null) foreach (var ip in todo) GeoResolver.Instance.Enqueue(ip);
        }

        public ServerStat Get(string ip)
        {
            lock (_lock) { var s = FindLocked(ip); return s?.Clone(); }
        }

        public void SetBlocked(string ip, bool blocked)
        {
            lock (_lock)
            {
                var s = FindLocked(ip);
                if (s == null) return;
                s.Blocked = blocked; s.BlockPending = false; s.AutoBlocked = false;
                if (blocked) s.Favorite = false;
                RebuildLocked();
            }
            BlockListChanged?.Invoke();
        }

        public void SetBlockPending(string ip)
        {
            lock (_lock)
            {
                var s = FindLocked(ip);
                if (s == null) return;
                s.BlockPending = true; s.AutoBlocked = false; s.Favorite = false;
                RebuildLocked();
            }
            BlockListChanged?.Invoke();
        }

        public void SetFavorite(string ip, bool fav)
        {
            lock (_lock)
            {
                var s = FindLocked(ip);
                if (s == null) return;
                s.Favorite = fav;
                if (fav) { s.Blocked = false; s.BlockPending = false; s.AutoBlocked = false; }
                RebuildLocked();
            }
            BlockListChanged?.Invoke();
        }

        public List<string> KeepOnly(string ip, out int pending)
        {
            var changed = new List<string>(); pending = 0;
            lock (_lock)
            {
                var fav = FindLocked(ip);
                if (fav == null) return changed;
                fav.Favorite = true; fav.Blocked = false; fav.BlockPending = false; fav.AutoBlocked = false;
                BlockOthersLocked(changed, ref pending);
            }
            BlockListChanged?.Invoke();
            return changed;
        }

        public List<string> BlockAllExceptFavorites(out int pending)
        {
            var changed = new List<string>(); pending = 0;
            lock (_lock)
            {
                if (_favorites.Count == 0) return changed;
                BlockOthersLocked(changed, ref pending);
            }
            BlockListChanged?.Invoke();
            return changed;
        }

        void BlockOthersLocked(List<string> changed, ref int pending)
        {
            foreach (var kv in _byIp)
            {
                var s = kv.Value;
                if (s.Favorite || !s.IsGame || s.Blocked) continue;
                if (IsMatchActive(kv.Key))
                {
                    if (!s.BlockPending) { s.BlockPending = true; pending++; changed.Add(s.Ip); }
                }
                else
                {
                    s.Blocked = true; s.BlockPending = false; s.AutoBlocked = false; changed.Add(s.Ip);
                }
            }
            RebuildLocked();
        }

        public void Unblock(IEnumerable<string> ips)
        {
            lock (_lock)
            {
                foreach (var ip in ips)
                {
                    var s = FindLocked(ip);
                    if (s == null) continue;
                    s.Blocked = false; s.BlockPending = false; s.AutoBlocked = false;
                }
                RebuildLocked();
            }
            BlockListChanged?.Invoke();
        }

        public void UnblockAll(bool clearFavorites)
        {
            lock (_lock)
            {
                foreach (var s in _byIp.Values)
                {
                    s.Blocked = false; s.BlockPending = false; s.AutoBlocked = false;
                    if (clearFavorites) s.Favorite = false;
                }
                RebuildLocked();
            }
            BlockListChanged?.Invoke();
        }

        public void ApplyPendingBlocks()
        {
            bool changed = false;
            lock (_lock)
            {
                foreach (var s in _byIp.Values)
                {
                    if (s.BlockPending && !s.Blocked && (DateTime.Now - s.LastSeen).TotalSeconds > 15)
                    {
                        s.Blocked = true; s.BlockPending = false; changed = true;
                    }
                }
                if (changed) RebuildLocked();
            }
            if (changed) BlockListChanged?.Invoke();
        }

        public void SetLabel(string ip, string label)
        {
            lock (_lock) { var s = FindLocked(ip); if (s != null) { s.Label = label ?? ""; _dirty = true; } }
        }

        public List<ServerStat> Snapshot()
        {
            lock (_lock) return _byIp.Values.Select(v => v.Clone()).ToList();
        }

        public void ClearStats(bool full)
        {
            lock (_lock)
            {
                if (full) _byIp.Clear();
                else
                {
                    foreach (var key in _byIp.Keys.ToList())
                    {
                        var s = _byIp[key];
                        bool keep = s.IsGame || s.Favorite || s.Blocked || s.BlockPending ||
                                    !string.IsNullOrEmpty(s.Label) || s.Role == ServerRole.Lobby;
                        if (!keep) { _byIp.Remove(key); continue; }
                        ResetCounters(s);
                    }
                }
                RebuildLocked();
            }
            BlockListChanged?.Invoke();
            SaveIfDirty();
        }

        static void ResetCounters(ServerStat s)
        {
            s.UdpPktsSent = s.UdpPktsRecv = s.UdpBytesSent = s.UdpBytesRecv = 0;
            s.TcpPktsSent = s.TcpPktsRecv = s.TcpBytesSent = s.TcpBytesRecv = 0;
            s.IcmpSent = s.IcmpRecv = 0;
            s.RttAvg = s.RttMin = s.RttLast = -1; s.RttSamples = 0;
            s.PingAvg = s.PingMin = s.PingLast = -1; s.PingJitter = 0; s.PingLoss = 0; s.PingRounds = 0; s.PingFails = 0;
            s.BlockedDrops = 0;
        }

        ServerStat FindLocked(string ip)
        {
            foreach (var s in _byIp.Values) if (s.Ip == ip) return s;
            return null;
        }

        void RebuildLocked()
        {
            _blocked = new HashSet<uint>(_byIp.Where(kv => kv.Value.Blocked).Select(kv => kv.Key));
            _favorites = new HashSet<uint>(_byIp.Where(kv => kv.Value.Favorite).Select(kv => kv.Key));
            _dirty = true;
        }

        void PruneLocked()
        {
            var now = DateTime.Now;
            var junk = new List<KeyValuePair<uint, ServerStat>>();
            foreach (var kv in _byIp)
            {
                var s = kv.Value;
                if (s.IsGame || s.Favorite || s.Blocked || s.BlockPending || !string.IsNullOrEmpty(s.Label) || s.Role == ServerRole.Lobby) continue;
                junk.Add(kv);
            }
            foreach (var kv in junk)
                if ((now - kv.Value.LastSeen).TotalHours > 24) _byIp.Remove(kv.Key);
            foreach (var kv in junk.Where(kv => _byIp.ContainsKey(kv.Key)).OrderByDescending(kv => kv.Value.LastSeen).Skip(MaxJunk).ToList())
                _byIp.Remove(kv.Key);
        }

        public void SaveIfDirty()
        {
            if (!_dirty) return;
            try { Save(); } catch (Exception ex) { Log.Warn(Tag, "save: " + ex.Message); }
        }

        public void Save()
        {
            if (_path == null) return;
            string json;
            lock (_lock)
            {
                PruneLocked();
                json = JsonSerializer.Serialize(_byIp.Values.ToList(), StatsJsonContext.Default.ListServerStat);
                _dirty = false;
            }
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, true);
        }

        void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var list = JsonSerializer.Deserialize(File.ReadAllText(_path), StatsJsonContext.Default.ListServerStat);
                if (list == null) return;
                lock (_lock)
                {
                    _byIp.Clear();
                    foreach (var s in list)
                    {
                        try
                        {
                            s.Label ??= ""; s.GeoCountry ??= ""; s.GeoCountryCode ??= ""; s.GeoCity ??= "";
                            s.GeoIsp ??= ""; s.GeoOrg ??= ""; s.GeoAsn ??= ""; s.GeoSource ??= "";
                            s.UdpPorts ??= new SortedSet<int>(); s.TcpPorts ??= new SortedSet<int>();
                            _byIp[Net.ParseIp(s.Ip)] = s;
                        }
                        catch { }
                    }
                    RebuildLocked();
                    _dirty = false;
                }
            }
            catch (Exception ex) { Log.Warn(Tag, "load: " + ex.Message); }
        }

        public string ExportCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("ip;label;role;country;city;isp;asn;game_ping_avg_ms;game_ping_min_ms;icmp_ping_avg_ms;icmp_ping_min_ms;icmp_loss;udp_sent_pkts;udp_recv_pkts;udp_sent_bytes;udp_recv_bytes;udp_loss;tcp_pkts;matches;first_seen;last_seen;favorite;blocked");
            foreach (var s in Snapshot().OrderBy(x => x.Role).ThenBy(x => x.Ip))
            {
                sb.Append(s.Ip).Append(';').Append(Csv(s.Label)).Append(';').Append(s.Role).Append(';')
                  .Append(Csv(s.GeoCountry)).Append(';').Append(Csv(s.GeoCity)).Append(';')
                  .Append(Csv(s.Provider)).Append(';').Append(Csv(s.GeoAsn)).Append(';')
                  .Append(s.HasRtt ? s.RttAvg.ToString("0.0") : "").Append(';')
                  .Append(s.HasRtt ? s.RttMin.ToString("0.0") : "").Append(';')
                  .Append(s.IcmpOk ? s.PingAvg.ToString("0.0") : "").Append(';')
                  .Append(s.PingMin >= 0 ? s.PingMin.ToString("0.0") : "").Append(';')
                  .Append(s.PingRounds > 0 ? s.PingLoss.ToString("0.00") : "").Append(';')
                  .Append(s.UdpPktsSent).Append(';').Append(s.UdpPktsRecv).Append(';')
                  .Append(s.UdpBytesSent).Append(';').Append(s.UdpBytesRecv).Append(';')
                  .Append(s.UdpLoss.ToString("0.00")).Append(';')
                  .Append(s.TcpPktsSent + s.TcpPktsRecv).Append(';')
                  .Append(s.Matches).Append(';')
                  .Append(s.FirstSeen.ToString("yyyy-MM-dd HH:mm:ss")).Append(';')
                  .Append(s.LastSeen.ToString("yyyy-MM-dd HH:mm:ss")).Append(';')
                  .Append(s.Favorite ? "1" : "0").Append(';')
                  .Append(s.Blocked ? "1" : "0").AppendLine();
            }
            return sb.ToString();
        }

        static string Csv(string s) => (s ?? "").Replace(';', ',').Replace('\n', ' ');
    }
}
