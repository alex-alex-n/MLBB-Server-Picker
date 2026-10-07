using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.Net;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using AndroidX.Core.View;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.Navigation;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.Snackbar;
using MlbbServerPicker.Core;
using MlbbServerPicker.UI;
using MlbbServerPicker.Vpn;

namespace MlbbServerPicker
{
    [Activity(Label = "@string/app_name", MainLauncher = true, Exported = true,
              Theme = "@style/Theme.MlbbPicker", LaunchMode = LaunchMode.SingleTop,
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.UiMode)]
    public partial class MainActivity : AppCompatActivity, NavigationBarView.IOnItemSelectedListener
    {
        const int ReqVpn = 1001, ReqExport = 1002, ReqNotif = 1003;
        public const string DonateUrl = "https://www.donationalerts.com/r/levoe123";

        enum Filter { Game, Active, Blocked, Other }
        enum Tab { Status, Favorites, Servers }

        Filter _filter = Filter.Game;
        Tab _tab = Tab.Status;

        View _root, _tabStatus, _tabFavs, _tabServers, _dot, _boxBest, _rowStrict, _barDonate;
        BottomNavigationView _nav;
        MaterialButton _btnToggle, _btnKeepBest, _btnUnblockAll, _btnDonate, _btnHelp,
                       _btnPingAll, _btnSort, _btnPingFavs, _btnFavBlockOthers;
        TextView _txtStatus, _txtCurrent, _txtCounters, _txtBestTitle, _txtBest, _txtBestGeo,
                 _txtEmpty, _txtEmptyFavs, _txtPingStatus, _txtFavStatus;
        LinearProgressIndicator _progPing, _progFav;
        MaterialSwitch _swStrict, _swAutoPing;
        Chip _chipGame, _chipActive, _chipBlocked, _chipOther;
        RecyclerView _list, _listFavs;
        ServerAdapter _adapter, _favAdapter;
        Handler _ui;
        bool _ticking, _syncing;
        ServerStat _best;
        bool _bestIsFavMode;
        long _lastAutoPing;

        // ---------------- lifecycle ----------------
        protected override void AttachBaseContext(Context @base)
        {
            AppSettings.Load(@base);
            Loc.Init(@base);
            base.AttachBaseContext(Loc.Wrap(@base));
        }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            WindowCompat.SetDecorFitsSystemWindows(Window, false);
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                Window.NavigationBarContrastEnforced = false;
                Window.StatusBarContrastEnforced = false;
            }

            StatsStore.Instance.Init(this);
            _ui = new Handler(Looper.MainLooper);

            SetContentView(Resource.Layout.activity_main);
            _root = FindViewById(Resource.Id.root);
            SetupInsets();
            SetSupportActionBar(FindViewById<MaterialToolbar>(Resource.Id.toolbar));

            // --- вкладки ---
            _tabStatus = FindViewById(Resource.Id.tabStatus);
            _tabFavs = FindViewById(Resource.Id.tabFavorites);
            _tabServers = FindViewById(Resource.Id.tabServers);
            _nav = FindViewById<BottomNavigationView>(Resource.Id.bottomNav);
            _nav.SetOnItemSelectedListener(this);

            _barDonate = FindViewById(Resource.Id.barDonate);
            _barDonate.Click += (s, e) => OpenDonate();

            // --- статус ---
            _dot = FindViewById(Resource.Id.dotStatus);
            _txtStatus = FindViewById<TextView>(Resource.Id.txtStatus);
            _txtCurrent = FindViewById<TextView>(Resource.Id.txtCurrent);
            _txtCounters = FindViewById<TextView>(Resource.Id.txtCounters);
            _txtBestTitle = FindViewById<TextView>(Resource.Id.txtBestTitle);
            _txtBest = FindViewById<TextView>(Resource.Id.txtBest);
            _txtBestGeo = FindViewById<TextView>(Resource.Id.txtBestGeo);
            _boxBest = FindViewById(Resource.Id.boxBest);
            _rowStrict = FindViewById(Resource.Id.rowStrict);
            _swStrict = FindViewById<MaterialSwitch>(Resource.Id.swStrict);
            _btnToggle = FindViewById<MaterialButton>(Resource.Id.btnToggle);
            _btnKeepBest = FindViewById<MaterialButton>(Resource.Id.btnKeepBest);
            _btnUnblockAll = FindViewById<MaterialButton>(Resource.Id.btnUnblockAll);
            _btnDonate = FindViewById<MaterialButton>(Resource.Id.btnDonate);
            _btnHelp = FindViewById<MaterialButton>(Resource.Id.btnHelp);

            _btnToggle.Click += (s, e) => ToggleVpn();
            _btnKeepBest.Click += (s, e) => OnBestButton();
            _btnUnblockAll.Click += (s, e) => ConfirmUnblockAll();
            _btnDonate.Click += (s, e) => OpenDonate();
            _btnHelp.Click += (s, e) => ShowHelp();
            _swStrict.CheckedChange += (s, e) =>
            {
                if (_syncing) return;
                AppSettings.StrictMode = e.IsChecked;
                AppSettings.Save(this);
                Snack(S(e.IsChecked ? Resource.String.snack_strict_on : Resource.String.snack_strict_off));
            };

            // --- избранное ---
            _listFavs = FindViewById<RecyclerView>(Resource.Id.listFavs);
            _txtEmptyFavs = FindViewById<TextView>(Resource.Id.txtEmptyFavs);
            _txtFavStatus = FindViewById<TextView>(Resource.Id.txtFavStatus);
            _progFav = FindViewById<LinearProgressIndicator>(Resource.Id.progFav);
            _swAutoPing = FindViewById<MaterialSwitch>(Resource.Id.swAutoPing);
            _btnPingFavs = FindViewById<MaterialButton>(Resource.Id.btnPingFavs);
            _btnFavBlockOthers = FindViewById<MaterialButton>(Resource.Id.btnFavBlockOthers);
            _favAdapter = MakeAdapter();
            _listFavs.SetLayoutManager(new LinearLayoutManager(this));
            _listFavs.SetAdapter(_favAdapter);
            if (_listFavs.GetItemAnimator() is SimpleItemAnimator a1) a1.SupportsChangeAnimations = false;
            _btnPingFavs.Click += (s, e) => OnPingFavorites();
            _btnFavBlockOthers.Click += (s, e) => OnBestButton();
            _swAutoPing.CheckedChange += (s, e) =>
            {
                if (_syncing) return;
                AppSettings.AutoPing = e.IsChecked;
                AppSettings.Save(this);
                _lastAutoPing = 0;
                Refresh();
            };

            // --- серверы ---
            _list = FindViewById<RecyclerView>(Resource.Id.list);
            _txtEmpty = FindViewById<TextView>(Resource.Id.txtEmpty);
            _txtPingStatus = FindViewById<TextView>(Resource.Id.txtPingStatus);
            _progPing = FindViewById<LinearProgressIndicator>(Resource.Id.progPing);
            _btnPingAll = FindViewById<MaterialButton>(Resource.Id.btnPingAll);
            _btnSort = FindViewById<MaterialButton>(Resource.Id.btnSort);
            _adapter = MakeAdapter();
            _list.SetLayoutManager(new LinearLayoutManager(this));
            _list.SetAdapter(_adapter);
            if (_list.GetItemAnimator() is SimpleItemAnimator a2) a2.SupportsChangeAnimations = false;
            _btnPingAll.Click += (s, e) => OnPingAll();
            _btnSort.Click += (s, e) => ShowSortMenu(_btnSort);

            _chipGame = FindViewById<Chip>(Resource.Id.chipGame);
            _chipActive = FindViewById<Chip>(Resource.Id.chipActive);
            _chipBlocked = FindViewById<Chip>(Resource.Id.chipBlocked);
            _chipOther = FindViewById<Chip>(Resource.Id.chipOther);
            _chipGame.Click += (s, e) => { _filter = Filter.Game; Refresh(); };
            _chipActive.Click += (s, e) => { _filter = Filter.Active; Refresh(); };
            _chipBlocked.Click += (s, e) => { _filter = Filter.Blocked; Refresh(); };
            _chipOther.Click += (s, e) => { _filter = Filter.Other; Refresh(); };
            FindViewById<ChipGroup>(Resource.Id.chips).Check(Resource.Id.chipGame);

            // восстановление вкладки (например, после смены языка)
            var tab = (Tab)(savedInstanceState?.GetInt("tab", 0) ?? 0);
            _nav.SelectedItemId = tab == Tab.Favorites ? Resource.Id.nav_favorites
                                : tab == Tab.Servers ? Resource.Id.nav_servers : Resource.Id.nav_status;
            SwitchTab(tab);

            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                const string PostNotifications = "android.permission.POST_NOTIFICATIONS";
                if (ContextCompat.CheckSelfPermission(this, PostNotifications) != Permission.Granted)
                    ActivityCompat.RequestPermissions(this, new[] { PostNotifications }, ReqNotif);
            }

            if (!IsMlbbInstalled()) ShowNotInstalled();
            if (!AppSettings.HelpShown) { AppSettings.HelpShown = true; AppSettings.Save(this); ShowHelp(); }
        }

        ServerAdapter MakeAdapter() => new ServerAdapter
        {
            BlockClicked = OnBlockClicked,
            KeepOnlyClicked = ShowKeepOnly,
            MoreClicked = OnMoreClicked,
            PingClicked = OnPingOne
        };

        protected override void OnSaveInstanceState(Bundle outState)
        {
            base.OnSaveInstanceState(outState);
            outState.PutInt("tab", (int)_tab);
        }

        void SetupInsets()
        {
            var ctl = new WindowInsetsControllerCompat(Window, _root);
            ctl.AppearanceLightStatusBars = false;
            ctl.AppearanceLightNavigationBars = false;
            ViewCompat.SetOnApplyWindowInsetsListener(_root, new InsetsListener(ins =>
                _root.SetPadding(ins.Left, ins.Top, ins.Right, ins.Bottom)));
            ViewCompat.RequestApplyInsets(_root);
        }

        sealed class InsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
        {
            readonly Action<AndroidX.Core.Graphics.Insets> _apply;
            public InsetsListener(Action<AndroidX.Core.Graphics.Insets> apply) { _apply = apply; }
            public WindowInsetsCompat OnApplyWindowInsets(View v, WindowInsetsCompat insets)
            {
                _apply(insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout()));
                return WindowInsetsCompat.Consumed;
            }
        }

        protected override void OnResume()
        {
            base.OnResume();
            _ticking = true;
            Pinger.Instance.Completed += OnPingCompleted;
            Pinger.Instance.BatchFinished += PostRefresh;
            StatsStore.Instance.ScheduleGeoLookups();
            Tick();
        }

        protected override void OnPause()
        {
            base.OnPause();
            _ticking = false;
            Pinger.Instance.Completed -= OnPingCompleted;
            Pinger.Instance.BatchFinished -= PostRefresh;
            StatsStore.Instance.SaveIfDirty();
        }

        void OnPingCompleted(PingResult _) => PostRefresh();
        void PostRefresh() => _ui.Post(() => { if (_ticking) Refresh(); });

        void Tick()
        {
            if (!_ticking) return;
            Refresh();
            AutoPingTick();
            _ui.PostDelayed(Tick, 1000);
        }

        /// <summary>Авто-пинг избранного: только пока открыта вкладка, по одному серверу, с интервалом.</summary>
        void AutoPingTick()
        {
            if (_tab != Tab.Favorites || !AppSettings.AutoPing) return;
            var p = Pinger.Instance;
            if (p.IsBusy) return;
            long now = Net.NowMs;
            if (now - _lastAutoPing < AppSettings.AutoPingInterval * 1000L) return;
            var favs = StatsStore.Instance.Snapshot().Where(x => x.Favorite).Select(x => x.Ip).ToList();
            if (favs.Count == 0) return;
            if (!p.CheckAvailable()) { _lastAutoPing = now; return; }
            _lastAutoPing = now;
            p.Enqueue(favs, false);
        }

        // ---------------- вкладки ----------------
        public bool OnNavigationItemSelected(IMenuItem item)
        {
            if (item.ItemId == Resource.Id.nav_favorites) SwitchTab(Tab.Favorites);
            else if (item.ItemId == Resource.Id.nav_servers) SwitchTab(Tab.Servers);
            else SwitchTab(Tab.Status);
            return true;
        }

        void SwitchTab(Tab t)
        {
            _tab = t;
            _tabStatus.Visibility = t == Tab.Status ? ViewStates.Visible : ViewStates.Gone;
            _tabFavs.Visibility = t == Tab.Favorites ? ViewStates.Visible : ViewStates.Gone;
            _tabServers.Visibility = t == Tab.Servers ? ViewStates.Visible : ViewStates.Gone;
            if (t == Tab.Favorites) _lastAutoPing = 0; // сразу пингуем при открытии
            Refresh();
        }

        // ---------------- UI refresh ----------------
        Color Col(int res) => new Color(ContextCompat.GetColor(this, res));
        static void SetText(TextView tv, string text) { if (tv.Text != text) tv.Text = text; }
        string S(int id, params object[] args) => Loc.S(id, args);

        static ServerStat PickBest(List<ServerStat> all) =>
            all.Where(x => x.IsGame && !x.Blocked && !x.BlockPending && (x.RttSamples >= 3 || x.IcmpOk))
               .OrderBy(x => x.BestMs).FirstOrDefault();

        void Refresh()
        {
            var store = StatsStore.Instance;
            bool running = MlbbVpnService.IsRunning;
            var all = store.Snapshot();
            var cur = store.CurrentMatchIp;
            int favs = all.Count(x => x.Favorite);

            RefreshStatus(all, running, cur, favs);
            RefreshBadges(running, cur, favs);
            if (_tab == Tab.Favorites) RefreshFavorites(all, cur);
            else if (_tab == Tab.Servers) RefreshServers(all, running, cur);
        }

        void RefreshStatus(List<ServerStat> all, bool running, string cur, int favs)
        {
            _dot.BackgroundTintList = ColorStateList.ValueOf(Col(running ? Resource.Color.good : Resource.Color.muted));
            string status = S(running ? Resource.String.status_on : Resource.String.status_off);
            if (!running && MlbbVpnService.LastError != null) status = S(Resource.String.status_error, MlbbVpnService.LastError);
            SetText(_txtStatus, status);

            if (cur != null)
            {
                var st = all.FirstOrDefault(x => x.Ip == cur);
                string where = st?.GeoLine != null ? " · " + st.GeoLine : (!string.IsNullOrEmpty(st?.Label) ? $" ({st.Label})" : "");
                string ping = st != null && st.HasRtt ? Loc.Ms(st.RttAvg) : S(Resource.String.ping_none);
                SetText(_txtCurrent, S(Resource.String.match_now, cur, where, ping));
                _txtCurrent.SetTextColor(Col(Resource.Color.good));
            }
            else
            {
                SetText(_txtCurrent, S(running ? Resource.String.hint_running : Resource.String.hint_stopped));
                _txtCurrent.SetTextColor(Col(Resource.Color.text_secondary));
            }

            int blocked = all.Count(x => x.Blocked), pending = all.Count(x => x.BlockPending && !x.Blocked);
            int game = all.Count(x => x.IsGame);
            int other = all.Count(x => !x.IsGame && !x.Favorite);
            var counters = new StringBuilder(S(Resource.String.counters_fmt, game, favs, blocked));
            if (pending > 0) counters.Append(" (").Append(S(Resource.String.counters_pending, pending)).Append(')');
            counters.Append(" · ").Append(S(Resource.String.counters_other, other));
            if (running) counters.Append(" · ").Append(S(Resource.String.counters_flows, StatsStore.Instance.ActiveFlows));
            SetText(_txtCounters, counters.ToString());

            // ---- блок «лучший сервер / избранное» ----
            if (favs > 0)
            {
                var favList = all.Where(x => x.Favorite).OrderBy(x => x.BestMs).ToList();
                int othersOpen = all.Count(x => x.IsGame && !x.Favorite && !x.Blocked && !x.BlockPending);
                _bestIsFavMode = true; _best = favList[0];
                SetText(_txtBestTitle, favs == 1 ? S(Resource.String.best_title_fav_one) : S(Resource.String.best_title_fav_many, favs));
                SetText(_txtBest, string.Join("\n", favList.Select(x => $"{x.Ip} · {(x.BestMs < 9999 ? Loc.Ms(x.BestMs) : S(Resource.String.ping_none))}")));
                SetText(_txtBestGeo, favs == 1
                    ? (favList[0].GeoLine ?? S(Resource.String.geo_pending))
                    : S(AppSettings.StrictMode ? Resource.String.best_fav_strict_on : Resource.String.best_fav_strict_off));
                SetText(_btnKeepBest, othersOpen > 0 ? S(Resource.String.btn_block_others, othersOpen) : S(Resource.String.btn_reset_favs));
                _btnKeepBest.SetIconResource(othersOpen > 0 ? Resource.Drawable.ic_block : Resource.Drawable.ic_check);
                _boxBest.Visibility = ViewStates.Visible;
            }
            else
            {
                _bestIsFavMode = false;
                _best = PickBest(all);
                if (_best != null)
                {
                    SetText(_txtBestTitle, S(Resource.String.best_title_ping));
                    SetText(_txtBest, $"{_best.Ip} · {Loc.Ms(_best.BestMs)} · {ServerAdapter.QualityText(_best.BestMs)}");
                    SetText(_txtBestGeo, (string.IsNullOrEmpty(_best.Flag) ? "" : _best.Flag + " ") + (_best.GeoLine ?? S(Resource.String.geo_pending)));
                    SetText(_btnKeepBest, S(Resource.String.btn_keep_only_this));
                    _btnKeepBest.SetIconResource(Resource.Drawable.ic_star);
                    _boxBest.Visibility = ViewStates.Visible;
                }
                else _boxBest.Visibility = ViewStates.Gone;
            }

            _rowStrict.Visibility = favs > 0 ? ViewStates.Visible : ViewStates.Gone;
            if (_swStrict.Checked != AppSettings.StrictMode) { _syncing = true; _swStrict.Checked = AppSettings.StrictMode; _syncing = false; }
            _btnUnblockAll.Visibility = (blocked + pending) > 0 ? ViewStates.Visible : ViewStates.Gone;

            SetText(_btnToggle, S(running ? Resource.String.btn_stop : Resource.String.btn_start));
            _btnToggle.SetIconResource(running ? Resource.Drawable.ic_stop : Resource.Drawable.ic_play);
        }

        void RefreshBadges(bool running, string cur, int favs)
        {
            if (running)
            {
                var b = _nav.GetOrCreateBadge(Resource.Id.nav_status);
                b.BackgroundColor = ContextCompat.GetColor(this, cur != null ? Resource.Color.good : Resource.Color.accent);
                b.ClearNumber();
            }
            else _nav.RemoveBadge(Resource.Id.nav_status);

            if (favs > 0)
            {
                var b = _nav.GetOrCreateBadge(Resource.Id.nav_favorites);
                b.BackgroundColor = ContextCompat.GetColor(this, Resource.Color.favorite);
                b.BadgeTextColor = ContextCompat.GetColor(this, Resource.Color.bg);
                b.Number = favs;
            }
            else _nav.RemoveBadge(Resource.Id.nav_favorites);
        }

        void RefreshFavorites(List<ServerStat> all, string cur)
        {
            var p = Pinger.Instance;
            var favs = all.Where(x => x.Favorite).OrderBy(x => x.BestMs).ThenByDescending(x => x.LastSeen).ToList();
            _favAdapter.Submit(favs, cur, p.Current);
            _txtEmptyFavs.Visibility = favs.Count == 0 ? ViewStates.Visible : ViewStates.Gone;

            int othersOpen = all.Count(x => x.IsGame && !x.Favorite && !x.Blocked && !x.BlockPending);
            if (favs.Count > 0 && othersOpen > 0)
            {
                _btnFavBlockOthers.Visibility = ViewStates.Visible;
                SetText(_btnFavBlockOthers, S(Resource.String.btn_block_others, othersOpen));
            }
            else _btnFavBlockOthers.Visibility = ViewStates.Gone;

            SetText(_swAutoPing, S(Resource.String.autoping_label, AppSettings.AutoPingInterval));
            if (_swAutoPing.Checked != AppSettings.AutoPing) { _syncing = true; _swAutoPing.Checked = AppSettings.AutoPing; _syncing = false; }

            if (p.IsBusy)
            {
                SetText(_txtFavStatus, S(Resource.String.ping_progress, p.Current ?? "…", p.Done, p.Total));
                _progFav.Visibility = ViewStates.Visible;
                _progFav.Max = Math.Max(1, p.Total);
                _progFav.SetProgressCompat(p.Done, true);
                _btnPingFavs.SetIconResource(Resource.Drawable.ic_stop);
            }
            else
            {
                _progFav.Visibility = ViewStates.Gone;
                _btnPingFavs.SetIconResource(Resource.Drawable.ic_refresh);
                var last = favs.Where(x => x.PingRounds > 0).Select(x => x.PingUpdated).DefaultIfEmpty(DateTime.MinValue).Max();
                string txt = !AppSettings.AutoPing ? S(Resource.String.fav_autoping_off)
                           : last == DateTime.MinValue ? S(Resource.String.fav_hint)
                           : S(Resource.String.fav_last_ping, Net.FormatAgo(last));
                SetText(_txtFavStatus, txt);
            }
        }

        void RefreshServers(List<ServerStat> all, bool running, string cur)
        {
            var p = Pinger.Instance;
            int blocked = all.Count(x => x.Blocked), pending = all.Count(x => x.BlockPending && !x.Blocked);
            int game = all.Count(x => x.IsGame), other = all.Count(x => !x.IsGame && !x.Favorite);
            int active = all.Count(x => (x.IsGame || x.Favorite) && (DateTime.Now - x.LastSeen).TotalSeconds < 120);

            SetText(_chipGame, ChipText(Resource.String.chip_game, game));
            SetText(_chipActive, ChipText(Resource.String.chip_active, active));
            SetText(_chipBlocked, ChipText(Resource.String.chip_blocked, blocked + pending));
            SetText(_chipOther, ChipText(Resource.String.chip_other, other));

            var items = SortItems(all.Where(Matches), cur).ToList();
            _adapter.Submit(items, cur, p.Current);

            if (items.Count == 0)
            {
                int id;
                switch (_filter)
                {
                    case Filter.Active: id = Resource.String.empty_active; break;
                    case Filter.Blocked: id = Resource.String.empty_blocked; break;
                    case Filter.Other: id = Resource.String.empty_other; break;
                    default: id = running ? Resource.String.empty_game_running : Resource.String.empty_game_stopped; break;
                }
                SetText(_txtEmpty, S(id));
                _txtEmpty.Visibility = ViewStates.Visible;
            }
            else _txtEmpty.Visibility = ViewStates.Gone;

            if (p.IsBusy)
            {
                SetText(_txtPingStatus, S(Resource.String.ping_progress, p.Current ?? "…", p.Done, p.Total));
                _txtPingStatus.Visibility = ViewStates.Visible;
                _progPing.Visibility = ViewStates.Visible;
                _progPing.Max = Math.Max(1, p.Total);
                _progPing.SetProgressCompat(p.Done, true);
                _btnPingAll.SetIconResource(Resource.Drawable.ic_stop);
            }
            else
            {
                _txtPingStatus.Visibility = ViewStates.Gone;
                _progPing.Visibility = ViewStates.Gone;
                _btnPingAll.SetIconResource(Resource.Drawable.ic_ping);
            }
        }

        string ChipText(int id, int n) => n > 0 ? S(Resource.String.chip_fmt, S(id), n) : S(id);

        IEnumerable<ServerStat> SortItems(IEnumerable<ServerStat> src, string cur)
        {
            var q = src.OrderByDescending(x => x.Ip == cur).ThenBy(x => x.Blocked ? 2 : x.BlockPending ? 1 : 0);
            switch (AppSettings.SortMode)
            {
                case 1: return q.ThenBy(x => x.GameMs).ThenByDescending(x => x.LastSeen);
                case 2: return q.ThenByDescending(x => x.LastSeen);
                default: return q.ThenBy(x => x.BestMs).ThenByDescending(x => x.LastSeen);
            }
        }

        bool Matches(ServerStat s)
        {
            switch (_filter)
            {
                case Filter.Active: return (s.IsGame || s.Favorite) && (DateTime.Now - s.LastSeen).TotalSeconds < 120;
                case Filter.Blocked: return s.Blocked || s.BlockPending;
                case Filter.Other: return !s.IsGame && !s.Favorite;
                default: return s.IsGame || s.Favorite;
            }
        }

        // ---------------- пинг ----------------
        void OnPingAll()
        {
            var p = Pinger.Instance;
            if (p.IsBusy) { p.Cancel(); Snack(S(Resource.String.ping_cancelled)); Refresh(); return; }
            if (!p.CheckAvailable()) { Snack(S(Resource.String.ping_unavailable)); return; }
            var ips = StatsStore.Instance.Snapshot().Where(Matches).Select(x => x.Ip).ToList();
            if (ips.Count == 0) { Snack(S(Resource.String.ping_nothing)); return; }
            int n = p.Enqueue(ips, true);
            Snack(S(Resource.String.ping_queued, n));
            Refresh();
        }

        void OnPingFavorites()
        {
            var p = Pinger.Instance;
            if (p.IsBusy) { p.Cancel(); Refresh(); return; }
            if (!p.CheckAvailable()) { Snack(S(Resource.String.ping_unavailable)); return; }
            var ips = StatsStore.Instance.Snapshot().Where(x => x.Favorite).Select(x => x.Ip).ToList();
            if (ips.Count == 0) { Snack(S(Resource.String.ping_nothing)); return; }
            _lastAutoPing = Net.NowMs;
            p.Enqueue(ips, true);
            Refresh();
        }

        void OnPingOne(ServerStat s)
        {
            var p = Pinger.Instance;
            if (!p.CheckAvailable()) { Snack(S(Resource.String.ping_unavailable)); return; }
            p.Enqueue(new[] { s.Ip }, true);
            Refresh();
        }

        void ShowSortMenu(View anchor)
        {
            var pm = new PopupMenu(this, anchor);
            pm.Menu.Add(0, 1, 0, S(Resource.String.sort_ping));
            pm.Menu.Add(0, 2, 1, S(Resource.String.sort_game));
            pm.Menu.Add(0, 3, 2, S(Resource.String.sort_recent));
            pm.Menu.SetGroupCheckable(0, true, true);
            pm.Menu.FindItem(AppSettings.SortMode + 1)?.SetChecked(true);
            pm.MenuItemClick += (s, e) =>
            {
                AppSettings.SortMode = e.Item.ItemId - 1;
                AppSettings.Save(this);
                Refresh();
            };
            pm.Show();
        }

        // ---------------- VPN control ----------------
        void ToggleVpn()
        {
            if (MlbbVpnService.IsRunning)
            {
                StartService(new Intent(this, typeof(MlbbVpnService)).SetAction(MlbbVpnService.ActionStop));
                return;
            }
            if (!IsMlbbInstalled()) { ShowNotInstalled(); return; }

            var prep = VpnService.Prepare(this);
            if (prep != null) StartActivityForResult(prep, ReqVpn);
            else StartVpnService();
        }

        void StartVpnService()
        {
            MlbbVpnService.LastError = null;
            ContextCompat.StartForegroundService(this,
                new Intent(this, typeof(MlbbVpnService)).SetAction(MlbbVpnService.ActionStart));
            if (!AppSettings.BatteryAsked) { AppSettings.BatteryAsked = true; AppSettings.Save(this); CheckBattery(false); }
        }

        bool IsMlbbInstalled()
        {
            foreach (var pkg in AppSettings.PackageList)
            {
                try { PackageManager.GetPackageInfo(pkg, 0); return true; }
                catch (PackageManager.NameNotFoundException) { }
            }
            return false;
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode == ReqVpn)
            {
                if (resultCode == Result.Ok) StartVpnService();
                else Snack(S(Resource.String.snack_vpn_denied));
            }
            else if (requestCode == ReqExport && resultCode == Result.Ok && data?.Data != null)
            {
                try
                {
                    using var os = ContentResolver.OpenOutputStream(data.Data);
                    var bytes = Encoding.UTF8.GetBytes(StatsStore.Instance.ExportCsv());
                    os.Write(bytes, 0, bytes.Length);
                    Snack(S(Resource.String.snack_csv_saved));
                }
                catch (Exception ex) { Snack(S(Resource.String.snack_export_error, ex.Message)); }
            }
        }

        // ---------------- menu ----------------
        public override bool OnCreateOptionsMenu(IMenu menu)
        {
            MenuInflater.Inflate(Resource.Menu.menu_main, menu);
            return true;
        }

        public override bool OnOptionsItemSelected(IMenuItem item)
        {
            switch (item.ItemId)
            {
                case Resource.Id.action_donate: OpenDonate(); return true;
                case Resource.Id.action_help: ShowHelp(); return true;
                case Resource.Id.action_battery: CheckBattery(true); return true;
                case Resource.Id.action_unblock_all: ConfirmUnblockAll(); return true;
                case Resource.Id.action_settings: ShowSettings(); return true;
                case Resource.Id.action_export:
                    var i = new Intent(Intent.ActionCreateDocument)
                        .AddCategory(Intent.CategoryOpenable)
                        .SetType("text/csv")
                        .PutExtra(Intent.ExtraTitle, $"mlbb_servers_{DateTime.Now:yyyyMMdd_HHmm}.csv");
                    StartActivityForResult(i, ReqExport);
                    return true;
                case Resource.Id.action_clear: ConfirmClear(); return true;
            }
            return base.OnOptionsItemSelected(item);
        }

        void OpenDonate()
        {
            try { StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(DonateUrl))); }
            catch { Snack(S(Resource.String.snack_no_browser)); }
        }

        Snackbar MakeSnack(string text, int length)
        {
            var sb = Snackbar.Make(_root, text, length);
            sb.SetAnchorView(_nav);
            return sb;
        }

        void Snack(string text) => MakeSnack(text, Snackbar.LengthShort).Show();
    }
}
