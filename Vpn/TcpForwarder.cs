using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Android.Net;
using Android.Util;
using MlbbServerPicker.Core;

namespace MlbbServerPicker.Vpn
{
    /// <summary>
    /// Упрощённый userspace-TCP: терминируем соединение игры на себе и открываем реальный сокет к серверу.
    /// Без масштабирования окна/SACK/timestamps (не предлагаем их в SYN-ACK — клиент их отключает).
    /// </summary>
    internal sealed class TcpForwarder : IDisposable
    {
        readonly TunEngine _engine;
        readonly VpnService _svc;
        readonly ConcurrentDictionary<long, TcpConnection> _conns = new ConcurrentDictionary<long, TcpConnection>();

        public TcpForwarder(TunEngine engine, VpnService svc) { _engine = engine; _svc = svc; }

        public int Count => _conns.Count;

        static long FlowKey(ushort sport, uint dst, ushort dport) => ((long)sport << 48) | ((long)dport << 32) | dst;

        public void Handle(in Ipv4Packet ip)
        {
            var b = ip.Buf; int o = ip.PayloadOffset;
            if (ip.PayloadLen < 20) return;
            ushort sport = Net.ReadU16(b, o), dport = Net.ReadU16(b, o + 2);
            uint seq = Net.ReadU32(b, o + 4), ack = Net.ReadU32(b, o + 8);
            int doff = (b[o + 12] >> 4) * 4;
            var flags = (TcpFlags)b[o + 13];
            ushort win = Net.ReadU16(b, o + 14);
            if (doff < 20 || doff > ip.PayloadLen) return;
            int dataOff = o + doff, dataLen = ip.PayloadLen - doff;

            StatsStore.Instance.RecordTcpSent(ip.Dst, dport, ip.Len);
            long key = FlowKey(sport, ip.Dst, dport);

            if ((flags & TcpFlags.Syn) != 0 && (flags & TcpFlags.Ack) == 0)
            {
                if (_conns.ContainsKey(key)) return; // повторный SYN, пока коннектимся
                var c = new TcpConnection(this, _engine, _svc, key, ip.Src, sport, ip.Dst, dport, seq, win);
                if (_conns.TryAdd(key, c)) c.BeginConnect();
                return;
            }

            if (_conns.TryGetValue(key, out var conn))
            {
                conn.OnPacket(seq, ack, flags, win, b, dataOff, dataLen);
            }
            else if ((flags & TcpFlags.Rst) == 0)
            {
                uint rstSeq = (flags & TcpFlags.Ack) != 0 ? ack : 0;
                uint rstAck = seq + (uint)dataLen + ((flags & (TcpFlags.Syn | TcpFlags.Fin)) != 0 ? 1u : 0u);
                SendRst(ip.Src, sport, ip.Dst, dport, rstSeq, rstAck);
            }
        }

        /// <summary>Для заблокированных серверов: отвечаем RST, чтобы игра сразу сочла сервер недоступным.</summary>
        public void RejectBlocked(in Ipv4Packet ip)
        {
            var b = ip.Buf; int o = ip.PayloadOffset;
            if (ip.PayloadLen < 20) return;
            var flags = (TcpFlags)b[o + 13];
            if ((flags & TcpFlags.Rst) != 0) return;
            ushort sport = Net.ReadU16(b, o), dport = Net.ReadU16(b, o + 2);
            uint seq = Net.ReadU32(b, o + 4), ack = Net.ReadU32(b, o + 8);
            int doff = (b[o + 12] >> 4) * 4;
            int dataLen = Math.Max(0, ip.PayloadLen - doff);
            uint rstSeq = (flags & TcpFlags.Ack) != 0 ? ack : 0;
            uint rstAck = seq + (uint)dataLen + ((flags & (TcpFlags.Syn | TcpFlags.Fin)) != 0 ? 1u : 0u);
            SendRst(ip.Src, sport, ip.Dst, dport, rstSeq, rstAck);
        }

        void SendRst(uint clientIp, ushort clientPort, uint serverIp, ushort serverPort, uint seq, uint ack)
        {
            var pkt = Packets.BuildTcp(serverIp, serverPort, clientIp, clientPort, seq, ack,
                                       TcpFlags.Rst | TcpFlags.Ack, 0, null, 0, 0);
            _engine.Write(pkt, pkt.Length);
        }

        internal void Remove(long key) => _conns.TryRemove(key, out _);

        public void Cleanup()
        {
            long now = Net.NowMs;
            foreach (var c in _conns.Values)
                if (now - c.LastActive > 300_000) c.Abort();
        }

        public void DropBlocked()
        {
            var store = StatsStore.Instance;
            foreach (var c in _conns.Values)
                if (store.IsBlocked(c.ServerIp)) c.Abort();
        }

        public void Dispose()
        {
            foreach (var c in _conns.Values) c.Close();
            _conns.Clear();
        }
    }

    internal sealed class TcpConnection
    {
        const string Tag = "MLBB.Tcp";
        const int Mss = 1360;
        const ushort LocalWindow = 65535;

        enum TcpState { Connecting, SynAckSent, Established, CloseWait, LastAck, FinWait1, FinWait2, Closed }

        readonly TcpForwarder _fw;
        readonly TunEngine _engine;
        readonly VpnService _svc;
        readonly long _key;
        readonly uint _clientIp;
        readonly ushort _clientPort, _serverPort;
        public readonly uint ServerIp;

        readonly object _lock = new object();
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        readonly BlockingCollection<byte[]> _toRemote = new BlockingCollection<byte[]>();
        readonly SemaphoreSlim _windowSignal = new SemaphoreSlim(0, 1);

        Socket _sock;
        TcpState _state = TcpState.Connecting;
        uint _localSeq;      // следующий seq, который отправим клиенту
        uint _remoteSeq;     // следующий seq, который ждём от клиента
        uint _clientAck;     // последний ack клиента
        ushort _clientWindow;
        public long LastActive = Net.NowMs;

        public TcpConnection(TcpForwarder fw, TunEngine engine, VpnService svc, long key,
                             uint clientIp, ushort clientPort, uint serverIp, ushort serverPort,
                             uint clientSeq, ushort window)
        {
            _fw = fw; _engine = engine; _svc = svc; _key = key;
            _clientIp = clientIp; _clientPort = clientPort; ServerIp = serverIp; _serverPort = serverPort;
            _remoteSeq = clientSeq + 1;
            _localSeq = (uint)Random.Shared.Next(1, int.MaxValue);
            _clientAck = _localSeq;
            _clientWindow = window == 0 ? (ushort)65535 : window;
        }

        public void BeginConnect() => _ = ConnectAsync();

        async Task ConnectAsync()
        {
            try
            {
                _sock = new Socket(AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                _svc.Protect(_sock.Handle.ToInt32());
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(15_000);
                await _sock.ConnectAsync(new IPEndPoint(Net.ToIPAddress(ServerIp), _serverPort), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug(Tag, $"connect {Net.IpToString(ServerIp)}:{_serverPort}: {ex.Message}");
                Abort();
                return;
            }

            lock (_lock)
            {
                if (_state == TcpState.Closed) return;
                Send(TcpFlags.Syn | TcpFlags.Ack, null, 0, 0, Mss);
                _localSeq++;
                _state = TcpState.SynAckSent;
            }
            _ = Task.Factory.StartNew(SenderLoop, TaskCreationOptions.LongRunning);
            _ = ReceiveLoopAsync();
        }

        public void OnPacket(uint seq, uint ack, TcpFlags flags, ushort win, byte[] buf, int off, int len)
        {
            LastActive = Net.NowMs;
            lock (_lock)
            {
                if (_state == TcpState.Closed) return;
                if ((flags & TcpFlags.Rst) != 0) { CloseLocked(); return; }
                if (_state == TcpState.Connecting) return;

                if ((flags & TcpFlags.Ack) != 0)
                {
                    if (SeqLeq(_clientAck, ack) && SeqLeq(ack, _localSeq)) _clientAck = ack;
                    _clientWindow = win;
                    SignalWindow();
                    switch (_state)
                    {
                        case TcpState.SynAckSent: _state = TcpState.Established; break;
                        case TcpState.FinWait1: if (ack == _localSeq) _state = TcpState.FinWait2; break;
                        case TcpState.LastAck: if (ack == _localSeq) { CloseLocked(); return; } break;
                    }
                }

                if (len > 0)
                {
                    if (seq == _remoteSeq && !_toRemote.IsAddingCompleted)
                    {
                        var copy = new byte[len];
                        Buffer.BlockCopy(buf, off, copy, 0, len);
                        _remoteSeq += (uint)len;
                        _toRemote.Add(copy);
                    }
                    // in-order, повтор или вне порядка — всегда подтверждаем то, что имеем
                    Send(TcpFlags.Ack, null, 0, 0);
                }

                if ((flags & TcpFlags.Fin) != 0)
                {
                    uint finSeq = seq + (uint)len;
                    if (finSeq == _remoteSeq)
                    {
                        _remoteSeq++;
                        Send(TcpFlags.Ack, null, 0, 0);
                        switch (_state)
                        {
                            case TcpState.SynAckSent:
                            case TcpState.Established:
                                _state = TcpState.CloseWait;
                                try { _toRemote.CompleteAdding(); } catch { }
                                break;
                            case TcpState.FinWait1:
                            case TcpState.FinWait2:
                                CloseLocked();
                                break;
                        }
                    }
                    else if (finSeq + 1 == _remoteSeq)
                    {
                        Send(TcpFlags.Ack, null, 0, 0); // повторный FIN
                    }
                }
            }
        }

        void SenderLoop()
        {
            try
            {
                foreach (var chunk in _toRemote.GetConsumingEnumerable(_cts.Token))
                {
                    int sent = 0;
                    while (sent < chunk.Length)
                        sent += _sock.Send(chunk, sent, chunk.Length - sent, SocketFlags.None);
                }
                // клиент прислал FIN → полузакрываем сокет к серверу
                try { _sock?.Shutdown(SocketShutdown.Send); } catch { }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { Abort(); }
        }

        async Task ReceiveLoopAsync()
        {
            var buf = new byte[16384];
            try
            {
                while (true)
                {
                    int n = await _sock.ReceiveAsync(new Memory<byte>(buf), SocketFlags.None, _cts.Token).ConfigureAwait(false);
                    if (n <= 0) { OnRemoteClosed(); return; }
                    LastActive = Net.NowMs;

                    int off = 0;
                    while (off < n)
                    {
                        int chunk = await WaitWindowAsync(Math.Min(Mss, n - off)).ConfigureAwait(false);
                        if (chunk <= 0) return;
                        lock (_lock)
                        {
                            if (_state == TcpState.Closed) return;
                            Send(TcpFlags.Psh | TcpFlags.Ack, buf, off, chunk);
                            _localSeq += (uint)chunk;
                        }
                        off += chunk;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception) { Abort(); }
        }

        /// <summary>Ждём, пока клиент освободит окно приёма. Возвращает сколько байт можно отправить (0 = закрыто).</summary>
        async Task<int> WaitWindowAsync(int want)
        {
            while (true)
            {
                lock (_lock)
                {
                    if (_state == TcpState.Closed) return 0;
                    uint inFlight = _localSeq - _clientAck;
                    if (_clientWindow > inFlight)
                        return Math.Min(want, (int)(_clientWindow - inFlight));
                }
                try { await _windowSignal.WaitAsync(250, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return 0; }
            }
        }

        void SignalWindow()
        {
            if (_windowSignal.CurrentCount == 0)
            {
                try { _windowSignal.Release(); } catch (SemaphoreFullException) { }
            }
        }

        void OnRemoteClosed()
        {
            lock (_lock)
            {
                if (_state == TcpState.Closed) return;
                Send(TcpFlags.Fin | TcpFlags.Ack, null, 0, 0);
                _localSeq++;
                _state = _state == TcpState.CloseWait ? TcpState.LastAck : TcpState.FinWait1;
            }
            _ = Task.Delay(20_000).ContinueWith(_ => Close());
        }

        void Send(TcpFlags flags, byte[] data, int off, int len, ushort mss = 0)
        {
            var pkt = Packets.BuildTcp(ServerIp, _serverPort, _clientIp, _clientPort,
                                       _localSeq, _remoteSeq, flags, LocalWindow, data, off, len, mss);
            _engine.Write(pkt, pkt.Length);
            StatsStore.Instance.RecordTcpRecv(ServerIp, _serverPort, pkt.Length);
        }

        static bool SeqLeq(uint a, uint b) => (int)(b - a) >= 0;

        void CloseLocked()
        {
            if (_state == TcpState.Closed) return;
            _state = TcpState.Closed;
            try { _cts.Cancel(); } catch { }
            try { _toRemote.CompleteAdding(); } catch { }
            try { _sock?.Close(); } catch { }
            SignalWindow();
            _fw.Remove(_key);
        }

        public void Close() { lock (_lock) CloseLocked(); }

        public void Abort()
        {
            lock (_lock)
            {
                if (_state == TcpState.Closed) return;
                Send(TcpFlags.Rst | TcpFlags.Ack, null, 0, 0);
                CloseLocked();
            }
        }
    }
}
