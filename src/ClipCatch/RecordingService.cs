using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        public string FilePath { get; set; }
    }

    /// <summary>
    /// Records one region to an H.264/AAC MP4 with a constant frame rate, the format editors such as
    /// Premiere Pro import without conversion. Events arrive on the UI thread.
    /// </summary>
    public sealed class RecordingService : IDisposable
    {
        private readonly Dispatcher _ui;
        private Recorder _recorder;
        private RecordingRequest _request;
        private RecorderApi _api;
        private bool _fallbackTried;
        private bool _stopRequested;
        private readonly Stopwatch _clock = new Stopwatch();

        public event Action Started;
        public event Action<string> Completed;
        public event Action<string> Failed;
        public event Action<RecorderStatus> StatusChanged;

        public RecordingService(Dispatcher ui) => _ui = ui;

        public bool IsActive => _recorder != null;
        public bool IsPaused { get; private set; }
        public TimeSpan Elapsed => _clock.Elapsed;

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
                // Portrait regions: the chosen size is the long side's partner, like phone video.
                double scale = w >= h ? targetHeight / h : targetHeight / w;
                w *= scale;
                h *= scale;
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
            return (int)Math.Max(2_000_000, Math.Min(bits, 120_000_000));
        }

        public void Start(RecordingRequest request)
        {
            if (_recorder != null) throw new InvalidOperationException("Es läuft bereits eine Aufnahme.");
            _request = request;
            _fallbackTried = false;
            _stopRequested = false;
            IsPaused = false;
            _clock.Reset();
            Directory.CreateDirectory(Path.GetDirectoryName(request.FilePath));
            // Windows Graphics Capture handles laptops with two graphics chips; Desktop Duplication
            // is the fallback if it fails to start.
            Begin(RecorderApi.WindowsGraphicsCapture);
        }

        private void Begin(RecorderApi api)
        {
            _api = api;
            var options = BuildOptions(_request, api);
            _recorder = Recorder.CreateRecorder(options);
            _recorder.OnRecordingComplete += (s, e) => _ui.BeginInvoke(new Action(() => OnComplete(e.FilePath)));
            _recorder.OnRecordingFailed += (s, e) => _ui.BeginInvoke(new Action(() => OnFailed(e.Error)));
            _recorder.OnStatusChanged += (s, e) => _ui.BeginInvoke(new Action(() => OnStatus(e.Status)));
            _recorder.Record(_request.FilePath);
        }

        private static RecorderOptions BuildOptions(RecordingRequest r, RecorderApi api)
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
            if (s.SystemAudio && LoopbackAudioSource.Default is LoopbackAudioSource speakers) audioSources.Add(speakers);
            if (s.Microphone && CaptureAudioSource.Default is CaptureAudioSource mic) audioSources.Add(mic);

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
                    IsHardwareEncodingEnabled = true,
                    IsLowLatencyEnabled = false,
                    // A plain MP4 with the index at the start: opens everywhere, Premiere included.
                    IsFragmentedMp4Enabled = false,
                    IsMp4FastStartEnabled = true,
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

        public void Stop()
        {
            if (_recorder == null) return;
            _stopRequested = true;
            _clock.Stop();
            _recorder.Stop();
        }

        public void TogglePause()
        {
            if (_recorder == null) return;
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

        private void OnStatus(RecorderStatus status)
        {
            if (status == RecorderStatus.Recording && !_clock.IsRunning && !IsPaused && !_stopRequested)
            {
                if (_clock.Elapsed == TimeSpan.Zero) Started?.Invoke();
                _clock.Start();
            }
            StatusChanged?.Invoke(status);
        }

        private void OnComplete(string path)
        {
            Release();
            Completed?.Invoke(path ?? _request.FilePath);
        }

        private void OnFailed(string error)
        {
            bool nothingRecorded = _clock.Elapsed < TimeSpan.FromSeconds(1) && !_stopRequested;
            Release();
            if (nothingRecorded && !_fallbackTried && _api == RecorderApi.WindowsGraphicsCapture)
            {
                _fallbackTried = true;
                TryDelete(_request.FilePath);
                try
                {
                    Begin(RecorderApi.DesktopDuplication);
                    return;
                }
                catch (Exception ex)
                {
                    Release();
                    error = error + Environment.NewLine + ex.Message;
                }
            }
            Failed?.Invoke(string.IsNullOrWhiteSpace(error) ? "Unbekannter Fehler" : error);
        }

        private void Release()
        {
            var r = _recorder;
            _recorder = null;
            IsPaused = false;
            _clock.Stop();
            try { r?.Dispose(); } catch { /* already gone */ }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* in use: leave it */ }
        }

        public void Dispose()
        {
            if (_recorder != null)
            {
                try { _recorder.Stop(); } catch { /* shutting down */ }
            }
            Release();
        }
    }
}
