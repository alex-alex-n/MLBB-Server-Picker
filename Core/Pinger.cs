using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using Android.Systems;
using Android.Util;
using Java.IO;
using Java.Net;

namespace MlbbServerPicker.Core
{
    public sealed class PingResult
    {
        public string Ip;
        public int Sent, Received;
        public double MedianMs = -1, MinMs = -1, JitterMs;
    }

    /// <summary>
    /// ICMP-пингер. Чтобы массовый/частый пинг не искажал результаты:
    /// • серверы пингуются строго по одному (одна очередь, один поток);
    /// • 4 пробы с паузой 200 мс, результат раунда — медиана (выбросы отсекаются);
    /// • пауза между серверами, cooldown 3 с на один IP;
    /// • результат хранится отдельно от игрового RTT и не трогает LastSeen.
    /// Приложение не входит в список VPN-приложений, поэтому пинг идёт напрямую через сеть.
    /// </summary>
    public sealed class Pinger
    {
        const string Tag = "MLBB.Ping";
        public const int Samples = 4;
        const int SampleGapMs = 200;
        const int TimeoutMs = 1500;
        const int BetweenHostsMs = 150;
        public const int CooldownMs = 3000;

        public static Pinger Instance { get; } = new Pinger();

        readonly object _lock = new object();
        readonly Queue<string> _queue = new Queue<string>();
        readonly HashSet<string> _queued = new HashSet<string>();
        readonly Dictionary<string, long> _lastPing = new Dictionary<string, long>();
        static readonly Regex RxTime = new Regex(@"time[=<]\s*([\d.]+)", RegexOptions.Compiled);

        CancellationTokenSource _cts;
        volatile bool _running;
        volatile string _current;
        volatile int _total, _done;
        int _mode; // 0 — не определён, 1 — ICMP-сокет, 2 — /system/bin/ping, -1 — недоступен

        public bool IsBusy => _running;
        public string Current => _current;
        public int Total => _total;
        public int Done => _done;

        /// <summary>Вызываются из рабочего потока!</summary>
        public event Action<PingResult> Completed;
        public event Action BatchFinished;

        Pinger() { }

        public bool CheckAvailable()
        {
            if (_mode == 0) _mode = DetectMode();
            return _mode > 0;
        }

        /// <summary>Ставит IP в очередь. force=false — соблюдать cooldown. Возвращает число добавленных.</summary>
        public int Enqueue(IEnumerable<string> ips, bool force)
        {
            int added = 0;
            lock (_lock)
            {
                long now = Net.NowMs;
                foreach (var ip in ips)
                {
                    if (string.IsNullOrEmpty(ip) || _queued.Contains(ip)) continue;
                    if (!force && _lastPing.TryGetValue(ip, out var t) && now - t < CooldownMs) continue;
                    _queue.Enqueue(ip); _queued.Add(ip); added++;
                }
                if (added == 0) return 0;
                if (_running) _total += added;
                else StartWorkerLocked();
            }
            return added;
        }

        public void Cancel()
        {
            lock (_lock) { _queue.Clear(); _queued.Clear(); _cts?.Cancel(); }
        }

        void StartWorkerLocked()
        {
            _running = true; _total = _queue.Count; _done = 0;
            _cts = new CancellationTokenSource();
            var tok = _cts.Token;
            new Thread(() => Worker(tok)) { IsBackground = true, Name = "pinger" }.Start();
        }

        void Worker(CancellationToken tok)
        {
            try
            {
                while (true)
                {
                    string ip;
                    lock (_lock)
                    {
                        if (_queue.Count == 0 || tok.IsCancellationRequested) break;
                        ip = _queue.Dequeue(); _queued.Remove(ip);
                    }
                    _current = ip;
                    PingResult r = null;
                    try { r = PingRound(ip, tok); }
                    catch (Exception ex) { Log.Debug(Tag, ip + ": " + ex.Message); }
                    lock (_lock) { _lastPing[ip] = Net.NowMs; _done++; }
                    if (r != null && !tok.IsCancellationRequested)
                    {
                        StatsStore.Instance.ApplyPing(r);
                        try { Completed?.Invoke(r); } catch { }
                    }
                    if (tok.IsCancellationRequested) break;
                    Thread.Sleep(BetweenHostsMs);
                }
            }
            finally
            {
                lock (_lock)
                {
                    _current = null;
                    if (_queue.Count > 0 && !tok.IsCancellationRequested) StartWorkerLocked();
                    else { _running = false; _queue.Clear(); _queued.Clear(); }
                }
                try { BatchFinished?.Invoke(); } catch { }
            }
        }

        PingResult PingRound(string ip, CancellationToken tok)
        {
            if (!CheckAvailable()) return null;
            var r = new PingResult { Ip = ip };
            var times = new List<double>(Samples);
            uint ipn = Net.ParseIp(ip);

            for (int i = 0; i < Samples; i++)
            {
                if (tok.IsCancellationRequested) break;
                double t = _mode == 1 ? IcmpOnce(ipn, i + 1) : BinaryOnce(ip);
                r.Sent++;
                if (t >= 0) { times.Add(t); r.Received++; }
                if (i < Samples - 1) Thread.Sleep(SampleGapMs);
            }

            if (times.Count > 0)
            {
                times.Sort();
                r.MinMs = times[0];
                r.MedianMs = times.Count % 2 == 1
                    ? times[times.Count / 2]
                    : (times[times.Count / 2 - 1] + times[times.Count / 2]) / 2.0;
                r.JitterMs = times[times.Count - 1] - times[0];
            }
            return r;
        }

        static int DetectMode()
        {
            try
            {
                var fd = Os.Socket(OsConstants.AfInet, OsConstants.SockDgram, OsConstants.IpprotoIcmp);
                Os.Close(fd);
                return 1;
            }
            catch (Exception ex) { Log.Info(Tag, "ICMP-сокеты недоступны: " + ex.Message); }
            if (System.IO.File.Exists("/system/bin/ping")) return 2;
            return -1;
        }

        /// <summary>Одна ICMP-проба через SOCK_DGRAM/IPPROTO_ICMP. Возвращает RTT в мс или -1.</summary>
        static double IcmpOnce(uint ip, int seq)
        {
            FileDescriptor fd = null;
            try
            {
                fd = Os.Socket(OsConstants.AfInet, OsConstants.SockDgram, OsConstants.IpprotoIcmp);
                var pkt = new byte[8 + 32];
                pkt[0] = 8; pkt[1] = 0;                   // Echo Request
                Net.WriteU16(pkt, 4, 0x4D4C);             // id (ядро подменит)
                Net.WriteU16(pkt, 6, (ushort)seq);
                for (int i = 8; i < pkt.Length; i++) pkt[i] = (byte)i;
                Net.WriteU16(pkt, 2, 0);
                Net.WriteU16(pkt, 2, Net.Checksum(pkt, 0, pkt.Length));

                var addr = InetAddress.GetByAddress(Net.ToBytes(ip));
                long t0 = Stopwatch.GetTimestamp();
                Os.Sendto(fd, pkt, 0, pkt.Length, 0, addr, 0);

                var buf = new byte[256];
                var pfd = new[] { new StructPollfd { Fd = fd, Events = (short)OsConstants.Pollin } };
                long deadline = t0 + TimeoutMs * Stopwatch.Frequency / 1000;
                while (true)
                {
                    int remain = (int)((deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
                    if (remain <= 0) return -1;
                    if (Os.Poll(pfd, remain) <= 0) return -1;
                    int n = Os.Recvfrom(fd, buf, 0, buf.Length, 0, new InetSocketAddress(0));
                    if (n >= 8 && buf[0] == 0 && Net.ReadU16(buf, 6) == seq)
                        return (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                }
            }
            catch (Exception ex) { Log.Debug(Tag, "icmp: " + ex.Message); return -1; }
            finally { if (fd != null) try { Os.Close(fd); } catch { } }
        }

        /// <summary>Запасной вариант — системная утилита ping.</summary>
        static double BinaryOnce(string ip)
        {
            try
            {
                var p = Java.Lang.Runtime.GetRuntime().Exec(new[] { "/system/bin/ping", "-c", "1", "-W", "2", "-n", ip });
                double res = -1;
                using (var rd = new System.IO.StreamReader(p.InputStream))
                {
                    string line;
                    while ((line = rd.ReadLine()) != null)
                    {
                        var m = RxTime.Match(line);
                        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                            res = v;
                    }
                }
                p.WaitFor();
                return res;
            }
            catch (Exception ex) { Log.Debug(Tag, "ping bin: " + ex.Message); return -1; }
        }
    }
}
