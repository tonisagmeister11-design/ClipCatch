using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Shapes;

namespace ClipCatch
{
    /// <summary>
    /// A thin frame just outside the recorded area, made of four strips that lie entirely outside
    /// it, so it can never appear in the video (on every Windows version). Clicks go through it.
    /// </summary>
    public sealed class RegionFrame
    {
        private const int Thickness = 3;
        private readonly List<Window> _strips = new List<Window>();

        public RegionFrame(Int32Rect r)
        {
            int t = Thickness;
            var rects = new[]
            {
                new Int32Rect(r.X - t, r.Y - t, r.Width + 2 * t, t),
                new Int32Rect(r.X - t, r.Y + r.Height, r.Width + 2 * t, t),
                new Int32Rect(r.X - t, r.Y, t, r.Height),
                new Int32Rect(r.X + r.Width, r.Y, t, r.Height),
            };
            foreach (var rect in rects)
            {
                var strip = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = false,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Topmost = true,
                    Focusable = false,
                    Background = (Brush)Application.Current.FindResource("Accent"),
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000, Top = -32000, Width = 1, Height = 1,
                    Title = "ClipCatch – Rahmen",
                };
                var px = rect;
                strip.SourceInitialized += (s, e) =>
                {
                    NativeMethods.MakeClickThrough(strip);
                    NativeMethods.ExcludeFromCapture(strip);
                    NativeMethods.PlaceTopmost(strip, px);
                };
                strip.Loaded += (s, e) => NativeMethods.PlaceTopmost(strip, px);
                strip.DpiChanged += (s, e) => strip.Dispatcher.BeginInvoke(new Action(() => NativeMethods.PlaceTopmost(strip, px)));
                _strips.Add(strip);
            }
        }

        public void Show()
        {
            foreach (var s in _strips) s.Show();
        }

        public void Close()
        {
            foreach (var s in _strips) s.Close();
            _strips.Clear();
        }

        public void SetPaused(bool paused)
        {
            Brush brush = paused ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)) : (Brush)Application.Current.FindResource("Accent");
            foreach (var s in _strips) s.Background = brush;
        }
    }

    /// <summary>3 – 2 – 1 over the selected area. Esc cancels.</summary>
    public sealed class CountdownWindow : Window
    {
        private readonly Int32Rect _regionPx;
        private readonly TextBlock _number;
        private readonly Grid _badge;
        private bool _cancelled;

        public CountdownWindow(Int32Rect regionOnScreenPx, string extraHint = null)
        {
            _regionPx = regionOnScreenPx;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0));
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Title = "ClipCatch – Countdown";
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
            Cursor = Cursors.Arrow;

            _number = new TextBlock
            {
                FontSize = 96, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10),
            };
            var hint = new TextBlock
            {
                Text = string.IsNullOrEmpty(extraHint) ? "Esc = abbrechen" : extraHint + "\nEsc = abbrechen",
                TextAlignment = TextAlignment.Center, FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 30),
            };
            _badge = new Grid
            {
                Width = 190, Height = 190, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
            };
            _badge.Children.Add(new Ellipse { Fill = (Brush)Application.Current.FindResource("RecordGradient"), Opacity = 0.95 });
            _badge.Children.Add(_number);
            var root = new Grid();
            root.Children.Add(_badge);
            root.Children.Add(hint);
            Content = root;

            SourceInitialized += (s, e) =>
            {
                NativeMethods.ExcludeFromCapture(this);
                NativeMethods.PlaceTopmost(this, _regionPx);
            };
            Loaded += (s, e) => NativeMethods.PlaceTopmost(this, _regionPx);
            DpiChanged += (s, e) => Dispatcher.BeginInvoke(new Action(() => NativeMethods.PlaceTopmost(this, _regionPx)));
            KeyDown += (s, e) => { if (e.Key == Key.Escape) { _cancelled = true; Close(); } };
            // Closed while still counting (Alt+F4, Esc): treat as cancelled.
            Closed += (s, e) => { if (_running) _cancelled = true; };
        }

        private bool _running;

        /// <summary>Counts down; false if the user cancelled.</summary>
        public async Task<bool> RunAsync(int seconds = 3)
        {
            _running = true;
            Show();
            Activate();
            Focus();
            for (int i = seconds; i >= 1; i--)
            {
                if (_cancelled) return false;
                _number.Text = i.ToString();
                var pop = new DoubleAnimation(1.25, 1.0, TimeSpan.FromMilliseconds(300)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut } };
                ((ScaleTransform)_badge.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                ((ScaleTransform)_badge.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, pop);
                await Task.Delay(1000);
            }
            if (_cancelled) return false;
            _running = false;
            Close();
            // Let the countdown vanish from the screen before the first captured frame.
            await Task.Delay(200);
            return true;
        }
    }

    /// <summary>
    /// Time, pause and stop while recording. It is placed outside the recorded area: below, above,
    /// beside it or on another monitor. Only if there is no room anywhere (full screen on a single
    /// monitor) it may sit inside, and only when Windows confirms it is hidden from the capture;
    /// otherwise it lives in the taskbar (thumbnail buttons, Ctrl+Shift+R).
    /// </summary>
    public sealed class RecordingBar : Window
    {
        public enum BarMode { Floating, InsideHidden, Taskbar }

        private const int Gap = 10;
        private readonly Int32Rect _regionPx;
        private readonly MonitorInfo _monitor;
        private readonly TextBlock _time;
        private readonly TextBlock _pauseIcon;
        private readonly Ellipse _dot;
        private readonly Button _pause;
        private readonly Button _stop;
        private readonly TextBlock _stopText;
        private readonly ThumbButtonInfo _thumbPause;
        private readonly ThumbButtonInfo _thumbStop;
        private Int32Rect _spot;
        private bool _excluded;
        private bool _started;
        private bool _paused;
        private TimeSpan _shownTime;

        public BarMode Mode { get; private set; }
        public event Action StopRequested;
        public event Action PauseRequested;

        public RecordingBar(Int32Rect regionOnScreenPx, MonitorInfo monitor, string hotkeyText)
        {
            _regionPx = regionOnScreenPx;
            _monitor = monitor;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Title = "ClipCatch – Aufnahme";
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/clipcatch.ico"));
            Foreground = (Brush)Application.Current.FindResource("Text");
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
            Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x26));
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000;
            Top = -32000;

            _dot = new Ellipse { Width = 10, Height = 10, Fill = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0) };
            _time = new TextBlock { Text = "Startet …", FontSize = 14, FontFamily = new FontFamily("Consolas, Segoe UI"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), MinWidth = 70 };

            _pauseIcon = new TextBlock { Style = (Style)Application.Current.FindResource("IconText"), Text = "\uE769" };
            _pause = new Button { Style = (Style)Application.Current.FindResource("FlatButton"), Content = _pauseIcon, ToolTip = "Pause / Weiter", Width = 34, Height = 32, Padding = new Thickness(0), IsEnabled = false };
            _pause.Click += (s, e) => PauseRequested?.Invoke();

            _stopText = new TextBlock { Text = "Stopp", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold };
            var stopContent = new StackPanel { Orientation = Orientation.Horizontal };
            stopContent.Children.Add(new Border { Width = 10, Height = 10, Background = Brushes.White, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            stopContent.Children.Add(_stopText);
            _stop = new Button
            {
                Style = (Style)Application.Current.FindResource("AccentButton"), Content = stopContent, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(4, 0, 0, 0),
                ToolTip = string.IsNullOrEmpty(hotkeyText) ? "Aufnahme beenden" : $"Aufnahme beenden ({hotkeyText})",
            };
            _stop.Click += (s, e) => StopRequested?.Invoke();

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_dot);
            row.Children.Add(_time);
            row.Children.Add(_pause);
            row.Children.Add(_stop);
            var body = new Border
            {
                BorderBrush = (Brush)Application.Current.FindResource("Line"), BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 6, 6), Child = row, Cursor = Cursors.SizeAll, ToolTip = "Ziehen zum Verschieben",
                Background = Brushes.Transparent,
            };
            body.MouseLeftButtonDown += OnDrag;
            Content = body;

            // Taskbar thumbnail buttons, used when the bar has to live in the taskbar.
            _thumbPause = new ThumbButtonInfo { Description = "Pause / Weiter", ImageSource = Glyph(pause: true), IsEnabled = false };
            _thumbPause.Click += (s, e) => PauseRequested?.Invoke();
            _thumbStop = new ThumbButtonInfo { Description = "Aufnahme beenden", ImageSource = Glyph(pause: false) };
            _thumbStop.Click += (s, e) => StopRequested?.Invoke();
            TaskbarItemInfo = new TaskbarItemInfo
            {
                ThumbButtonInfos = new ThumbButtonInfoCollection { _thumbPause, _thumbStop },
                Description = "ClipCatch nimmt auf",
            };

            SourceInitialized += (s, e) => NativeMethods.RoundCorners(this);
            Loaded += (s, e) => { if (Mode != BarMode.Taskbar) Place(); };
            DpiChanged += (s, e) => { if (Mode != BarMode.Taskbar) Dispatcher.BeginInvoke(new Action(Place)); };
            StateChanged += OnStateChanged;
        }

        /// <summary>Decides where the bar goes, before the countdown, so it can say so.</summary>
        public BarMode Prepare()
        {
            NativeMethods.Handle(this);
            _excluded = NativeMethods.ExcludeFromCapture(this);
            if (FindOutsideSpot(out _spot)) Mode = BarMode.Floating;
            else if (_excluded) Mode = BarMode.InsideHidden;
            else Mode = BarMode.Taskbar;
            Log.Info($"Leiste: {Mode} (aus Aufnahme ausgeblendet: {_excluded})");
            return Mode;
        }

        public void ShowForRecording()
        {
            if (Mode == BarMode.Taskbar)
            {
                ShowInTaskbar = true;
                ShowActivated = false;
                WindowState = WindowState.Minimized;
                TaskbarItemInfo.Overlay = Glyph(pause: false, dot: true);
            }
            Show();
        }

        private Size ContentSizeDip()
        {
            var c = (UIElement)Content;
            c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return new Size(Math.Max(c.DesiredSize.Width, 240), Math.Max(c.DesiredSize.Height, 44));
        }

        /// <summary>A spot that does not touch the recorded area (or its frame), on any monitor.</summary>
        private bool FindOutsideSpot(out Int32Rect spot)
        {
            var dip = ContentSizeDip();
            var keepOut = new Int32Rect(_regionPx.X - 4, _regionPx.Y - 4, _regionPx.Width + 8, _regionPx.Height + 8);
            var monitors = NativeMethods.GetMonitors();
            var own = monitors.FirstOrDefault(m => m.DeviceName == _monitor.DeviceName) ?? _monitor;

            // Next to the area on its own monitor: below, above, right, left.
            double scale = NativeMethods.ScaleAt(own.Bounds.X + own.Bounds.Width / 2, own.Bounds.Y + own.Bounds.Height / 2);
            int w = (int)Math.Ceiling(dip.Width * scale), h = (int)Math.Ceiling(dip.Height * scale);
            int cx = _regionPx.X + (_regionPx.Width - w) / 2, cy = _regionPx.Y + (_regionPx.Height - h) / 2;
            var candidates = new[]
            {
                new Int32Rect(cx, keepOut.Y + keepOut.Height + Gap, w, h),
                new Int32Rect(cx, keepOut.Y - Gap - h, w, h),
                new Int32Rect(keepOut.X + keepOut.Width + Gap, cy, w, h),
                new Int32Rect(keepOut.X - Gap - w, cy, w, h),
            };
            foreach (var c in candidates)
            {
                var fitted = FitInto(c, own.WorkArea);
                if (Inside(fitted, own.WorkArea) && !Intersects(fitted, keepOut)) { spot = fitted; return true; }
            }

            // Another monitor: bottom centre of its work area.
            foreach (var m in monitors.Where(m => m.DeviceName != own.DeviceName))
            {
                double s = NativeMethods.ScaleAt(m.Bounds.X + m.Bounds.Width / 2, m.Bounds.Y + m.Bounds.Height / 2);
                int mw = (int)Math.Ceiling(dip.Width * s), mh = (int)Math.Ceiling(dip.Height * s);
                var c = new Int32Rect(m.WorkArea.X + (m.WorkArea.Width - mw) / 2, m.WorkArea.Y + m.WorkArea.Height - mh - 40, mw, mh);
                if (Inside(c, m.WorkArea) && !Intersects(c, keepOut)) { spot = c; return true; }
            }
            spot = default;
            return false;
        }

        private void Place()
        {
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale);
            if (Mode == BarMode.InsideHidden)
            {
                _spot = new Int32Rect(_regionPx.X + (_regionPx.Width - w) / 2, _regionPx.Y + _regionPx.Height - h - 60, w, h);
            }
            else
            {
                // Same anchor, real size (the estimate may differ by a few pixels).
                _spot = new Int32Rect(_spot.X + (_spot.Width - w) / 2, _spot.Y, w, h);
            }
            NativeMethods.PlaceTopmost(this, _spot);
        }

        private void OnDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed || Mode == BarMode.Taskbar) return;
            DragMove();
            // Never let it be dragged into the picture where Windows cannot hide it.
            var now = NativeMethods.GetWindowRectPx(this);
            if (!_excluded && Intersects(now, _regionPx)) NativeMethods.PlaceTopmost(this, _spot);
            else _spot = now;
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            // In the taskbar, a click on the button would restore the window into the picture:
            // keep it minimised and treat the click as "stop".
            if (Mode == BarMode.Taskbar && WindowState != WindowState.Minimized)
            {
                WindowState = WindowState.Minimized;
                StopRequested?.Invoke();
            }
        }

        private static Int32Rect FitInto(Int32Rect r, Int32Rect area)
        {
            int x = Math.Max(area.X, Math.Min(r.X, area.X + area.Width - r.Width));
            return new Int32Rect(x, r.Y, r.Width, r.Height);
        }

        private static bool Inside(Int32Rect r, Int32Rect area) =>
            r.X >= area.X && r.Y >= area.Y && r.X + r.Width <= area.X + area.Width && r.Y + r.Height <= area.Y + area.Height;

        private static bool Intersects(Int32Rect a, Int32Rect b) =>
            a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

        /// <summary>Small white glyphs for the taskbar: pause bars, a stop square, or a red dot.</summary>
        private static ImageSource Glyph(bool pause, bool dot = false)
        {
            var group = new DrawingGroup();
            using (var dc = group.Open())
            {
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 16, 16));
                if (dot) dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xF0, 0x44, 0x5A)), new Pen(Brushes.White, 1.5), new Point(8, 8), 6.5, 6.5);
                else if (pause)
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(3.5, 2.5, 3.5, 11));
                    dc.DrawRectangle(Brushes.White, null, new Rect(9, 2.5, 3.5, 11));
                }
                else dc.DrawRoundedRectangle(Brushes.White, null, new Rect(3, 3, 10, 10), 1.5, 1.5);
            }
            group.Freeze();
            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }

        /// <summary>The first frame is in: the recording really runs.</summary>
        public void SetStarted()
        {
            _started = true;
            _pause.IsEnabled = true;
            _thumbPause.IsEnabled = true;
            _dot.Fill = (Brush)Application.Current.FindResource("Accent");
            _dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            SetTime(TimeSpan.Zero);
        }

        public void SetTime(TimeSpan t)
        {
            if (!_started) return;
            var text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
            if (_shownTime == t && _time.Text == text) return;
            _shownTime = t;
            _time.Text = text;
            Title = (_paused ? "⏸ " : "● ") + text + " – ClipCatch nimmt auf (klicken = beenden)";
        }

        public void SetPaused(bool paused)
        {
            _paused = paused;
            _pauseIcon.Text = paused ? "\uE768" : "\uE769";
            _thumbPause.ImageSource = paused ? Glyph(pause: false, dot: true) : Glyph(pause: true);
            _dot.Fill = paused ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)) : (Brush)Application.Current.FindResource("Accent");
        }

        public void SetFinishing()
        {
            _pause.IsEnabled = false;
            _stop.IsEnabled = false;
            _thumbPause.IsEnabled = false;
            _thumbStop.IsEnabled = false;
            _stopText.Text = "Speichern …";
            _dot.BeginAnimation(OpacityProperty, null);
        }
    }
}
