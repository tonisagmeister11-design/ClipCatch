using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ClipCatch
{
    /// <summary>
    /// A thin frame just outside the recorded area. Clicks go through it, and it is hidden from the
    /// capture, so it never shows up in the video.
    /// </summary>
    public sealed class RegionFrame : Window
    {
        private const int Thickness = 3;
        private readonly Int32Rect _regionPx;
        private readonly Rectangle _line;

        public RegionFrame(Int32Rect regionOnScreenPx)
        {
            _regionPx = regionOnScreenPx;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = false;
            Focusable = false;
            Title = "ClipCatch – Aufnahmebereich";
            _line = new Rectangle
            {
                Stroke = (Brush)Application.Current.FindResource("Accent"),
                StrokeDashArray = new DoubleCollection { 4, 2 },
                SnapsToDevicePixels = true,
            };
            Content = _line;
            SourceInitialized += (s, e) =>
            {
                NativeMethods.MakeClickThrough(this);
                NativeMethods.ExcludeFromCapture(this);
                Place();
            };
            Loaded += (s, e) =>
            {
                _line.StrokeThickness = Thickness / VisualTreeHelper.GetDpi(this).DpiScaleX;
                Place();
            };
            DpiChanged += (s, e) => Dispatcher.BeginInvoke(new Action(Place));
        }

        private void Place() => NativeMethods.PlaceTopmost(this, new Int32Rect(
            _regionPx.X - Thickness, _regionPx.Y - Thickness, _regionPx.Width + 2 * Thickness, _regionPx.Height + 2 * Thickness));

        public void SetPaused(bool paused) =>
            _line.Stroke = paused ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)) : (Brush)Application.Current.FindResource("Accent");
    }

    /// <summary>3 – 2 – 1 over the selected area. Esc cancels.</summary>
    public sealed class CountdownWindow : Window
    {
        private readonly Int32Rect _regionPx;
        private readonly TextBlock _number;
        private readonly Grid _badge;
        private bool _cancelled;

        public CountdownWindow(Int32Rect regionOnScreenPx)
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
                Text = "Esc = abbrechen", FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255)),
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
            // Give the compositor a frame to remove the countdown before the first captured frame.
            await Task.Delay(60);
            return true;
        }
    }

    /// <summary>The floating bar while recording: time, pause and stop. Hidden from the capture.</summary>
    public sealed class RecordingBar : Window
    {
        private readonly Int32Rect _regionPx;
        private readonly MonitorInfo _monitor;
        private readonly TextBlock _time;
        private readonly TextBlock _pauseIcon;
        private readonly Ellipse _dot;
        private readonly Button _pause;
        private readonly Button _stop;
        private readonly TextBlock _stopText;

        public event Action StopRequested;
        public event Action PauseRequested;

        public RecordingBar(Int32Rect regionOnScreenPx, MonitorInfo monitor, string hotkeyText)
        {
            _regionPx = regionOnScreenPx;
            _monitor = monitor;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowActivated = false;
            Title = "ClipCatch – Aufnahme";
            Foreground = (Brush)Application.Current.FindResource("Text");
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
            // Off-screen until Loaded knows its size and places it next to the area.
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000;
            Top = -32000;

            _dot = new Ellipse { Width = 10, Height = 10, Fill = (Brush)Application.Current.FindResource("Accent"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0) };
            _dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            _time = new TextBlock { Text = "00:00", FontSize = 14, FontFamily = new FontFamily("Consolas, Segoe UI"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), MinWidth = 46 };

            _pauseIcon = new TextBlock { Style = (Style)Application.Current.FindResource("IconText"), Text = "" };
            _pause = new Button { Style = (Style)Application.Current.FindResource("FlatButton"), Content = _pauseIcon, ToolTip = "Pause / Weiter", Width = 34, Height = 32, Padding = new Thickness(0) };
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
            var pill = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1F, 0x1F, 0x26)),
                BorderBrush = (Brush)Application.Current.FindResource("Line"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14), Padding = new Thickness(8, 6, 6, 6), Margin = new Thickness(12),
                Child = row, Cursor = Cursors.SizeAll, ToolTip = "Ziehen zum Verschieben",
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Opacity = 0.5 },
            };
            pill.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            Content = pill;

            SourceInitialized += (s, e) => NativeMethods.ExcludeFromCapture(this);
            Loaded += (s, e) => Place();
        }

        /// <summary>Below the area if there is room on its monitor, else above it, else inside at the bottom.</summary>
        private void Place()
        {
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale);
            var m = _monitor.Bounds;
            int x = _regionPx.X + (_regionPx.Width - w) / 2;
            x = Math.Max(m.X, Math.Min(x, m.X + m.Width - w));
            int y = _regionPx.Y + _regionPx.Height + 4;
            if (y + h > m.Y + m.Height) y = _regionPx.Y - h - 4;
            if (y < m.Y) y = _regionPx.Y + _regionPx.Height - h - 8;
            NativeMethods.PlaceTopmost(this, new Int32Rect(x, y, w, h));
        }

        public void SetTime(TimeSpan t) =>
            _time.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

        public void SetPaused(bool paused)
        {
            _pauseIcon.Text = paused ? "" : "";
            _dot.Fill = paused ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)) : (Brush)Application.Current.FindResource("Accent");
        }

        public void SetFinishing()
        {
            _pause.IsEnabled = false;
            _stop.IsEnabled = false;
            _stopText.Text = "Speichern …";
            _dot.BeginAnimation(OpacityProperty, null);
        }
    }
}
