using System;
using System.Threading;
using MlbbServerPicker.Core;

namespace MlbbServerPicker.Vpn
{
    [Flags]
    internal enum TcpFlags : byte { Fin = 1, Syn = 2, Rst = 4, Psh = 8, Ack = 16, Urg = 32 }

    /// <summary>Разобранный IPv4-пакет (ссылка на общий буфер — копировать перед асинхронным использованием!).</summary>
    internal readonly struct Ipv4Packet
    {
        public readonly byte[] Buf;
        public readonly int Len;
        public readonly int HeaderLen;
        public readonly byte Proto;
        public readonly uint Src;
        public readonly uint Dst;

        public int PayloadOffset => HeaderLen;
        public int PayloadLen => Len - HeaderLen;

        Ipv4Packet(byte[] buf, int len, int hl, byte proto, uint src, uint dst)
        {
            Buf = buf; Len = len; HeaderLen = hl; Proto = proto; Src = src; Dst = dst;
        }

        public static bool TryParse(byte[] buf, int len, out Ipv4Packet p)
        {
            p = default;
            if (len < 20 || (buf[0] >> 4) != 4) return false;
            int hl = (buf[0] & 0x0F) * 4;
            if (hl < 20 || hl > len) return false;
            int total = Net.ReadU16(buf, 2);
            if (total < hl) return false;
            if (total < len) len = total;
            if ((Net.ReadU16(buf, 6) & 0x1FFF) != 0) return false; // фрагменты не поддерживаем
            p = new Ipv4Packet(buf, len, hl, buf[9], Net.ReadU32(buf, 12), Net.ReadU32(buf, 16));
            return true;
        }
    }

    internal static class Packets
    {
        static int _ipId = Environment.TickCount;
        static ushort NextId() => (ushort)Interlocked.Increment(ref _ipId);

        public static void WriteIpv4Header(byte[] b, int o, byte proto, uint src, uint dst, int totalLen)
        {
            b[o] = 0x45; b[o + 1] = 0;
            Net.WriteU16(b, o + 2, (ushort)totalLen);
            Net.WriteU16(b, o + 4, NextId());
            Net.WriteU16(b, o + 6, 0x4000); // DF
            b[o + 8] = 64; b[o + 9] = proto;
            b[o + 10] = 0; b[o + 11] = 0;
            Net.WriteU32(b, o + 12, src);
            Net.WriteU32(b, o + 16, dst);
            Net.WriteU16(b, o + 10, Net.Checksum(b, o, 20));
        }

        public static byte[] BuildUdp(uint src, ushort sport, uint dst, ushort dport, byte[] data, int off, int len)
        {
            var pkt = new byte[28 + len];
            WriteIpv4Header(pkt, 0, 17, src, dst, pkt.Length);
            Net.WriteU16(pkt, 20, sport);
            Net.WriteU16(pkt, 22, dport);
            Net.WriteU16(pkt, 24, (ushort)(8 + len));
            Net.WriteU16(pkt, 26, 0);
            if (len > 0) Buffer.BlockCopy(data, off, pkt, 28, len);
            ushort cs = Net.Checksum(pkt, 20, 8 + len, Net.PseudoHeader(src, dst, 17, 8 + len));
            if (cs == 0) cs = 0xFFFF;
            Net.WriteU16(pkt, 26, cs);
            return pkt;
        }

        public static byte[] BuildTcp(uint src, ushort sport, uint dst, ushort dport, uint seq, uint ack,
                                      TcpFlags flags, ushort window, byte[] data, int off, int len, ushort mss = 0)
        {
            int optLen = mss > 0 ? 4 : 0;
            int hdr = 20 + optLen;
            var pkt = new byte[20 + hdr + len];
            WriteIpv4Header(pkt, 0, 6, src, dst, pkt.Length);
            int t = 20;
            Net.WriteU16(pkt, t, sport);
            Net.WriteU16(pkt, t + 2, dport);
            Net.WriteU32(pkt, t + 4, seq);
            Net.WriteU32(pkt, t + 8, ack);
            pkt[t + 12] = (byte)((hdr / 4) << 4);
            pkt[t + 13] = (byte)flags;
            Net.WriteU16(pkt, t + 14, window);
            Net.WriteU16(pkt, t + 16, 0);
            Net.WriteU16(pkt, t + 18, 0);
            if (mss > 0) { pkt[t + 20] = 2; pkt[t + 21] = 4; Net.WriteU16(pkt, t + 22, mss); }
            if (len > 0) Buffer.BlockCopy(data, off, pkt, 20 + hdr, len);
            Net.WriteU16(pkt, t + 16, Net.Checksum(pkt, 20, hdr + len, Net.PseudoHeader(src, dst, 6, hdr + len)));
            return pkt;
        }

        public static byte[] BuildIcmp(uint src, uint dst, byte[] icmp, int off, int len)
        {
            var pkt = new byte[20 + len];
            WriteIpv4Header(pkt, 0, 1, src, dst, pkt.Length);
            Buffer.BlockCopy(icmp, off, pkt, 20, len);
            return pkt;
        }
    }
}
