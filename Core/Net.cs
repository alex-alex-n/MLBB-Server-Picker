using System.Net;

namespace MlbbServerPicker.Core
{
    /// <summary>Байтовые утилиты, IPv4-адреса, контрольные суммы, форматирование.</summary>
    public static class Net
    {
        public static long NowMs => System.Environment.TickCount64;

        public static uint ReadU32(byte[] b, int o) =>
            ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

        public static ushort ReadU16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);

        public static void WriteU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        public static void WriteU16(byte[] b, int o, ushort v)
        {
            b[o] = (byte)(v >> 8); b[o + 1] = (byte)v;
        }

        public static string IpToString(uint ip) =>
            $"{ip >> 24}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";

        public static uint ParseIp(string s) => ReadU32(IPAddress.Parse(s).GetAddressBytes(), 0);

        public static byte[] ToBytes(uint ip) =>
            new[] { (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip };

        public static IPAddress ToIPAddress(uint ip) => new IPAddress(ToBytes(ip));

        public static uint FromBytes(byte[] a) => ReadU32(a, 0);

        public static bool IsPrivate(uint ip) =>
            (ip >> 24) == 10 || (ip >> 24) == 127 || (ip >> 20) == 0xAC1 ||
            (ip >> 16) == 0xC0A8 || (ip >> 22) == 0x191 || (ip >> 16) == 0xA9FE;

        public static ushort Checksum(byte[] buf, int off, int len, ulong sum = 0)
        {
            int i = off;
            while (len > 1) { sum += ReadU16(buf, i); i += 2; len -= 2; }
            if (len == 1) sum += (ulong)(buf[i] << 8);
            while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
            return (ushort)~sum;
        }

        public static ulong PseudoHeader(uint src, uint dst, byte proto, int len)
        {
            ulong s = 0;
            s += src >> 16; s += src & 0xFFFF;
            s += dst >> 16; s += dst & 0xFFFF;
            s += proto; s += (uint)len;
            return s;
        }

        public static string FormatBytes(long b)
        {
            if (b < 1024) return Loc.S(Resource.String.bytes_b, b.ToString());
            if (b < 1024 * 1024) return Loc.S(Resource.String.bytes_kb, (b / 1024.0).ToString("0.#"));
            if (b < 1024L * 1024 * 1024) return Loc.S(Resource.String.bytes_mb, (b / 1024.0 / 1024).ToString("0.##"));
            return Loc.S(Resource.String.bytes_gb, (b / 1024.0 / 1024 / 1024).ToString("0.##"));
        }

        public static string FormatAgo(System.DateTime t)
        {
            var d = System.DateTime.Now - t;
            if (d.TotalSeconds < 5) return Loc.S(Resource.String.ago_now);
            if (d.TotalSeconds < 60) return Loc.S(Resource.String.ago_sec, (int)d.TotalSeconds);
            if (d.TotalMinutes < 60) return Loc.S(Resource.String.ago_min, (int)d.TotalMinutes);
            if (d.TotalHours < 24) return Loc.S(Resource.String.ago_hour, (int)d.TotalHours);
            return t.ToString("dd.MM HH:mm");
        }
    }
}
