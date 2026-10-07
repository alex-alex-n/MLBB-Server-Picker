using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Android.Net;
using Android.Util;
using MlbbServerPicker.Core;

namespace MlbbServerPicker.Vpn
{
    /// <summary>UDP NAT: каждый поток (srcPort,dst,dport) → защищённый UDP-сокет.</summary>
    internal sealed class UdpForwarder : IDisposable
    {
        const string Tag = "MLBB.Udp";
        const int IdleMs = 60_000;
        const int MaxDatagram = 1472;

        sealed class Session
        {
            public Socket Sock;
            public uint ClientIp;
            public ushort SrcPort;
            public uint DstIp;
            public ushort DstPort;
            public long LastActive;
            public long LastSendTs;
            public volatile bool AwaitingReply;
            public long PktsSent, PktsRecv;
            public bool IsMatch;
            public readonly CancellationTokenSource Cts = new CancellationTokenSource();
        }

        readonly TunEngine _engine;
        readonly VpnService _svc;
        readonly ConcurrentDictionary<long, Session> _sessions = new ConcurrentDictionary<long, Session>();

        public UdpForwarder(TunEngine engine, VpnService svc) { _engine = engine; _svc = svc; }

        public int Count => _sessions.Count;

        static long FlowKey(ushort sport, uint dst, ushort dport) => ((long)sport << 48) | ((long)dport << 32) | dst;

        public void Handle(in Ipv4Packet ip)
        {
            var b = ip.Buf; int o = ip.PayloadOffset;
            if (ip.PayloadLen < 8) return;
            ushort sport = Net.ReadU16(b, o), dport = Net.ReadU16(b, o + 2);
            int udpLen = Net.ReadU16(b, o + 4);
            int dataLen = Math.Min(udpLen, ip.PayloadLen) - 8;
            if (dataLen < 0) return;

            var stats = StatsStore.Instance;
            long key = FlowKey(sport, ip.Dst, dport);
            if (!_sessions.TryGetValue(key, out var s))
            {
                // Режим «Только избранные»: пробу к чужому игровому серверу не выпускаем,
                // сервер автоматически попадает в блок-лист — для игры он «не отвечает».
                if (stats.TryAutoBlock(ip.Dst, dport)) return;

                s = Open(key, ip.Src, sport, ip.Dst, dport);
                if (s == null) return;
                stats.NoteForeignMatch(ip.Dst, dport);
            }

            try { s.Sock.Send(b, o + 8, dataLen, SocketFlags.None); }
            catch (Exception ex)
            {
                Log.Debug(Tag, $"send {Net.IpToString(ip.Dst)}:{dport}: {ex.Message}");
                Close(key, s);
                return;
            }

            s.LastActive = Net.NowMs;
            s.LastSendTs = Stopwatch.GetTimestamp();
            s.AwaitingReply = true;
            s.PktsSent++;

            stats.RecordUdpSent(ip.Dst, dport, ip.Len);

            // Эвристика «идёт матч»: длинный двунаправленный UDP-поток
            if (!s.IsMatch && s.PktsSent >= 300 && s.PktsRecv >= 100)
            {
                s.IsMatch = true;
                stats.RecordMatchStart(ip.Dst, dport);
            }
            if (s.IsMatch) stats.TouchMatch(ip.Dst);
        }

        Session Open(long key, uint clientIp, ushort sport, uint dst, ushort dport)
        {
            try
            {
                var sock = new Socket(AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, ProtocolType.Udp);
                _svc.Protect(sock.Handle.ToInt32());
                sock.Connect(new IPEndPoint(Net.ToIPAddress(dst), dport));
                var s = new Session
                {
                    Sock = sock, ClientIp = clientIp, SrcPort = sport, DstIp = dst, DstPort = dport, LastActive = Net.NowMs
                };
                _sessions[key] = s;
                _ = ReceiveLoopAsync(key, s);
                return s;
            }
            catch (Exception ex)
            {
                Log.Warn(Tag, $"open {Net.IpToString(dst)}:{dport}: {ex.Message}");
                return null;
            }
        }

        async Task ReceiveLoopAsync(long key, Session s)
        {
            var buf = new byte[8192];
            try
            {
                while (!s.Cts.IsCancellationRequested)
                {
                    int n = await s.Sock.ReceiveAsync(new Memory<byte>(buf), SocketFlags.None, s.Cts.Token).ConfigureAwait(false);
                    if (n <= 0) continue;

                    double rtt = -1;
                    if (s.AwaitingReply)
                    {
                        s.AwaitingReply = false;
                        if (AppSettings.ProbePorts.Contains(s.DstPort))
                            rtt = (Stopwatch.GetTimestamp() - s.LastSendTs) * 1000.0 / Stopwatch.Frequency;
                    }
                    s.LastActive = Net.NowMs;
                    s.PktsRecv++;
                    if (n > MaxDatagram) continue;

                    var pkt = Packets.BuildUdp(s.DstIp, s.DstPort, s.ClientIp, s.SrcPort, buf, 0, n);
                    _engine.Write(pkt, pkt.Length);
                    StatsStore.Instance.RecordUdpRecv(s.DstIp, s.DstPort, pkt.Length, rtt);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
            finally { Close(key, s); }
        }

        void Close(long key, Session s)
        {
            _sessions.TryRemove(new KeyValuePair<long, Session>(key, s));
            try { s.Cts.Cancel(); } catch { }
            try { s.Sock.Close(); } catch { }
        }

        public void Cleanup()
        {
            long now = Net.NowMs;
            foreach (var kv in _sessions)
                if (now - kv.Value.LastActive > IdleMs) Close(kv.Key, kv.Value);
        }

        public void DropBlocked()
        {
            var store = StatsStore.Instance;
            foreach (var kv in _sessions)
                if (store.IsBlocked(kv.Value.DstIp)) Close(kv.Key, kv.Value);
        }

        public void Dispose()
        {
            foreach (var kv in _sessions) Close(kv.Key, kv.Value);
        }
    }
}
