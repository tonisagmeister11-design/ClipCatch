using System;
using System.IO;
using System.Xml.Serialization;

namespace ClipCatch
{
    public enum OutputResolution { Original, P720, P1080, P1440, P2160 }

    public enum VideoQuality { Standard, High, Maximum }

    /// <summary>Everything set in the main window; kept in %APPDATA%\ClipCatch\settings.xml.</summary>
    public sealed class AppSettings
    {
        public int Fps { get; set; } = 60;
        public OutputResolution Resolution { get; set; } = OutputResolution.Original;
        public VideoQuality Quality { get; set; } = VideoQuality.High;
        public bool SystemAudio { get; set; } = true;
        public bool Microphone { get; set; }
        public bool ShowCursor { get; set; } = true;
        public bool ShowClicks { get; set; }
        public bool Countdown { get; set; } = true;
        public string OutputFolder { get; set; }

        public static string DataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipCatch");

        public static string LogDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipCatch");

        public static string DefaultOutputFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ClipCatch");

        private static string FilePath => Path.Combine(DataDir, "settings.xml");

        public string EffectiveOutputFolder =>
            string.IsNullOrWhiteSpace(OutputFolder) ? DefaultOutputFolder : OutputFolder;

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    using (var s = File.OpenRead(FilePath))
                    {
                        var loaded = (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(s);
                        if (loaded.Fps != 30 && loaded.Fps != 60) loaded.Fps = 60;
                        return loaded;
                    }
                }
            }
            catch
            {
                // A damaged file falls back to the defaults.
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var tmp = FilePath + ".tmp";
                using (var s = File.Create(tmp))
                {
                    new XmlSerializer(typeof(AppSettings)).Serialize(s, this);
                }
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch
            {
                // Settings are a convenience; never fail a recording over them.
            }
        }
    }
}
