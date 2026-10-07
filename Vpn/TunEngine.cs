using System;
using System.Threading;
using Android.Net;
using Android.OS;
using Android.Util;
using MlbbServerPicker.Core;

namespace MlbbServerPicker.Vpn
{
    /// <summary>Читает пакеты из TUN, применяет блок-лист, раздаёт по форвардерам, пишет ответы обратно.</summary>
    internal sealed class TunEngine
    {
        const string Tag = "MLBB.Tun";

        readonly VpnService _svc;
        readonly ParcelFileDescriptor _pfd;
        readonly Java.IO.FileInputStream _in;
        readonly Java.IO.FileOutputStream _out;
        readonly object _writeLock = new object();

        readonly UdpForwarder _udp;
        readonly TcpForwarder _tcp;
        readonly IcmpForwarder _icmp;

        public readonly uint LocalIp;
        volatile bool _running;
        Thread _thread;
        Timer _janitor;

        public TunEngine(VpnService svc, ParcelFileDescriptor pfd, uint localIp)
        {
            _svc = svc; _pfd = pfd; LocalIp = localIp;
            _in = new Java.IO.FileInputStream(pfd.FileDescriptor);
            _out = new Java.IO.FileOutputStream(pfd.FileDescriptor);
            _udp = new UdpForwarder(this, svc);
            _tcp = new TcpForwarder(this, svc);
            _icmp = new IcmpForwarder(this, svc);
            StatsStore.Instance.BlockListChanged += OnBlockListChanged;
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(ReadLoop) { IsBackground = true, Name = "tun-reader" };
            _thread.Start();
            _janitor = new Timer(_ =>
            {
                try
                {
                    _udp.Cleanup(); _tcp.Cleanup(); _icmp.Cleanup();
                    StatsStore.Instance.ActiveFlows = _udp.Count + _tcp.Count;
                    StatsStore.Instance.ApplyPendingBlocks();
                    StatsStore.Instance.ScheduleGeoLookups();
                    StatsStore.Instance.SaveIfDirty();
                }
                catch (Exception ex) { Log.Warn(Tag, "janitor: " + ex.Message); }
            }, null, 5000, 5000);
        }

        public void Stop()
        {
            _running = false;
            StatsStore.Instance.BlockListChanged -= OnBlockListChanged;
            _janitor?.Dispose();
            try { _pfd.Close(); } catch { }
            _udp.Dispose(); _tcp.Dispose(); _icmp.Dispose();
            StatsStore.Instance.ActiveFlows = 0;
        }

        void OnBlockListChanged()
        {
            _udp.DropBlocked();
            _tcp.DropBlocked();
        }

        public void Write(byte[] pkt, int len)
        {
            if (!_running) return;
            lock (_writeLock)
            {
                try { _out.Write(pkt, 0, len); }
                catch (Exception ex) { Log.Debug(Tag, "write: " + ex.Message); }
            }
        }

        void ReadLoop()
        {
            var buf = new byte[4096];
            while (_running)
            {
                int n;
                try { n = _in.Read(buf, 0, buf.Length); }
                catch (Exception) { break; }
                if (n <= 0) continue;
                try { Dispatch(buf, n); }
                catch (Exception ex) { Log.Warn(Tag, "dispatch: " + ex.Message); }
            }
            Log.Info(Tag, "reader stopped");
        }

        void Dispatch(byte[] buf, int n)
        {
            if (!Ipv4Packet.TryParse(buf, n, out var ip)) return;

            if (StatsStore.Instance.IsBlocked(ip.Dst))
            {
                // «Мёртвый» сервер: UDP/ICMP — тишина, TCP — RST
                StatsStore.Instance.RecordBlockedDrop(ip.Dst);
                if (ip.Proto == 6 && AppSettings.RstBlocked) _tcp.RejectBlocked(ip);
                return;
            }

            switch (ip.Proto)
            {
                case 17: _udp.Handle(ip); break;
                case 6: _tcp.Handle(ip); break;
                case 1: _icmp.Handle(ip); break;
            }
        }
    }
}
