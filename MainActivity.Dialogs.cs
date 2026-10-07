using System;
using System.Linq;
using System.Text;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Text;
using Google.Android.Material.Dialog;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.Snackbar;
using Google.Android.Material.TextField;
using MlbbServerPicker.Core;
using MlbbServerPicker.Vpn;


namespace MlbbServerPicker
{
    public partial class MainActivity
    {
        static readonly int[] AutoPingIntervals = { 5, 10, 15, 30, 60 };

        // ---------------- действия с серверами ----------------
        void OnBlockClicked(ServerStat s)
        {
            var store = StatsStore.Instance;
            if (s.Blocked || s.BlockPending)
            {
                store.SetBlocked(s.Ip, false);
                Refresh();
                Snack(S(Resource.String.snack_unblocked, s.Ip));
                return;
            }

            if (!s.IsGame && !s.Favorite)
            {
                new MaterialAlertDialogBuilder(this)
                    .SetTitle(S(Resource.String.dlg_not_game_title))
                    .SetMessage(S(Resource.String.dlg_not_game_msg))
                    .SetPositiveButton(S(Resource.String.btn_block), (d, e) => { store.SetBlocked(s.Ip, true); Refresh(); Snack(S(Resource.String.snack_blocked, s.Ip)); })
                    .SetNegativeButton(S(Resource.String.btn_cancel), (d, e) => { })
                    .Show();
                return;
            }

            if (store.CurrentMatchIp == s.Ip)
            {
                new MaterialAlertDialogBuilder(this)
                    .SetTitle(S(Resource.String.dlg_match_now_title))
                    .SetMessage(S(Resource.String.dlg_match_now_msg))
                    .SetPositiveButton(S(Resource.String.btn_after_match), (d, e) => { store.SetBlockPending(s.Ip); Refresh(); Snack(S(Resource.String.snack_block_pending, s.Ip)); })
                    .SetNegativeButton(S(Resource.String.btn_now), (d, e) => { store.SetBlocked(s.Ip, true); Refresh(); Snack(S(Resource.String.snack_blocked, s.Ip)); })
                    .SetNeutralButton(S(Resource.String.btn_cancel), (d, e) => { })
                    .Show();
                return;
            }

            store.SetBlocked(s.Ip, true);
            Refresh();
            MakeSnack(S(Resource.String.snack_blocked, s.Ip), Snackbar.LengthLong)
                .SetAction(S(Resource.String.btn_undo), v => { store.SetBlocked(s.Ip, false); Refresh(); })
                .Show();
        }

        void ShowKeepOnly(ServerStat s)
        {
            var store = StatsStore.Instance;
            if (s.Favorite)
            {
                new MaterialAlertDialogBuilder(this)
                    .SetTitle(S(Resource.String.dlg_fav_title, s.Ip))
                    .SetMessage(S(Resource.String.dlg_fav_msg))
                    .SetPositiveButton(S(Resource.String.btn_unfavorite), (d, e) => { store.SetFavorite(s.Ip, false); Refresh(); })
                    .SetNegativeButton(S(Resource.String.btn_cancel), (d, e) => { })
                    .Show();
                return;
            }

            var all = store.Snapshot();
            var others = all.Where(x => x.IsGame && !x.Favorite && !x.Blocked && !x.BlockPending && x.Ip != s.Ip).ToList();
            string cur = store.CurrentMatchIp;
            bool matchNow = cur != null && cur != s.Ip && others.Any(x => x.Ip == cur);
            string geo = s.GeoLine != null ? $" ({s.GeoLine})" : "";

            var msg = new StringBuilder();
            msg.Append(S(Resource.String.keep_msg_1, s.Ip, geo)).Append("\n\n");
            msg.Append(others.Count > 0 ? S(Resource.String.keep_msg_others, others.Count) : S(Resource.String.keep_msg_no_others));
            if (matchNow) msg.Append(' ').Append(S(Resource.String.keep_msg_match_note));
            msg.Append(".\n\n").Append(S(Resource.String.keep_msg_strict)).Append("\n\n");
            msg.Append(S(Resource.String.keep_msg_lobby)).Append("\n\n");
            msg.Append(S(Resource.String.keep_msg_hang));

            new MaterialAlertDialogBuilder(this)
                .SetTitle(S(Resource.String.dlg_keep_title))
                .SetMessage(msg.ToString())
                .SetPositiveButton(S(Resource.String.btn_keep_only_this), (d, e) => DoKeepOnly(s.Ip))
                .SetNegativeButton(S(Resource.String.btn_cancel), (d, e) => { })
                .Show();
        }

        void DoKeepOnly(string ip)
        {
            var store = StatsStore.Instance;
            var changed = store.KeepOnly(ip, out int pending);
            AppSettings.StrictMode = true;
            AppSettings.Save(this);
            Refresh();
            string text = S(Resource.String.snack_keep_result, ip, changed.Count - pending)
                        + (pending > 0 ? ", " + S(Resource.String.keep_pending_suffix, pending) : "");
            MakeSnack(text, Snackbar.LengthLong)
                .SetAction(S(Resource.String.btn_undo), v => { store.Unblock(changed); store.SetFavorite(ip, false); Refresh(); })
                .Show();
        }

        void OnBestButton()
        {
            var store = StatsStore.Instance;
            if (!_bestIsFavMode)
            {
                if (_best != null) ShowKeepOnly(_best);
                return;
            }

            var all = store.Snapshot();
            int othersOpen = all.Count(x => x.IsGame && !x.Favorite && !x.Blocked && !x.BlockPending);
            if (othersOpen > 0)
            {
                new MaterialAlertDialogBuilder(this)
                    .SetTitle(S(Resource.String.dlg_block_others_title))
                    .SetMessage(S(Resource.String.dlg_block_others_msg, othersOpen))
                    .SetPositiveButton(S(Resource.String.btn_block), (d, e) =>
                    {
                        var changed = store.BlockAllExceptFavorites(out int pending);
                        Refresh();
                        string text = S(Resource.String.snack_blocked_count, changed.Count - pending)
                                    + (pending > 0 ? ", " + S(Resource.String.keep_pending_suffix, pending) : "");
                        MakeSnack(text, Snackbar.LengthLong)
                            .SetAction(S(Resource.String.btn_undo), v => { store.Unblock(changed); Refresh(); })
                            .Show();
                    })
                    .SetNegativeButton(S(Resource.String.btn_cancel), (d, e) => { })
                    .Show();
            }
            else
            {
                new MaterialAlertDialogBuilder(this)
                    .SetTitle(S(Resource.String.dlg_reset_title))
                    .SetMessage(S(Resource.String.dlg_reset_msg))
                    .SetPositiveButton(S(Resource.String.btn_reset), (d, e) =>
                    {
                        store.UnblockAll(true);
                        AppSettings.StrictMode = false; AppSettings.Save(this);
                        Refresh(); Snack(S(Resource.String.snack_reset));
                    })
                    .SetNegativeButton(S(Resource.String.btn_cancel), (d, e) => { })
                    .Show();
            }
        }

        void ConfirmUnblockAll()
        {
            new MaterialAlertDialogBuilder(this)
                .SetTitle(S(Resource.String.dlg_unblock_all_title))
                .SetMessage(S(Resource.String.dlg_unblock_all_msg))
                .SetPositiveButton(S(Resource.String.btn_unblock), (d, e) =>
                {
                    StatsStore.Instance.UnblockAll(false);
                    AppSettings.StrictMode = false; AppSettings.Save(this);
                    Refresh(); Snack(S(Resource.String.snack_unblocked_all));
                })
                .SetNegativeButton(S(Resource.String.btn_cancel), (d, e) => { })
                .Show();
        }

        void ConfirmClear()
        {
            new MaterialAlertDialogBuilder(this)
                .SetTitle(S(Resource.String.dlg_clear_title))
                .SetMessage(S(Resource.String.dlg_clear_msg))
                .SetPositiveButton(S(Resource.String.btn_clear_counters), (d, e) => { StatsStore.Instance.ClearStats(false); Refresh(); Snack(S(Resource.String.snack_counters_cleared)); })
                .SetNeutralButton(S(Resource.String.btn_delete_all), (d, e) =>
                {
                    new MaterialAlertDialogBuilder(this)
                        .SetTitle(S(Resource.String.dlg_delete_all_title))
                        .SetMessage(S(Resource.String.dlg_delete_all_msg))
                        .SetPositiveButton(S(Resource.String.btn_delete_all), (d2, e2) => { StatsStore.Instance.ClearStats(true); AppSettings.StrictMode = false; AppSettings.Save(this); Refresh(); })
                        .SetNegativeButton(S(Resource.String.btn_cancel), (d2, e2) => { })
                        .Show();
                })
                .SetNegativeButton(S(Resource.String.btn_cancel), (d, e) => { })
                .Show();
        }

        void OnMoreClicked(ServerStat s, View anchor)
        {
            const int MLabel = 1, MFav = 2, MPing = 3, MDetails = 4, MCopy = 5, MWhois = 6;
            var pm = new PopupMenu(this, anchor);
            pm.Menu.Add(0, MPing, 0, S(Resource.String.menu_ping));
            pm.Menu.Add(0, MFav, 1, S(s.Favorite ? Resource.String.menu_fav_remove : Resource.String.menu_fav_add));
            pm.Menu.Add(0, MLabel, 2, S(Resource.String.menu_label));
            pm.Menu.Add(0, MDetails, 3, S(Resource.String.menu_details));
            pm.Menu.Add(0, MCopy, 4, S(Resource.String.menu_copy));
            pm.Menu.Add(0, MWhois, 5, S(Resource.String.menu_whois));
            pm.MenuItemClick += (sender, e) =>
            {
                switch (e.Item.ItemId)
                {
                    case MPing: OnPingOne(s); break;
                    case MLabel: OnLabelClicked(s); break;
                    case MFav:
                        StatsStore.Instance.SetFavorite(s.Ip, !s.Favorite);
                        Refresh();
                        Snack(S(s.Favorite ? Resource.String.snack_fav_removed : Resource.String.snack_fav_added, s.Ip));
                        break;
                    case MDetails: _adapter.ToggleExpanded(s.Ip); _favAdapter.ToggleExpanded(s.Ip); break;
                    case MCopy:
                        var cb = (ClipboardManager)GetSystemService(ClipboardService);
                        cb.PrimaryClip = ClipData.NewPlainText("ip", s.Ip);
                        Snack(S(Resource.String.snack_ip_copied));
                        break;
                    case MWhois:
                        try { StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse("https://ipinfo.io/" + s.Ip))); }
                        catch { Snack(S(Resource.String.snack_no_browser)); }
                        break;
                }
            };
            pm.Show();
        }

        void OnLabelClicked(ServerStat s)
        {
            var input = new EditText(this) { Text = s.Label, Hint = S(Resource.String.label_hint) };
            input.SetSingleLine(true);
            var container = new FrameLayout(this);
            int pad = (int)(20 * Resources.DisplayMetrics.Density);
            container.SetPadding(pad, pad / 2, pad, 0);
            container.AddView(input);

            new MaterialAlertDialogBuilder(this)
                .SetTitle(S(Resource.String.dlg_label_title, s.Ip))
                .SetView(container)
                .SetPositiveButton(S(Resource.String.btn_save), (d, e) => { StatsStore.Instance.SetLabel(s.Ip, input.Text?.Trim()); Refresh(); })
                .SetNegativeButton(S(Resource.String.btn_cancel), (d, e) => { })
                .Show();
        }

        // ---------------- системные диалоги ----------------
        void ShowNotInstalled()
        {
            new MaterialAlertDialogBuilder(this)
                .SetTitle(S(Resource.String.dlg_not_installed_title))
                .SetMessage(S(Resource.String.dlg_not_installed_msg, AppSettings.Packages))
                .SetPositiveButton(S(Resource.String.btn_settings), (s, e) => ShowSettings())
                .SetNegativeButton(S(Resource.String.btn_close), (s, e) => { })
                .Show();
        }

        void CheckBattery(bool fromMenu)
        {
            try
            {
                var pm = (PowerManager)GetSystemService(PowerService);
                if (pm.IsIgnoringBatteryOptimizations(PackageName))
                {
                    if (fromMenu) Snack(S(Resource.String.snack_battery_ok));
                    return;
                }
                new MaterialAlertDialogBuilder(this)
                    .SetTitle(S(Resource.String.dlg_battery_title))
                    .SetMessage(S(Resource.String.dlg_battery_msg))
                    .SetPositiveButton(S(Resource.String.btn_open_settings), (s, e) =>
                    {
                        try
                        {
                            StartActivity(new Intent(Settings.ActionRequestIgnoreBatteryOptimizations,
                                Android.Net.Uri.Parse("package:" + PackageName)));
                        }
                        catch { try { StartActivity(new Intent(Settings.ActionIgnoreBatteryOptimizationSettings)); } catch { } }
                    })
                    .SetNegativeButton(S(Resource.String.btn_later), (s, e) => { })
                    .Show();
            }
            catch (Exception ex) { Log.Warn("MLBB.UI", "battery: " + ex.Message); }
        }

        void ShowSettings()
        {
            var v = LayoutInflater.Inflate(Resource.Layout.dialog_settings, null);
            var spLang = v.FindViewById<Spinner>(Resource.Id.spLanguage);
            var spInt = v.FindViewById<Spinner>(Resource.Id.spAutoPing);
            var swRst = v.FindViewById<MaterialSwitch>(Resource.Id.swRst);
            var edPackages = v.FindViewById<TextInputEditText>(Resource.Id.edPackages);
            var edDns = v.FindViewById<TextInputEditText>(Resource.Id.edDns);
            var edProbe = v.FindViewById<TextInputEditText>(Resource.Id.edProbePorts);
            var edMatch = v.FindViewById<TextInputEditText>(Resource.Id.edMatchPorts);
            var edLobby = v.FindViewById<TextInputEditText>(Resource.Id.edLobbyPorts);

            var langAdapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem,
                new[] { S(Resource.String.lang_system), "Русский", "English" });
            langAdapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
            spLang.Adapter = langAdapter;
            spLang.SetSelection(AppSettings.Language == "ru" ? 1 : AppSettings.Language == "en" ? 2 : 0);

            var intAdapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem,
                AutoPingIntervals.Select(i => S(Resource.String.sec_fmt, i)).ToArray());
            intAdapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
            spInt.Adapter = intAdapter;
            int idx = Array.IndexOf(AutoPingIntervals, AppSettings.AutoPingInterval);
            spInt.SetSelection(idx < 0 ? 1 : idx);

            swRst.Checked = AppSettings.RstBlocked;
            edPackages.Text = AppSettings.Packages;
            edDns.Text = AppSettings.Dns;
            edProbe.Text = AppSettings.ProbePortsText;
            edMatch.Text = AppSettings.MatchPortsText;
            edLobby.Text = AppSettings.LobbyPortsText;

            new MaterialAlertDialogBuilder(this)
                .SetTitle(S(Resource.String.settings_title))
                .SetView(v)
                .SetPositiveButton(S(Resource.String.btn_save), (d, e) =>
                {
                    string newLang = spLang.SelectedItemPosition == 1 ? "ru" : spLang.SelectedItemPosition == 2 ? "en" : "";
                    bool langChanged = newLang != (AppSettings.Language ?? "");
                    AppSettings.Language = newLang;
                    AppSettings.AutoPingInterval = AutoPingIntervals[Math.Clamp(spInt.SelectedItemPosition, 0, AutoPingIntervals.Length - 1)];
                    AppSettings.RstBlocked = swRst.Checked;
                    AppSettings.Packages = edPackages.Text?.Trim() ?? "";
                    AppSettings.Dns = edDns.Text?.Trim() ?? "";
                    AppSettings.ProbePortsText = edProbe.Text?.Trim() ?? "";
                    AppSettings.MatchPortsText = edMatch.Text?.Trim() ?? "";
                    AppSettings.LobbyPortsText = edLobby.Text?.Trim() ?? "";
                    AppSettings.Save(this);
                    if (langChanged) { Loc.Rebuild(); Recreate(); return; }
                    Snack(S(MlbbVpnService.IsRunning ? Resource.String.snack_saved_restart : Resource.String.snack_saved));
                })
                .SetNegativeButton(S(Resource.String.btn_cancel), (d, e) => { })
                .Show();
        }

        void ShowHelp()
        {
            var tv = new TextView(this);
            tv.TextFormatted = HtmlCompat.FromHtml(S(Resource.String.help_html), HtmlCompat.FromHtmlModeCompact);
            tv.SetTextColor(Col(Resource.Color.text));
            tv.SetTextSize(ComplexUnitType.Sp, 14);
            tv.SetLineSpacing(0, 1.15f);
            int pad = (int)(20 * Resources.DisplayMetrics.Density);
            tv.SetPadding(pad, pad / 2, pad, pad / 2);
            var sv = new ScrollView(this);
            sv.AddView(tv);

            new MaterialAlertDialogBuilder(this)
                .SetTitle(S(Resource.String.help_title))
                .SetView(sv)
                .SetPositiveButton(S(Resource.String.btn_ok), (d, e) => { })
                .SetNeutralButton(S(Resource.String.donate_button) + " ❤", (d, e) => OpenDonate())
                .Show();
        }
    }
}
