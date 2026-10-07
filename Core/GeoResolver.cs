using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Android.Util;

namespace MlbbServerPicker.Core
{
    public sealed class GeoInfo
    {
        public string Country { get; set; } = "";
        public string CountryCode { get; set; } = "";
        public string City { get; set; } = "";
        public string Isp { get; set; } = "";
        public string Org { get; set; } = "";
        public string Asn { get; set; } = "";
        public string Source { get; set; } = "";

        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(Country) &&
            string.IsNullOrWhiteSpace(CountryCode) &&
            string.IsNullOrWhiteSpace(City) &&
            string.IsNullOrWhiteSpace(Isp) &&
            string.IsNullOrWhiteSpace(Org) &&
            string.IsNullOrWhiteSpace(Asn);
    }

    public sealed class GeoResolver
    {
        const string Tag = "MLBB.Geo";

        public static GeoResolver Instance { get; } = new GeoResolver();

        readonly ConcurrentQueue<uint> _queue = new ConcurrentQueue<uint>();
        readonly ConcurrentDictionary<uint, byte> _queued = new ConcurrentDictionary<uint, byte>();
        readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        int _workerRunning;

        GeoResolver() { }

        public void Enqueue(uint ip)
        {
            if (Net.IsPrivate(ip))
                return;

            if (!_queued.TryAdd(ip, 1))
                return;

            _queue.Enqueue(ip);
            EnsureWorker();
        }

        void EnsureWorker()
        {
            if (Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
                _ = Task.Run(WorkerAsync);
        }

        async Task WorkerAsync()
        {
            try
            {
                while (_queue.TryDequeue(out var ip))
                {
                    _queued.TryRemove(ip, out _);

                    GeoInfo info = null;

                    try
                    {
                        info = await ResolveAsync(ip).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(Tag, "resolve " + Net.IpToString(ip) + ": " + ex.Message);
                    }

                    StatsStore.Instance.ApplyGeo(ip, info);

                    // Не долбим API слишком часто
                    await Task.Delay(350).ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _workerRunning, 0);

                if (!_queue.IsEmpty)
                    EnsureWorker();
            }
        }

        async Task<GeoInfo> ResolveAsync(uint ip)
        {
            var ipText = Net.IpToString(ip);

            // ipwho.is без ключа, подходит для простого whois/geo.
            var url = "https://ipwho.is/" + ipText;

            using var resp = await _http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.False)
                return null;

            string GetString(string name)
            {
                if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString() ?? "";
                return "";
            }

            var g = new GeoInfo
            {
                Country = GetString("country"),
                CountryCode = GetString("country_code"),
                City = GetString("city"),
                Isp = GetString("isp"),
                Org = GetString("org"),
                Source = "ipwho.is"
            };

            if (root.TryGetProperty("connection", out var conn) &&
                conn.ValueKind == JsonValueKind.Object)
            {
                if (conn.TryGetProperty("asn", out var asn))
                {
                    if (asn.ValueKind == JsonValueKind.Number)
                        g.Asn = "AS" + asn.GetInt32();
                    else if (asn.ValueKind == JsonValueKind.String)
                        g.Asn = asn.GetString() ?? "";
                }

                if (string.IsNullOrWhiteSpace(g.Isp) &&
                    conn.TryGetProperty("isp", out var isp) &&
                    isp.ValueKind == JsonValueKind.String)
                    g.Isp = isp.GetString() ?? "";

                if (string.IsNullOrWhiteSpace(g.Org) &&
                    conn.TryGetProperty("org", out var org) &&
                    org.ValueKind == JsonValueKind.String)
                    g.Org = org.GetString() ?? "";
            }

            return g;
        }
    }
}
