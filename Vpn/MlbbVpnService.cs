using System;
using System.Linq;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Util;
using AndroidX.Core.App;
using MlbbServerPicker.Core;

namespace MlbbServerPicker.Vpn
{
    [Service(Permission = "android.permission.BIND_VPN_SERVICE", Exported = true,
             ForegroundServiceType = ForegroundService.TypeSystemExempted)]
    [IntentFilter(new[] { "android.net.VpnService" })]
    public sealed class MlbbVpnService : VpnService
    {
        const string Tag = "MLBB.Vpn";
        public const string ActionStart = "com.mlbbtools.serverpicker.START";
        public const string ActionStop = "com.mlbbtools.serverpicker.STOP";
        const string ChannelId = "mlbb_vpn";
        const int NotifId = 1;
        const string LocalAddress = "10.77.0.2";

        public static volatile bool IsRunning;
        public static volatile string LastError;

        TunEngine _engine;
        Timer _notifTimer;

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            AppSettings.Load(this);
            Loc.Init(this);

            if (intent?.Action == ActionStop)
            {
                StopVpn();
                StopForeground(StopForegroundFlags.Remove);
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            StartAsForeground();
            if (_engine == null) StartVpn();
            return StartCommandResult.Sticky;
        }

        void StartAsForeground()
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var nm = (NotificationManager)GetSystemService(NotificationService);
                var ch = new NotificationChannel(ChannelId, Loc.S(Resource.String.notif_channel), NotificationImportance.Low)
                {
                    Description = Loc.S(Resource.String.notif_desc)
                };
                nm.CreateNotificationChannel(ch);
            }

            var n = BuildNotification(Loc.S(Resource.String.notif_starting));
            if (OperatingSystem.IsAndroidVersionAtLeast(34))
                StartForeground(NotifId, n, ForegroundService.TypeSystemExempted);
            else
                StartForeground(NotifId, n);
        }

        void StartVpn()
        {
            StatsStore.Instance.Init(this);
            try
            {
                var b = new VpnService.Builder(this);
                b.SetSession("MLBB Server Picker")
                 .SetMtu(1500)
                 .AddAddress(LocalAddress, 32)
                 .AddRoute("0.0.0.0", 0)
                 .SetBlocking(true);

                foreach (var dns in AppSettings.DnsList)
                {
                    try { b.AddDnsServer(dns); } catch (Exception ex) { Log.Warn(Tag, "dns " + dns + ": " + ex.Message); }
                }

                int added = 0;
                foreach (var pkg in AppSettings.PackageList)
                {
                    try { b.AddAllowedApplication(pkg); added++; }
                    catch (PackageManager.NameNotFoundException) { }
                }
                if (added == 0) throw new InvalidOperationException(Loc.S(Resource.String.vpn_err_not_found));

                var openApp = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)),
                    PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
                b.SetConfigureIntent(openApp);

                var pfd = b.Establish() ?? throw new InvalidOperationException(Loc.S(Resource.String.vpn_err_no_iface));

                _engine = new TunEngine(this, pfd, Net.ParseIp(LocalAddress));
                _engine.Start();

                IsRunning = true;
                LastError = null;
                _notifTimer = new Timer(_ => UpdateNotification(), null, 2000, 5000);
                Log.Info(Tag, "VPN started");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Error(Tag, ex.ToString());
                StopVpn();
                StopForeground(StopForegroundFlags.Remove);
                StopSelf();
            }
        }

        void StopVpn()
        {
            IsRunning = false;
            _notifTimer?.Dispose(); _notifTimer = null;
            _engine?.Stop(); _engine = null;
            StatsStore.Instance.SaveIfDirty();
        }

        public override void OnRevoke()
        {
            Log.Info(Tag, "VPN revoked by system");
            LastError = Loc.S(Resource.String.vpn_err_revoked);
            StopVpn();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }

        public override void OnDestroy()
        {
            StopVpn();
            base.OnDestroy();
        }

        void UpdateNotification()
        {
            try
            {
                var store = StatsStore.Instance;
                var all = store.Snapshot();
                int blocked = all.Count(s => s.Blocked);
                int game = all.Count(s => s.IsGame);
                int favs = all.Count(s => s.Favorite);
                var cur = store.CurrentMatchIp;
                string text;
                if (cur != null)
                {
                    var st = all.FirstOrDefault(x => x.Ip == cur);
                    string where = st?.GeoLine != null ? " · " + st.GeoLine : "";
                    text = Loc.S(Resource.String.notif_match, cur + where);
                }
                else text = Loc.S(Resource.String.counters_fmt, game, favs, blocked);
                var nm = (NotificationManager)GetSystemService(NotificationService);
                nm.Notify(NotifId, BuildNotification(text));
            }
            catch (Exception ex) { Log.Debug(Tag, "notif: " + ex.Message); }
        }

        Notification BuildNotification(string text)
        {
            var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)),
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            var stop = PendingIntent.GetService(this, 1,
                new Intent(this, typeof(MlbbVpnService)).SetAction(ActionStop),
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

            return new NotificationCompat.Builder(this, ChannelId)
                .SetSmallIcon(Resource.Drawable.ic_stat_shield)
                .SetContentTitle(Loc.S(Resource.String.notif_title))
                .SetContentText(text)
                .SetOngoing(true)
                .SetOnlyAlertOnce(true)
                .SetPriority(NotificationCompat.PriorityLow)
                .SetContentIntent(open)
                .AddAction(Resource.Drawable.ic_stop, Loc.S(Resource.String.btn_stop), stop)
                .Build();
        }
    }
}
