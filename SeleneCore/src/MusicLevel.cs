using System;
using System.Linq;
using System.Reflection;
using System.Windows.Threading;

namespace Selene
{
    // Playnite's "Music volume" and "Music muted" (Selene settings > Sound & music) are the master for all music. They only
    // reach Playnite's own background music, and UniPlaySong keeps a volume of its own, so Selene's start-up and home music
    // played on with both set to off. UniPlaySong's player service has a temporary level for this (SetVolumeMultiplier):
    // it is set to the master level, and UniPlaySong's own saved volume stays as it is.
    public static class MusicLevel
    {
        private static readonly Guid UpsId = new Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        private static readonly string[] Steps = { "0", "25", "50", "75", "100" };
        private static DispatcherTimer timer;
        private static double? applied;

        public static bool Installed() { return Features.HasAddon("UniPlaySong"); }

        public static void Start()
        {
            if (timer != null) return;
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (s, e) => { HomeNav.SyncMoveSound(); Apply(); };
            timer.Start();
            HomeNav.SyncMoveSound();
            Apply();   // at once; if the service isn't there yet, the next tick
        }

        // Playnite closing: UniPlaySong goes back to its own level (it keeps nothing of this, but another theme may start it again)
        public static void Release() { if (timer != null) timer.Stop(); applied = null; Set(1.0); }

        // ---- the rows in Selene settings (Playnite's own settings, edited there) ----
        public static string Volume()
        {
            var fs = FullscreenSettings();
            var p = fs != null ? fs.GetType().GetProperty("BackgroundVolume") : null;
            if (p == null) return "100";
            double v = Convert.ToDouble(p.GetValue(fs, null)) * 100;
            return Steps.OrderBy(s => Math.Abs(double.Parse(s) - v)).First();   // 45% (Selene's old default) reads as 50%
        }

        public static void SetVolume(string value)
        {
            var fs = FullscreenSettings();
            var p = fs != null ? fs.GetType().GetProperty("BackgroundVolume") : null;
            if (p == null) return;
            p.SetValue(fs, Convert.ChangeType(double.Parse(value) / 100.0, p.PropertyType), null);
            SaveNow();
            Apply();
        }

        // Playnite writes its settings when it closes; a change made here is written at once, or a restart that isn't a clean exit
        // (a crash, a killed process) brings the old level back
        private static void SaveNow()
        {
            try
            {
                var appType = Type.GetType("Playnite.PlayniteApplication, Playnite");
                var cur = appType != null ? appType.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) : null;
                var current = cur != null ? cur.GetValue(null, null) : null;
                var prop = current != null ? appType.GetProperty("AppSettings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                var settings = prop != null ? prop.GetValue(current, null) : null;
                var save = settings != null ? settings.GetType().GetMethod("SaveSettings", Type.EmptyTypes) : null;
                if (save == null) { HomeNav.Log("music level: Playnite has no SaveSettings()"); return; }
                save.Invoke(settings, null);
            }
            catch (Exception ex) { HomeNav.Log("music level: save: " + (ex.InnerException ?? ex).Message); }
        }
        // Mute music is Selene's own setting: Playnite's IsMusicMuted is not written to its config, so it came back at every
        // start (muted, whatever was chosen). Selene keeps the choice and hands it to Playnite.
        public static bool Muted() { return Features.On("musicmuted"); }

        private static void SyncPlayniteMute(object fs, bool muted)
        {
            var p = fs.GetType().GetProperty("IsMusicMuted");
            if (p != null && !Equals(p.GetValue(fs, null), muted)) p.SetValue(fs, muted, null);
        }
        // ---- UniPlaySong's own switches that choose what plays (not how loud) ----
        public static bool UpsSwitch(string name)
        {
            var settings = UpsSettings();
            var p = settings != null ? settings.GetType().GetProperty(name) : null;
            return p != null && Equals(p.GetValue(settings, null), true);
        }

        public static void SetUpsSwitch(string name, bool on)
        {
            try
            {
                var plugin = HomeNav.Api.Addons.Plugins.FirstOrDefault(p => p.Id == UpsId);
                var settings = UpsSettings();
                var prop = settings != null ? settings.GetType().GetProperty(name) : null;
                if (plugin == null || prop == null || !prop.CanWrite) { HomeNav.Log("music: UniPlaySong setting " + name + " not found"); return; }
                prop.SetValue(settings, on, null);
                // saved the way the add-on saves its own settings page
                var save = typeof(Playnite.SDK.Plugins.Plugin).GetMethod("SavePluginSettings").MakeGenericMethod(settings.GetType());
                save.Invoke(plugin, new[] { settings });
                HomeNav.Log("music: UniPlaySong " + name + " " + (on ? "on" : "off"));
            }
            catch (Exception ex) { HomeNav.Log("music: changing " + name + " failed: " + (ex.InnerException ?? ex).Message); }
        }

        // "Now playing: title - artist" for the row under the volume (nothing when nothing plays)
        public static string NowPlaying()
        {
            var settings = UpsSettings();
            if (settings == null) return null;
            var title = settings.GetType().GetProperty("NowPlayingTitle");
            var artist = settings.GetType().GetProperty("NowPlayingArtist");
            var name = title != null ? title.GetValue(settings, null) as string : null;
            if (string.IsNullOrEmpty(name)) return null;
            var by = artist != null ? artist.GetValue(settings, null) as string : null;
            return "Now playing: " + name + (string.IsNullOrEmpty(by) ? "" : " - " + by);
        }

        // the song and its artist as UniPlaySong has them (the default music's title is its file name in [Default] brackets)
        public static void Song(out string title, out string artist)
        {
            title = artist = null;
            var settings = UpsSettings();
            if (settings == null) return;
            var t = settings.GetType().GetProperty("NowPlayingTitle");
            var a = settings.GetType().GetProperty("NowPlayingArtist");
            title = t != null ? t.GetValue(settings, null) as string : null;
            artist = a != null ? a.GetValue(settings, null) as string : null;
            if (title != null && title.StartsWith("[Default]")) title = title.Substring(9).Trim();
            // UniPlaySong's title follows the game the cursor is on, while Selene keeps the home music going: what is really
            // playing is the file its player has open. Where the two differ, the file's name is shown.
            var file = PlayingFile();
            if (!string.IsNullOrEmpty(file) && (title == null || Plain(title).IndexOf(Plain(file), StringComparison.Ordinal) < 0 && Plain(file).IndexOf(Plain(title), StringComparison.Ordinal) < 0))
            { title = Readable(file); artist = null; }
        }

        // a file name as words: "tunetank-dark-ambient-soundscape-music" reads as the default music's title does (spaces)
        private static string Readable(string file)
        {
            var s = file.Replace('_', ' ');
            if (s.IndexOf(' ') < 0) s = s.Replace('-', ' ');
            return s.Trim();
        }
        private static string Plain(string s) { return new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant(); }

        // the name of the file the player has open (without its folder or extension)
        private static string PlayingFile()
        {
            try
            {
                var svc = Service();
                var p = svc != null ? svc.GetType().GetProperty("CurrentSongPath") : null;
                var path = p != null ? p.GetValue(svc, null) as string : null;
                return string.IsNullOrEmpty(path) ? null : System.IO.Path.GetFileNameWithoutExtension(path);
            }
            catch { return null; }
        }
        private static object UpsSettings() { return HomeNav.LiveUps(); }

        // ---- the master level, applied to UniPlaySong ----
        private static void Apply()
        {
            try
            {
                if (!Installed()) return;
                var fs = FullscreenSettings();
                if (fs == null) return;
                var volume = fs.GetType().GetProperty("BackgroundVolume");
                SyncPlayniteMute(fs, Muted());
                double level = Muted() ? 0.0 : (volume != null ? Math.Max(0.0, Math.Min(1.0, Convert.ToDouble(volume.GetValue(fs, null)))) : 1.0);
                // checked every tick (the player may not exist yet at start-up), but only written when it differs (Set)
                if (!Set(level)) return;
                if (applied.HasValue && Math.Abs(applied.Value - level) < 0.001) return;
                applied = level;
                HomeNav.Log("music level: UniPlaySong at " + Math.Round(level * 100) + "% (Playnite's music volume" + (Muted() ? ", muted" : "") + ")");
            }
            catch (Exception ex) { HomeNav.Log("music level: " + ex.Message); }
        }

        // UniPlaySong's player (its plugin keeps it in a private field)
        private static object Service()
        {
            var plugin = HomeNav.Api != null ? HomeNav.Api.Addons.Plugins.FirstOrDefault(p => p.Id == UpsId) : null;
            var field = plugin != null ? plugin.GetType().GetField("_playbackService", BindingFlags.Instance | BindingFlags.NonPublic) : null;
            return field != null ? field.GetValue(plugin) : null;
        }

        // the player's own controls: Toggle ("TogglePlayPauseInternal"), Next ("SkipToNextSong"), Restart ("RestartCurrentSong")
        public static void Control(string method)
        {
            try
            {
                var svc = Service();
                var m = svc != null ? svc.GetType().GetMethod(method, Type.EmptyTypes) : null;
                if (m == null) { HomeNav.Log("music: UniPlaySong has no " + method + "()"); return; }
                m.Invoke(svc, null);
                HomeNav.Log("music: " + method);
            }
            catch (Exception ex) { HomeNav.Log("music: " + method + ": " + (ex.InnerException ?? ex).Message); }
        }

        public static bool Paused()
        {
            try
            {
                var svc = Service();
                var p = svc != null ? svc.GetType().GetProperty("IsPaused") : null;
                return p != null && (bool)p.GetValue(svc, null);
            }
            catch { return false; }
        }

        // the service's own temporary level        // (0 = silent, 1 = as UniPlaySong has it): its saved volume stays as it was
        private static bool Set(double level)
        {
            try
            {
                var svc = Service();
                if (svc == null) return false;
                // only when it differs: setting it writes the player's volume at once, which cancels a fade under way, and
                // UniPlaySong changes songs only when its fade-out ends (set every tick, songs got stuck)
                var held = svc.GetType().GetField("_volumeMultiplier", BindingFlags.Instance | BindingFlags.NonPublic);
                if (held != null && Math.Abs(Convert.ToDouble(held.GetValue(svc)) - level) < 0.001) return true;
                var set = svc.GetType().GetMethod("SetVolumeMultiplier", new[] { typeof(double) });
                if (set == null) { HomeNav.Log("music level: UniPlaySong has no SetVolumeMultiplier(double)"); return false; }
                set.Invoke(svc, new object[] { level });
                return true;
            }
            catch (Exception ex) { HomeNav.Log("music level: " + ex.Message); return false; }
        }

        private static object FullscreenSettings()
        {
            var appType = Type.GetType("Playnite.PlayniteApplication, Playnite");
            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var cur = appType != null ? appType.GetProperty("Current", flags) : null;
            var current = cur != null ? cur.GetValue(null, null) : null;
            if (current == null) return null;
            var settingsProp = appType.GetProperty("AppSettings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var settings = settingsProp != null ? settingsProp.GetValue(current, null) : null;
            var fs = settings != null ? settings.GetType().GetProperty("Fullscreen") : null;
            return fs != null ? fs.GetValue(settings, null) : null;
        }
    }
}
