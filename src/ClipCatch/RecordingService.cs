using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ScreenRecorderLib;

namespace ClipCatch
{
    /// <summary>What to record: a rectangle on one monitor, in physical pixels relative to that monitor.</summary>
    public sealed class RecordingRequest
    {
        public MonitorInfo Monitor { get; set; }
        public Int32Rect Region { get; set; }
        public AppSettings Settings { get; set; }
        /// <summary>Where the finished file goes.</summary>
        public string FilePath { get; set; }
    }

    /// <summary>
    /// Records one region to an H.264/AAC MP4 with a constant frame rate, the format editors such as
    /// Premiere Pro import without conversion. Events arrive on the UI thread.
    ///
    /// Reliability: the video is written to a local temp folder first (OneDrive and Windows'
    /// controlled folder access cannot block it), a watchdog notices a capture that never delivers
    /// frames, and a recording that fails before its first frame is retried with the next capture
    /// method and encoder, so it records on as many PCs as possible.
    /// </summary>
    public sealed class RecordingService : IDisposable
    {
        /// <summary>Capture methods and encoders, in the order they are tried.</summary>
        private static readonly (RecorderApi Api, bool Hardware, string Name)[] Attempts =
        {
            (RecorderApi.WindowsGraphicsCapture, true, "Windows Graphics Capture, Hardware-Encoder"),
            (RecorderApi.DesktopDuplication, true, "Desktop Duplication, Hardware-Encoder"),
            (RecorderApi.WindowsGraphicsCapture, false, "Windows Graphics Capture, Software-Encoder"),
            (RecorderApi.DesktopDuplication, false, "Desktop Duplication, Software-Encoder"),
        };

        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(4);

        private readonly Dispatcher _ui;
        private readonly DispatcherTimer _watchdog;
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly Stopwatch _sinceBegin = new Stopwatch();
        private readonly Stopwatch _sinceRecording = new Stopwatch();
        private Recorder _recorder;
        private RecordingRequest _request;
        private string _tempPath;
        private int _attempt;
        private bool _hasFrames;
        private bool _stopRequested;
        private bool _finishing;
        private readonly List<string> _errors = new List<string>();

        /// <summary>The first frame was captured: the recording really runs.</summary>
        public event Action Started;
        public event Action<string> Completed;
        public event Action<string> Failed;

        public RecordingService(Dispatcher ui)
        {
            _ui = ui;
            _watchdog = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(250) };
            _watchdog.Tick += (s, e) => Watch();
        }

        public bool IsActive => _recorder != null || _finishing;
        public bool IsRecording => _hasFrames && _recorder != null;
        public bool IsPaused { get; private set; }
        public TimeSpan Elapsed => _clock.Elapsed;

        public static string TempDir => Path.Combine(AppSettings.LogDir, "Temp");

        /// <summary>The output frame size for a region and a chosen resolution: even, aspect kept.</summary>
        public static Int32Rect OutputSize(Int32Rect region, OutputResolution resolution)
        {
            double w = region.Width, h = region.Height;
            int targetHeight;
            switch (resolution)
            {
                case OutputResolution.P720: targetHeight = 720; break;
                case OutputResolution.P1080: targetHeight = 1080; break;
                case OutputResolution.P1440: targetHeight = 1440; break;
                case OutputResolution.P2160: targetHeight = 2160; break;
                default: targetHeight = 0; break;
            }
            if (targetHeight > 0)
            {
                // Portrait regions: the chosen size is the short side, like phone video.
                double scale = w >= h ? targetHeight / h : targetHeight / w;
                w *= scale;
                h *= scale;
            }
            // Encoders refuse tiny frames: at least 64 pixels on the short side.
            double grow = 64.0 / Math.Min(w, h);
            if (grow > 1)
            {
                w *= grow;
                h *= grow;
            }
            // H.264 encoders on Windows accept at most 4096 pixels on the long side.
            double limit = 4096.0 / Math.Max(w, h);
            if (limit < 1)
            {
                w *= limit;
                h *= limit;
            }
            return new Int32Rect(0, 0, Even(w), Even(h));
        }

        private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);

        /// <summary>Target bit rate: bits per pixel per frame by quality, capped for hardware encoders.</summary>
        public static int Bitrate(Int32Rect size, int fps, VideoQuality quality)
        {
            double bpp = quality == VideoQuality.Maximum ? 0.25 : quality == VideoQuality.High ? 0.15 : 0.09;
            double bits = size.Width * (double)size.Height * fps * bpp;
            return (int)Math.Max(2_000_000, Math.Min(bits, 100_000_000));
        }

        public void Start(RecordingRequest request)
        {
            if (IsActive) throw new InvalidOperationException("Es läuft bereits eine Aufnahme.");
            _request = request;
            _stopRequested = false;
            _finishing = false;
            _hasFrames = false;
            IsPaused = false;
            _clock.Reset();
            _errors.Clear();
            Directory.CreateDirectory(TempDir);
            CleanTempDir();
            _tempPath = Path.Combine(TempDir, Path.GetFileName(request.FilePath));
            Log.Info($"Aufnahme: {request.Monitor.DeviceName} {request.Region.Width}x{request.Region.Height}+{request.Region.X}+{request.Region.Y}, " +
                     $"{request.Settings.Fps} FPS, {request.Settings.Resolution}, {request.Settings.Quality} → {request.FilePath}");
            _attempt = -1;
            NextAttempt();
            _watchdog.Start();
        }

        /// <summary>Starts the next capture method; false when all have been tried.</summary>
        private bool NextAttempt()
        {
            while (++_attempt < Attempts.Length)
            {
                var a = Attempts[_attempt];
                try
                {
                    TryDelete(_tempPath);
                    Log.Info($"Versuch {_attempt + 1}: {a.Name}");
                    var recorder = Recorder.CreateRecorder(BuildOptions(_request, a.Api, a.Hardware));
                    recorder.OnRecordingComplete += (s, e) => _ui.BeginInvoke(new Action(() => OnComplete(recorder)));
                    recorder.OnRecordingFailed += (s, e) => _ui.BeginInvoke(new Action(() => OnFailed(recorder, e.Error)));
                    recorder.OnStatusChanged += (s, e) => _ui.BeginInvoke(new Action(() => OnStatus(recorder, e.Status)));
                    _recorder = recorder;
                    _sinceBegin.Restart();
                    _sinceRecording.Reset();
                    recorder.Record(_tempPath);
                    return true;
                }
                catch (Exception ex)
                {
                    _errors.Add($"{a.Name}: {ex.Message}");
                    Log.Error($"{a.Name} startet nicht", ex);
                    Release();
                }
            }
            return false;
        }

        private static RecorderOptions BuildOptions(RecordingRequest r, RecorderApi api, bool hardware)
        {
            var s = r.Settings;
            var source = new DisplayRecordingSource(r.Monitor.DeviceName)
            {
                RecorderApi = api,
                IsBorderRequired = false,
                IsCursorCaptureEnabled = s.ShowCursor,
                SourceRect = new ScreenRect(r.Region.X, r.Region.Y, r.Region.Width, r.Region.Height),
            };
            var size = OutputSize(r.Region, s.Resolution);

            // The default devices; a missing one (no microphone, no speakers) is left out instead of
            // failing the recording.
            var audioSources = new List<AudioSourceBase>();
            try
            {
                if (s.SystemAudio && LoopbackAudioSource.Default is LoopbackAudioSource speakers) audioSources.Add(speakers);
                if (s.Microphone && CaptureAudioSource.Default is CaptureAudioSource mic) audioSources.Add(mic);
            }
            catch (Exception ex)
            {
                Log.Error("Audiogeräte nicht verfügbar, Aufnahme ohne Ton", ex);
                audioSources.Clear();
            }

            Directory.CreateDirectory(AppSettings.LogDir);
            return new RecorderOptions
            {
                SourceOptions = new SourceOptions { RecordingSources = new List<RecordingSourceBase> { source } },
                OutputOptions = new OutputOptions
                {
                    RecorderMode = RecorderMode.Video,
                    OutputFrameSize = new ScreenSize(size.Width, size.Height),
                    Stretch = StretchMode.Uniform,
                },
                AudioOptions = new AudioOptions
                {
                    IsAudioEnabled = audioSources.Count > 0,
                    AudioSources = audioSources,
                    Bitrate = AudioBitrate.bitrate_192kbps,
                    Channels = AudioChannels.Stereo,
                },
                VideoEncoderOptions = new VideoEncoderOptions
                {
                    Encoder = new H264VideoEncoder
                    {
                        BitrateMode = H264BitrateControlMode.UnconstrainedVBR,
                        EncoderProfile = H264Profile.High,
                    },
                    Bitrate = Bitrate(size, s.Fps, s.Quality),
                    Framerate = s.Fps,
                    // Constant frame rate: editors expect it (variable-rate screen video drifts out of sync).
                    IsFixedFramerate = true,
                    IsHardwareEncodingEnabled = hardware,
                    IsLowLatencyEnabled = false,
                    // A plain MP4, written straight to disk: opens everywhere, Premiere included.
                    IsFragmentedMp4Enabled = false,
                    IsMp4FastStartEnabled = false,
                },
                MouseOptions = new MouseOptions
                {
                    IsMousePointerEnabled = s.ShowCursor,
                    IsMouseClicksDetected = s.ShowClicks,
                    MouseLeftClickDetectionColor = "#FFD400",
                    MouseRightClickDetectionColor = "#4FC3F7",
                },
                LogOptions = new LogOptions
                {
                    IsLogEnabled = true,
                    LogSeverityLevel = LogLevel.Info,
                    LogFilePath = Path.Combine(AppSettings.LogDir, "recorder.log"),
                },
            };
        }

        /// <summary>Ends the recording. Before the first frame this cancels instead.</summary>
        public void Stop()
        {
            if (_recorder == null || _stopRequested) return;
            _stopRequested = true;
            _clock.Stop();
            if (!_hasFrames)
            {
                Log.Info("Gestoppt vor dem ersten Bild: abgebrochen");
                Release();
                _watchdog.Stop();
                TryDelete(_tempPath);
                Failed?.Invoke("Die Aufnahme wurde beendet, bevor das erste Bild aufgenommen war.");
                return;
            }
            _finishing = true;
            try
            {
                _recorder.Stop();
            }
            catch (Exception ex)
            {
                Log.Error("Stop", ex);
                Release();
                _ = FinishAsync();
            }
        }

        public void TogglePause()
        {
            if (!IsRecording || _stopRequested) return;
            try
            {
                if (IsPaused)
                {
                    _recorder.Resume();
                    _clock.Start();
                }
                else
                {
                    _recorder.Pause();
                    _clock.Stop();
                }
                IsPaused = !IsPaused;
            }
            catch (Exception ex)
            {
                Log.Error("Pause", ex);
            }
        }

        private void Watch()
        {
            var r = _recorder;
            if (r == null || _stopRequested) return;
            int frames;
            try { frames = r.CurrentFrameNumber; } catch { frames = 0; }

            if (!_hasFrames && frames > 0)
            {
                _hasFrames = true;
                _clock.Restart();
                Log.Info($"Erstes Bild nach {_sinceBegin.ElapsedMilliseconds} ms ({Attempts[_attempt].Name})");
                Started?.Invoke();
                return;
            }
            if (_hasFrames) return;

            string problem = null;
            if (!_sinceRecording.IsRunning && _sinceBegin.Elapsed > StartTimeout)
                problem = "startet nicht (Zeitüberschreitung)";
            else if (_sinceRecording.IsRunning && _sinceRecording.Elapsed > FirstFrameTimeout)
                problem = "liefert keine Bilder";
            if (problem == null) return;

            _errors.Add($"{Attempts[_attempt].Name}: {problem}");
            Log.Info($"{Attempts[_attempt].Name} {problem}, nächster Versuch");
            Release();
            Retry();
        }

        private void OnStatus(Recorder sender, RecorderStatus status)
        {
            if (sender != _recorder) return;
            if (status == RecorderStatus.Recording && !_sinceRecording.IsRunning) _sinceRecording.Start();
        }

        private void OnComplete(Recorder sender)
        {
            if (sender != _recorder) return;
            Release();
            _ = FinishAsync();
        }

        private void OnFailed(Recorder sender, string error)
        {
            if (sender != _recorder) return;
            error = string.IsNullOrWhiteSpace(error) ? "Unbekannter Fehler" : error.Trim();
            _errors.Add($"{Attempts[_attempt].Name}: {error}");
            Log.Info($"Fehler ({Attempts[_attempt].Name}): {error}");
            Release();
            if (!_hasFrames && !_stopRequested)
            {
                Retry();
                return;
            }
            // Failed after frames were written (e.g. the disk filled up): keep whatever is usable.
            if (File.Exists(_tempPath) && new FileInfo(_tempPath).Length > 0)
            {
                _ = FinishAsync();
                return;
            }
            Fail();
        }

        private void Retry()
        {
            if (NextAttempt()) return;
            Fail();
        }

        private void Fail()
        {
            _watchdog.Stop();
            _finishing = false;
            TryDelete(_tempPath);
            var details = string.Join(Environment.NewLine, _errors);
            Log.Info("Aufnahme fehlgeschlagen:" + Environment.NewLine + details);
            Failed?.Invoke(details);
        }

        /// <summary>Checks the finished file and moves it from the temp folder to its destination.</summary>
        private async Task FinishAsync()
        {
            _watchdog.Stop();
            string temp = _tempPath, target = _request.FilePath;
            try
            {
                if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                    throw new IOException("Die Videodatei ist leer.");
                string final = await Task.Run(() => MoveToTarget(temp, target));
                _finishing = false;
                Log.Info($"Gespeichert: {final} ({new FileInfo(final).Length / 1024} KB, {_clock.Elapsed})");
                Completed?.Invoke(final);
            }
            catch (Exception ex)
            {
                _finishing = false;
                Log.Error("Speichern", ex);
                _errors.Add(ex.Message);
                Failed?.Invoke(ex.Message);
            }
        }

        /// <summary>Moves the file; if the folder refuses (e.g. controlled folder access), keeps it in a folder that works.</summary>
        private static string MoveToTarget(string temp, string target)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                target = Unique(target);
                File.Move(temp, target);
                return target;
            }
            catch (Exception ex)
            {
                Log.Error($"Verschieben nach {target} nicht möglich", ex);
                var fallbackDir = Path.Combine(AppSettings.LogDir, "Aufnahmen");
                Directory.CreateDirectory(fallbackDir);
                var fallback = Unique(Path.Combine(fallbackDir, Path.GetFileName(target)));
                File.Move(temp, fallback);
                return fallback;
            }
        }

        private static string Unique(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                var p = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(p)) return p;
            }
        }

        private void Release()
        {
            var r = _recorder;
            _recorder = null;
            IsPaused = false;
            if (r == null) return;
            try { r.Dispose(); } catch (Exception ex) { Log.Error("Dispose", ex); }
        }

        /// <summary>Leftovers from a crash or an aborted attempt.</summary>
        private static void CleanTempDir()
        {
            try
            {
                foreach (var f in Directory.GetFiles(TempDir))
                {
                    if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-2)) TryDelete(f);
                }
            }
            catch
            {
                // Only housekeeping.
            }
        }

        private static void TryDelete(string path)
        {
            try { if (path != null && File.Exists(path)) File.Delete(path); } catch { /* in use: leave it */ }
        }

        public void Dispose()
        {
            _watchdog.Stop();
            var r = _recorder;
            if (r != null)
            {
                try { r.Stop(); } catch { /* shutting down */ }
            }
            Release();
        }
    }

    /// <summary>ClipCatch's own log (%LOCALAPPDATA%\ClipCatch\clipcatch.log), next to the recorder's.</summary>
    public static class Log
    {
        private static readonly object Gate = new object();

        public static string FilePath => Path.Combine(AppSettings.LogDir, "clipcatch.log");

        public static void Info(string message) => Write(message);

        public static void Error(string message, Exception ex) => Write($"{message}: {ex}");

        private static void Write(string line)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppSettings.LogDir);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > 2_000_000) info.Delete();
                    File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}{Environment.NewLine}");
                }
            }
            catch
            {
                // Logging must never throw.
            }
        }
    }
}
