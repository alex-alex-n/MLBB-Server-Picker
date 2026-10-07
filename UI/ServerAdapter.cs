using System;
using System.Collections.Generic;
using Android.Content.Res;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Content;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using MlbbServerPicker.Core;

namespace MlbbServerPicker.UI
{
    public sealed class ServerAdapter : RecyclerView.Adapter
    {
        static readonly Java.Lang.Object Tick = new Java.Lang.String("tick");

        List<ServerStat> _items = new List<ServerStat>();
        string _currentMatchIp, _pingingIp;
        readonly HashSet<string> _expanded = new HashSet<string>();

        public Action<ServerStat> BlockClicked;
        public Action<ServerStat> KeepOnlyClicked;
        public Action<ServerStat> PingClicked;
        public Action<ServerStat, View> MoreClicked;

        public ServerAdapter() { HasStableIds = true; }

        public void Submit(List<ServerStat> items, string currentMatchIp, string pingingIp)
        {
            bool same = items.Count == _items.Count;
            if (same)
                for (int i = 0; i < items.Count; i++)
                    if (items[i].Ip != _items[i].Ip) { same = false; break; }

            _items = items;
            _currentMatchIp = currentMatchIp;
            _pingingIp = pingingIp;
            if (same) NotifyItemRangeChanged(0, items.Count, Tick);
            else NotifyDataSetChanged();
        }

        public void ToggleExpanded(string ip)
        {
            if (!_expanded.Remove(ip)) _expanded.Add(ip);
            int i = _items.FindIndex(x => x.Ip == ip);
            if (i >= 0) NotifyItemChanged(i, Tick);
        }

        public override int ItemCount => _items.Count;

        public override long GetItemId(int position)
        {
            try { return Net.ParseIp(_items[position].Ip); } catch { return _items[position].Ip.GetHashCode(); }
        }

        sealed class VH : RecyclerView.ViewHolder
        {
            public ServerStat Current;
            public readonly MaterialCardView Card;
            public readonly TextView Flag, Ip, Geo, Ping, Quality, BadgeRole, BadgeNow, BadgeFav, BadgeBlocked, Label, Meta, Details;
            public readonly MaterialButton BtnBlock, BtnKeep, BtnMore, BtnPing;

            public VH(View v) : base(v)
            {
                Card = (MaterialCardView)v;
                Flag = v.FindViewById<TextView>(Resource.Id.txtFlag);
                Ip = v.FindViewById<TextView>(Resource.Id.txtIp);
                Geo = v.FindViewById<TextView>(Resource.Id.txtGeo);
                Ping = v.FindViewById<TextView>(Resource.Id.txtPing);
                Quality = v.FindViewById<TextView>(Resource.Id.txtQuality);
                BadgeRole = v.FindViewById<TextView>(Resource.Id.badgeRole);
                BadgeNow = v.FindViewById<TextView>(Resource.Id.badgeNow);
                BadgeFav = v.FindViewById<TextView>(Resource.Id.badgeFav);
                BadgeBlocked = v.FindViewById<TextView>(Resource.Id.badgeBlocked);
                Label = v.FindViewById<TextView>(Resource.Id.txtLabel);
                Meta = v.FindViewById<TextView>(Resource.Id.txtMeta);
                Details = v.FindViewById<TextView>(Resource.Id.txtDetails);
                BtnBlock = v.FindViewById<MaterialButton>(Resource.Id.btnBlock);
                BtnKeep = v.FindViewById<MaterialButton>(Resource.Id.btnKeep);
                BtnMore = v.FindViewById<MaterialButton>(Resource.Id.btnMore);
                BtnPing = v.FindViewById<MaterialButton>(Resource.Id.btnPing);
            }
        }

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var v = LayoutInflater.From(parent.Context).Inflate(Resource.Layout.item_server, parent, false);
            var vh = new VH(v);
            vh.BtnBlock.Click += (s, e) => { if (vh.Current != null) BlockClicked?.Invoke(vh.Current); };
            vh.BtnKeep.Click += (s, e) => { if (vh.Current != null) KeepOnlyClicked?.Invoke(vh.Current); };
            vh.BtnPing.Click += (s, e) => { if (vh.Current != null) PingClicked?.Invoke(vh.Current); };
            vh.BtnMore.Click += (s, e) => { if (vh.Current != null) MoreClicked?.Invoke(vh.Current, vh.BtnMore); };
            vh.Card.Click += (s, e) => { if (vh.Current != null) ToggleExpanded(vh.Current.Ip); };
            return vh;
        }

        public static string QualityText(double ms) =>
            Loc.S(ms < 60 ? Resource.String.quality_excellent
                : ms < 100 ? Resource.String.quality_good
                : ms < 150 ? Resource.String.quality_meh
                : Resource.String.quality_bad);

        public static int QualityColor(double ms) =>
            ms < 60 ? Resource.Color.good : ms < 120 ? Resource.Color.warn : Resource.Color.bad;

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            var vh = (VH)holder;
            var s = _items[position];
            var ctx = vh.ItemView.Context;
            vh.Current = s;

            Color C(int res) => new Color(ContextCompat.GetColor(ctx, res));

            vh.Ip.Text = s.Ip;
            vh.Flag.Text = string.IsNullOrEmpty(s.Flag) ? "🌐" : s.Flag;

            // Провайдер / местоположение
            if (s.HasGeo) vh.Geo.Text = s.GeoLine;
            else if (s.IsGame || s.Favorite || s.Role == ServerRole.Lobby)
                vh.Geo.Text = Loc.S(s.GeoAttempts > 0 ? Resource.String.geo_failed : Resource.String.geo_resolving);
            else
                vh.Geo.Text = Loc.S(s.Role == ServerRole.Service ? Resource.String.geo_service
                                  : s.Role == ServerRole.Lobby ? Resource.String.geo_lobby : Resource.String.geo_unknown);

            // Пинг: приоритет — наш ICMP (если есть), под ним игровой RTT
            if (s.IcmpOk)
            {
                vh.Ping.Text = Loc.Ms(s.PingAvg);
                vh.Ping.SetTextColor(C(QualityColor(s.PingAvg)));
                vh.Quality.Text = s.HasRtt ? Loc.S(Resource.String.ping_in_game, Loc.Ms(s.RttAvg)) : QualityText(s.PingAvg);
            }
            else if (s.HasRtt)
            {
                vh.Ping.Text = Loc.Ms(s.RttAvg);
                vh.Ping.SetTextColor(C(QualityColor(s.RttAvg)));
                vh.Quality.Text = s.IcmpDead ? Loc.S(Resource.String.ping_game_icmp_dead)
                                             : Loc.S(Resource.String.ping_game_label, QualityText(s.RttAvg));
            }
            else
            {
                vh.Ping.Text = Loc.Ms(0).Replace("0", "—");
                vh.Ping.SetTextColor(C(Resource.Color.muted));
                vh.Quality.Text = s.IcmpDead ? Loc.S(Resource.String.ping_no_reply)
                                : s.IsGame ? Loc.S(Resource.String.ping_not_measured) : "";
            }

            // Роль
            int roleText, roleColor;
            switch (s.Role)
            {
                case ServerRole.Match: roleText = Resource.String.role_match; roleColor = Resource.Color.role_match; break;
                case ServerRole.Probe: roleText = Resource.String.role_probe; roleColor = Resource.Color.role_probe; break;
                case ServerRole.Lobby: roleText = Resource.String.role_lobby; roleColor = Resource.Color.role_lobby; break;
                case ServerRole.Service: roleText = Resource.String.role_service; roleColor = Resource.Color.role_service; break;
                default: roleText = Resource.String.role_other; roleColor = Resource.Color.role_unknown; break;
            }
            vh.BadgeRole.Text = Loc.S(roleText);
            vh.BadgeRole.BackgroundTintList = ColorStateList.ValueOf(C(roleColor));

            bool isNow = _currentMatchIp != null && _currentMatchIp == s.Ip;
            vh.BadgeNow.Visibility = isNow ? ViewStates.Visible : ViewStates.Gone;
            vh.BadgeFav.Visibility = s.Favorite ? ViewStates.Visible : ViewStates.Gone;

            if (s.Blocked) { vh.BadgeBlocked.Text = Loc.S(s.AutoBlocked ? Resource.String.badge_autoblock : Resource.String.badge_blocked); vh.BadgeBlocked.Visibility = ViewStates.Visible; }
            else if (s.BlockPending) { vh.BadgeBlocked.Text = Loc.S(Resource.String.badge_pending); vh.BadgeBlocked.Visibility = ViewStates.Visible; }
            else vh.BadgeBlocked.Visibility = ViewStates.Gone;

            vh.Label.Text = s.Label ?? "";
            vh.Label.Visibility = string.IsNullOrEmpty(s.Label) ? ViewStates.Gone : ViewStates.Visible;

            // Краткая строка
            var meta = new List<string>();
            if (s.IsGame) meta.Add(Loc.S(Resource.String.meta_matches, s.Matches));
            if (s.HasRtt) meta.Add(Loc.S(Resource.String.meta_samples, s.RttSamples));
            if (s.UdpPktsSent >= 50) meta.Add(Loc.S(Resource.String.meta_loss, s.UdpLoss.ToString("P0")));
            if (s.PingRounds > 0) meta.Add(Loc.S(Resource.String.meta_pinged, Net.FormatAgo(s.PingUpdated)));
            meta.Add(Net.FormatAgo(s.LastSeen));
            vh.Meta.Text = string.Join(" · ", meta);

            // Подробности
            bool expanded = _expanded.Contains(s.Ip);
            vh.Details.Visibility = expanded ? ViewStates.Visible : ViewStates.Gone;
            if (expanded)
            {
                string udp = s.UdpPorts.Count > 0 ? string.Join(", ", s.UdpPorts) : "—";
                string tcp = s.TcpPorts.Count > 0 ? string.Join(", ", s.TcpPorts) : "—";
                var lines = new List<string>
                {
                    Loc.S(Resource.String.det_ports, udp, tcp),
                    Loc.S(Resource.String.det_udp, Net.FormatBytes(s.UdpBytesSent), s.UdpPktsSent, Net.FormatBytes(s.UdpBytesRecv), s.UdpPktsRecv),
                    Loc.S(Resource.String.det_tcp, Net.FormatBytes(s.TcpBytesSent), Net.FormatBytes(s.TcpBytesRecv))
                };
                if (s.HasRtt) lines.Add(Loc.S(Resource.String.det_rtt, Loc.Ms(s.RttAvg), Loc.Ms(s.RttMin), Loc.Ms(s.RttLast)));
                if (s.PingRounds > 0)
                    lines.Add(Loc.S(Resource.String.det_ping,
                        s.PingAvg >= 0 ? Loc.Ms(s.PingAvg) : "—",
                        s.PingLast >= 0 ? Loc.Ms(s.PingLast) : "—",
                        s.PingMin >= 0 ? Loc.Ms(s.PingMin) : "—",
                        Loc.Ms(s.PingJitter), s.PingLoss.ToString("P0"), s.PingRounds));
                if (s.IcmpSent > 0) lines.Add(Loc.S(Resource.String.det_icmp, s.IcmpRecv, s.IcmpSent));
                if (s.HasGeo)
                {
                    string geo = Loc.S(Resource.String.det_geo, s.Provider + (string.IsNullOrEmpty(s.GeoAsn) ? "" : " (" + s.GeoAsn + ")"));
                    if (!string.IsNullOrEmpty(s.GeoOrg) && s.GeoOrg != s.GeoIsp) geo += " · " + s.GeoOrg;
                    if (!string.IsNullOrEmpty(s.GeoSource)) geo += " · " + Loc.S(Resource.String.det_source, s.GeoSource);
                    lines.Add(geo);
                }
                lines.Add(Loc.S(Resource.String.det_first, s.FirstSeen.ToString("dd.MM HH:mm"), s.BlockedDrops));
                vh.Details.Text = string.Join("\n", lines);
            }

            // Кнопки
            bool blocked = s.Blocked || s.BlockPending;
            vh.BtnBlock.Text = Loc.S(blocked ? Resource.String.btn_unblock_card : Resource.String.btn_block_card);
            vh.BtnBlock.SetIconResource(blocked ? Resource.Drawable.ic_check : Resource.Drawable.ic_block);
            vh.BtnBlock.IconSize = (int)(16 * ctx.Resources.DisplayMetrics.Density);

            vh.BtnKeep.Visibility = (s.IsGame || s.Favorite) ? ViewStates.Visible : ViewStates.Gone;
            vh.BtnKeep.Text = Loc.S(s.Favorite ? Resource.String.btn_fav_card : Resource.String.btn_keep_card);

            bool pingingThis = _pingingIp != null && _pingingIp == s.Ip;
            vh.BtnPing.Enabled = !pingingThis;
            vh.BtnPing.IconTint = ColorStateList.ValueOf(C(pingingThis ? Resource.Color.accent : Resource.Color.text_secondary));

            // Рамка
            int stroke = s.Blocked ? Resource.Color.bad
                       : isNow ? Resource.Color.good
                       : s.Favorite ? Resource.Color.favorite
                       : Resource.Color.card_stroke;
            vh.Card.StrokeColor = ContextCompat.GetColor(ctx, stroke);
            vh.Card.Alpha = s.Blocked ? 0.72f : 1f;
        }
    }
}
