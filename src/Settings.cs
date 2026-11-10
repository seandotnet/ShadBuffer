using System.IO;
using System.Web.Script.Serialization;

namespace ShadBuffer
{
    public sealed class Settings
    {
        public const string CaptureGame = "game";
        public const string CaptureDesktop = "desktop";
        public const string DeviceDefault = "default";
        public const string DeviceOff = "off";
        public const string NoiseOff = "off";
        public const string NoiseRnnoise = "rnnoise";
        public const string NoiseSpeex = "speex";

        public int ClipSeconds { get; set; } = 120;
        public string SaveHotkey { get; set; } = "Alt+F10";
        public string OverlayHotkey { get; set; } = "Alt+X";
        public string RecordHotkey { get; set; } = "Alt+F9";
        public string ScreenshotHotkey { get; set; } = "Alt+F1";
        public string ReplayHotkey { get; set; } = "Alt+Shift+F10";
        public string MicHotkey { get; set; } = "Ctrl+Alt+M";
        public string ClipsDir { get; set; }
        public string RecordingsDir { get; set; }
        public bool SaveSound { get; set; } = true;
        public bool SortByGame { get; set; } = true;

        public string Capture { get; set; } = CaptureGame;
        public string MonitorId { get; set; } = "";
        public int Quality { get; set; } = 20;
        public int Fps { get; set; } = 60;
        public int OutputHeight { get; set; } = 0;
        public string Codec { get; set; } = "h264";

        public bool RecordMic { get; set; } = true;
        public string MicDevice { get; set; } = DeviceDefault;
        public string NoiseSuppression { get; set; } = NoiseOff;
        public string DesktopDevice { get; set; } = DeviceDefault;
        public bool SeparateTracks { get; set; }

        public bool StartAtLogin { get; set; } = true;
        public bool RunAsAdmin { get; set; } = true;
        public bool GameBarOff { get; set; } = true;

        public bool ReplayEnabled { get; set; } = true;
        public bool KappaNoticeSeen { get; set; }

        public string FontChoice { get; set; } = "default";
        public string CustomFontFile { get; set; } = "";
        public string CustomFontFace { get; set; } = "";

        public static Settings Load(Context ctx)
        {
            Settings settings = File.Exists(ctx.SettingsPath)
                ? new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(ctx.SettingsPath))
                : new Settings();
            if (string.IsNullOrEmpty(settings.ClipsDir))
            {
                settings.ClipsDir = ctx.ClipsDir;
            }
            if (string.IsNullOrEmpty(settings.RecordingsDir))
            {
                settings.RecordingsDir = ctx.RecordingsDir;
            }
            return settings;
        }

        public void Save(Context ctx)
        {
            Directory.CreateDirectory(ctx.AppDir);
            File.WriteAllText(ctx.SettingsPath, ToJson());
        }

        public Settings Clone()
        {
            return new JavaScriptSerializer().Deserialize<Settings>(ToJson());
        }

        public string ToJson()
        {
            return new JavaScriptSerializer().Serialize(this);
        }

        // these save the moment they change, so they never count as unapplied
        public void CopyLiveValues(Settings from)
        {
            ReplayEnabled = from.ReplayEnabled;
            RecordMic = from.RecordMic;
            KappaNoticeSeen = from.KappaNoticeSeen;
            FontChoice = from.FontChoice;
            CustomFontFile = from.CustomFontFile;
            CustomFontFace = from.CustomFontFace;
        }

        public bool SameAs(Settings other)
        {
            Settings a = Clone();
            a.CopyLiveValues(other);
            return a.ToJson() == other.ToJson();
        }

        // anything obs only reads at launch, the helper owns the other hotkeys so those don't need a restart
        public bool NeedsObsRestart(Settings other)
        {
            Settings a = Clone();
            Settings b = other.Clone();
            a.CopyLiveValues(b);
            a.StartAtLogin = b.StartAtLogin;
            a.GameBarOff = b.GameBarOff;
            a.OverlayHotkey = b.OverlayHotkey;
            a.RecordHotkey = b.RecordHotkey;
            a.ScreenshotHotkey = b.ScreenshotHotkey;
            a.ReplayHotkey = b.ReplayHotkey;
            a.MicHotkey = b.MicHotkey;
            a.FontChoice = b.FontChoice;
            a.CustomFontFile = b.CustomFontFile;
            a.CustomFontFace = b.CustomFontFace;
            return a.ToJson() != b.ToJson();
        }
    }
}
