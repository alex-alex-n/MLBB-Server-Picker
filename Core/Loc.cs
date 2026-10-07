using System.Linq;
using Android.Content;
using Android.Content.Res;

namespace MlbbServerPicker.Core
{
    /// <summary>
    /// Локализация. Строки берутся из ресурсов (values = EN, values-ru = RU).
    /// Если язык выбран вручную — контекст оборачивается нужной локалью.
    /// </summary>
    public static class Loc
    {
        static Context _app, _ctx;

        public static void Init(Context c)
        {
            if (_app == null) _app = c.ApplicationContext ?? c;
            Rebuild();
        }

        public static void Rebuild() { _ctx = _app == null ? null : Wrap(_app); }

        public static Context Ctx => _ctx ?? _app;

        public static Context Wrap(Context c)
        {
            var lang = AppSettings.Language;
            if (string.IsNullOrEmpty(lang)) return c;           // системный язык: Android сам выберет ru или en
            var conf = new Configuration(c.Resources.Configuration);
            conf.SetLocale(new Java.Util.Locale(lang));
            return c.CreateConfigurationContext(conf);
        }

        public static string S(int id, params object[] args)
        {
            var c = Ctx;
            if (c == null) return "";
            if (args == null || args.Length == 0) return c.GetString(id);
            return c.Resources.GetString(id, args.Select(J).ToArray());
        }

        static Java.Lang.Object J(object a) => a switch
        {
            null => new Java.Lang.String(""),
            Java.Lang.Object jo => jo,
            int i => Java.Lang.Integer.ValueOf(i),
            long l => Java.Lang.Long.ValueOf(l),
            string s => new Java.Lang.String(s),
            _ => new Java.Lang.String(a.ToString())
        };

        public static string Ms(double v) => S(Resource.String.ms_fmt, v.ToString("0"));
    }
}
