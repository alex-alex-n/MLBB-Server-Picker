using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Android.Net;
using Android.OS;
using Android.Systems;
using Android.Util;
using Java.IO;
using Java.Net;
using MlbbServerPicker.Core;

namespace MlbbServerPicker.Vpn
{
    /// <summary>
    /// Пробрасывает ICMP echo игры через непривилегированные ping-сокеты (SOCK_DGRAM/IPPROTO_ICMP).
    /// Один сокет на ICMP-id клиента, один poll-поток на все сокеты. RTT считаем сами.
    /// </summary>
    internal sealed class IcmpForwarder : IDisposable
    {
        const string Tag = "MLBB.Icmp";
        const int IdleMs = 20_000;

        sealed class Session
        {
            public FileDescriptor Fd;
            public ushort Id;
            public uint ClientIp;
            public long LastActive;
            public readonly Dictionary<ushort, long> Pending = new Dictionary<ushort, long>();
        }

        readonly TunEngine _engine;
        readonly VpnService _svc;
        readonly object _lock = new object();
        readonly Dictionary<ushort, Session> _sessions = new Dictionary<ushort, Session>();
        readonly Thread _thread;
        volatile bool _running = true;
        volatile bool _supported = true;

        public IcmpForwarder(TunEngine engine, VpnService svc)
        {
            _engine = engine; _svc = svc;
            _thread = new Thread(PollLoop) { IsBackground = true, Name = "icmp-poll" };
            _thread.Start();
        }

        public void Handle(in Ipv4Packet ip)
        {
            if (!_supported) return;
            int o = ip.PayloadOffset, len = ip.PayloadLen;
            if (len < 8 || ip.Buf[o] != 8) return; // только Echo Request
            ushort id = Net.ReadU16(ip.Buf, o + 4), seq = Net.ReadU16(ip.Buf, o + 6);

            Session s;
            lock (_lock)
            {
                if (!_sessions.TryGetValue(id, out s))
                {
                    try
                    {
                        var fd = Os.Socket(OsConstants.AfInet, OsConstants.SockDgram, OsConstants.IpprotoIcmp);
                        using (var dup = ParcelFileDescriptor.Dup(fd)) _svc.Protect(dup.Fd);
                        s = new Session { Fd = fd, Id = id, ClientIp = ip.Src };
                        _sessions[id] = s;
                    }
                    catch (Exception ex)
                    {
                        _supported = false;
                        Log.Warn(Tag, "ping-сокеты недоступны, ICMP игры будет отбрасываться: " + ex.Message);
                        return;
                    }
                }
                s.LastActive = Net.NowMs;
                s.Pending[seq] = Stopwatch.GetTimestamp();
            }

            try
            {
                Os.Sendto(s.Fd, ip.Buf, o, len, 0, InetAddress.GetByAddress(Net.ToBytes(ip.Dst)), 0);
                StatsStore.Instance.RecordIcmpSent(ip.Dst);
            }
            catch (Exception ex) { Log.Debug(Tag, "sendto: " + ex.Message); }
        }

        void PollLoop()
        {
            var buf = new byte[4096];
            while (_running)
            {
                Session[] sess;
                StructPollfd[] fds;
                lock (_lock)
                {
                    sess = _sessions.Values.ToArray();
                    fds = new StructPollfd[sess.Length];
                    for (int i = 0; i < sess.Length; i++)
                        fds[i] = new StructPollfd { Fd = sess[i].Fd, Events = (short)OsConstants.Pollin };
                }
                if (fds.Length == 0) { Thread.Sleep(200); continue; }

                int ready;
                try { ready = Os.Poll(fds, 300); }
                catch (Exception) { Thread.Sleep(100); continue; }
                if (ready <= 0) continue;

                for (int i = 0; i < fds.Length; i++)
                {
                    if ((fds[i].Revents & OsConstants.Pollin) == 0) continue;
                    var s = sess[i];
                    try
                    {
                        var from = new InetSocketAddress(0);
                        int n = Os.Recvfrom(s.Fd, buf, 0, buf.Length, 0, from);
                        if (n < 8 || buf[0] != 0) continue; // только Echo Reply

                        ushort seq = Net.ReadU16(buf, 6);
                        double rtt = -1;
                        lock (_lock)
                        {
                            if (s.Pending.Remove(seq, out long ts))
                                rtt = (Stopwatch.GetTimestamp() - ts) * 1000.0 / Stopwatch.Frequency;
                        }

                        // ядро подменило id — возвращаем оригинальный и пересчитываем checksum
                        Net.WriteU16(buf, 4, s.Id);
                        Net.WriteU16(buf, 2, 0);
                        Net.WriteU16(buf, 2, Net.Checksum(buf, 0, n));

                        var addr = from.Address;
                        if (addr == null) continue;
                        uint srcIp = Net.FromBytes(addr.GetAddress());

                        var pkt = Packets.BuildIcmp(srcIp, s.ClientIp, buf, 0, n);
                        _engine.Write(pkt, pkt.Length);
                        StatsStore.Instance.RecordIcmpRecv(srcIp, rtt);
                    }
                    catch (Exception ex) { Log.Debug(Tag, "recv: " + ex.Message); }
                }
            }
        }

        public void Cleanup()
        {
            long now = Net.NowMs;
            lock (_lock)
            {
                foreach (var id in _sessions.Keys.ToList())
                {
                    var s = _sessions[id];
                    if (now - s.LastActive > IdleMs)
                    {
                        try { Os.Close(s.Fd); } catch { }
                        _sessions.Remove(id);
                    }
                }
            }
        }

        public void Dispose()
        {
            _running = false;
            lock (_lock)
            {
                foreach (var s in _sessions.Values) { try { Os.Close(s.Fd); } catch { } }
                _sessions.Clear();
            }
        }
    }
}
