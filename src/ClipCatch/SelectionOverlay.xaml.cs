using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClipCatch
{
    public sealed class SelectionResult
    {
        public MonitorInfo Monitor { get; set; }
        /// <summary>Physical pixels, relative to the monitor's top-left corner; even width and height.</summary>
        public Int32Rect Region { get; set; }
    }

    /// <summary>Shows a selection overlay on every monitor and returns the chosen area, or null.</summary>
    public static class SelectionSession
    {
        public static Task<SelectionResult> Run(string summary, SelectionResult previous)
        {
            var done = new TaskCompletionSource<SelectionResult>();
            var overlays = new List<SelectionOverlay>();

            void Finish(SelectionResult result)
            {
                foreach (var o in overlays) o.Close();
                overlays.Clear();
                done.TrySetResult(result);
            }

            foreach (var monitor in NativeMethods.GetMonitors())
            {
                var overlay = new SelectionOverlay(monitor, summary);
                overlay.Confirmed += Finish;
                overlay.Cancelled += () => Finish(null);
                overlay.SelectionStarted += sender =>
                {
                    foreach (var o in overlays.Where(o => o != sender)) o.ClearSelection();
                };
                overlays.Add(overlay);
            }

            foreach (var o in overlays) o.Show();
            // Keyboard focus to the screen under the mouse, with the previous area pre-selected there.
            NativeMethods.GetCursorPos(out var cursor);
            var active = overlays.FirstOrDefault(o => o.Contains(cursor.X, cursor.Y)) ?? overlays.FirstOrDefault();
            if (previous != null)
            {
                var same = overlays.FirstOrDefault(o => o.Monitor.DeviceName == previous.Monitor.DeviceName);
                same?.Preselect(previous.Region);
            }
            active?.Activate();
            active?.Focus();
            return done.Task;
        }
    }

    public partial class SelectionOverlay : Window
    {
        private enum Mode { None, Create, Move, Resize }

        private const double HandleSize = 9;
        private const double Grip = 8;
        private const double MinSize = 32;

        public MonitorInfo Monitor { get; }
        public event Action<SelectionResult> Confirmed;
        public event Action Cancelled;
        public event Action<SelectionOverlay> SelectionStarted;

        private Rect? _selection;
        private Mode _mode;
        private Point _anchor;
        private Rect _startRect;
        // Which edges a resize moves: -1 = left/top, 1 = right/bottom, 0 = neither.
        private int _edgeX, _edgeY;
        private readonly List<Rectangle> _handles = new List<Rectangle>();
        private Int32Rect? _pendingPreselect;

        public SelectionOverlay(MonitorInfo monitor, string summary)
        {
            Monitor = monitor;
            InitializeComponent();
            SummaryText.Text = summary;
            for (int i = 0; i < 8; i++)
            {
                var h = new Rectangle
                {
                    Width = HandleSize, Height = HandleSize, Fill = Brushes.White,
                    Stroke = (Brush)FindResource("Accent"), StrokeThickness = 1.5,
                    Visibility = Visibility.Collapsed, IsHitTestVisible = false,
                };
                _handles.Add(h);
                Layer.Children.Insert(1, h);
            }

            SourceInitialized += (s, e) => NativeMethods.PlaceTopmost(this, Monitor.Bounds);
            DpiChanged += (s, e) => Dispatcher.BeginInvoke(new Action(() => NativeMethods.PlaceTopmost(this, Monitor.Bounds)));
            Loaded += (s, e) =>
            {
                NativeMethods.PlaceTopmost(this, Monitor.Bounds);
                if (_pendingPreselect is Int32Rect px) ApplyPreselect(px);
                Redraw();
            };
            SizeChanged += (s, e) => Redraw();
            Root.MouseLeftButtonDown += OnDown;
            Root.MouseMove += OnMove;
            Root.MouseLeftButtonUp += OnUp;
            Root.MouseRightButtonUp += (s, e) => { if (_selection.HasValue) ClearSelection(); else Cancelled?.Invoke(); };
            KeyDown += OnKey;
        }

        public bool Contains(int x, int y) =>
            x >= Monitor.Bounds.X && y >= Monitor.Bounds.Y &&
            x < Monitor.Bounds.X + Monitor.Bounds.Width && y < Monitor.Bounds.Y + Monitor.Bounds.Height;

        private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

        public void Preselect(Int32Rect px)
        {
            if (IsLoaded) ApplyPreselect(px);
            else _pendingPreselect = px;
        }

        private void ApplyPreselect(Int32Rect px)
        {
            _pendingPreselect = null;
            var s = Scale;
            var r = new Rect(px.X / s, px.Y / s, px.Width / s, px.Height / s);
            r.Intersect(new Rect(0, 0, ActualWidth, ActualHeight));
            if (!r.IsEmpty && r.Width >= MinSize && r.Height >= MinSize) _selection = r;
            Redraw();
        }

        public void ClearSelection()
        {
            _selection = null;
            _mode = Mode.None;
            Redraw();
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Cancelled?.Invoke();
                e.Handled = true;
            }
            else if ((e.Key == Key.Enter || e.Key == Key.Space) && _selection.HasValue && _mode == Mode.None)
            {
                Confirm();
                e.Handled = true;
            }
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (IsInToolbar(e)) return;
            var p = e.GetPosition(Root);
            if (e.ClickCount == 2)
            {
                SelectAll();
                return;
            }
            SelectionStarted?.Invoke(this);
            Activate();
            Focus();
            _anchor = p;
            var hit = HitTest(p);
            if (hit.mode == Mode.None)
            {
                _mode = Mode.Create;
                _selection = new Rect(p, p);
            }
            else
            {
                _mode = hit.mode;
                _edgeX = hit.ex;
                _edgeY = hit.ey;
                _startRect = _selection.Value;
            }
            Root.CaptureMouse();
            Redraw();
            e.Handled = true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(Root);
            var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
            p = new Point(Clamp(p.X, 0, bounds.Width), Clamp(p.Y, 0, bounds.Height));
            switch (_mode)
            {
                case Mode.Create:
                    _selection = new Rect(_anchor, p);
                    break;
                case Mode.Move:
                {
                    var dx = Clamp(p.X - _anchor.X, -_startRect.Left, bounds.Width - _startRect.Right);
                    var dy = Clamp(p.Y - _anchor.Y, -_startRect.Top, bounds.Height - _startRect.Bottom);
                    _selection = new Rect(_startRect.X + dx, _startRect.Y + dy, _startRect.Width, _startRect.Height);
                    break;
                }
                case Mode.Resize:
                {
                    double l = _startRect.Left, t = _startRect.Top, r = _startRect.Right, b = _startRect.Bottom;
                    if (_edgeX < 0) l = Math.Min(p.X, r - MinSize);
                    if (_edgeX > 0) r = Math.Max(p.X, l + MinSize);
                    if (_edgeY < 0) t = Math.Min(p.Y, b - MinSize);
                    if (_edgeY > 0) b = Math.Max(p.Y, t + MinSize);
                    _selection = new Rect(new Point(l, t), new Point(r, b));
                    break;
                }
                default:
                    if (!IsInToolbar(e)) Cursor = CursorFor(HitTest(p));
                    return;
            }
            Redraw();
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (_mode == Mode.None) return;
            Root.ReleaseMouseCapture();
            if (_mode == Mode.Create && _selection is Rect r && (r.Width < MinSize || r.Height < MinSize))
            {
                _selection = null;
            }
            _mode = Mode.None;
            Redraw();
        }

        private void SelectAll()
        {
            SelectionStarted?.Invoke(this);
            _mode = Mode.None;
            Root.ReleaseMouseCapture();
            _selection = new Rect(0, 0, ActualWidth, ActualHeight);
            Redraw();
            Activate();
            Focus();
        }

        private bool IsInToolbar(MouseEventArgs e)
        {
            if (Toolbar.Visibility != Visibility.Visible) return false;
            var p = e.GetPosition(Toolbar);
            return p.X >= 0 && p.Y >= 0 && p.X <= Toolbar.ActualWidth && p.Y <= Toolbar.ActualHeight;
        }

        private (Mode mode, int ex, int ey) HitTest(Point p)
        {
            if (!(_selection is Rect r)) return (Mode.None, 0, 0);
            bool nearL = Math.Abs(p.X - r.Left) <= Grip, nearR = Math.Abs(p.X - r.Right) <= Grip;
            bool nearT = Math.Abs(p.Y - r.Top) <= Grip, nearB = Math.Abs(p.Y - r.Bottom) <= Grip;
            bool insideX = p.X >= r.Left - Grip && p.X <= r.Right + Grip;
            bool insideY = p.Y >= r.Top - Grip && p.Y <= r.Bottom + Grip;
            int ex = nearL && insideY ? -1 : nearR && insideY ? 1 : 0;
            int ey = nearT && insideX ? -1 : nearB && insideX ? 1 : 0;
            if (ex != 0 || ey != 0) return (Mode.Resize, ex, ey);
            if (r.Contains(p)) return (Mode.Move, 0, 0);
            return (Mode.None, 0, 0);
        }

        private static Cursor CursorFor((Mode mode, int ex, int ey) hit)
        {
            if (hit.mode == Mode.Move) return Cursors.SizeAll;
            if (hit.mode != Mode.Resize) return Cursors.Cross;
            if (hit.ex != 0 && hit.ey != 0) return hit.ex == hit.ey ? Cursors.SizeNWSE : Cursors.SizeNESW;
            return hit.ex != 0 ? Cursors.SizeWE : Cursors.SizeNS;
        }

        private void Redraw()
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            var full = new RectangleGeometry(new Rect(0, 0, w, h));
            bool has = _selection is Rect r0 && r0.Width > 0 && r0.Height > 0;
            Hint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            if (!has)
            {
                Shade.Data = full;
                Frame.Visibility = SizeLabel.Visibility = Toolbar.Visibility = Visibility.Collapsed;
                foreach (var hd in _handles) hd.Visibility = Visibility.Collapsed;
                return;
            }

            var r = _selection.Value;
            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
            group.Children.Add(full);
            group.Children.Add(new RectangleGeometry(r));
            Shade.Data = group;

            Frame.Visibility = Visibility.Visible;
            Canvas.SetLeft(Frame, r.Left - 1);
            Canvas.SetTop(Frame, r.Top - 1);
            Frame.Width = r.Width + 2;
            Frame.Height = r.Height + 2;

            var points = new[]
            {
                new Point(r.Left, r.Top), new Point(r.Left + r.Width / 2, r.Top), new Point(r.Right, r.Top),
                new Point(r.Right, r.Top + r.Height / 2), new Point(r.Right, r.Bottom),
                new Point(r.Left + r.Width / 2, r.Bottom), new Point(r.Left, r.Bottom), new Point(r.Left, r.Top + r.Height / 2),
            };
            for (int i = 0; i < 8; i++)
            {
                _handles[i].Visibility = Visibility.Visible;
                Canvas.SetLeft(_handles[i], points[i].X - HandleSize / 2);
                Canvas.SetTop(_handles[i], points[i].Y - HandleSize / 2);
            }

            var px = ToPixels(r);
            SizeText.Text = $"{px.Width} × {px.Height}";
            SizeLabel.Visibility = Visibility.Visible;
            var labelSize = Measure(SizeLabel);
            double labelY = r.Top - labelSize.Height - 8;
            if (labelY < 4) labelY = r.Top + 8;
            Canvas.SetLeft(SizeLabel, Clamp(r.Left, 4, w - labelSize.Width - 4));
            Canvas.SetTop(SizeLabel, labelY);

            Toolbar.Visibility = _mode == Mode.None ? Visibility.Visible : Visibility.Collapsed;
            if (Toolbar.Visibility == Visibility.Visible)
            {
                var toolbarSize = Measure(Toolbar);
                double tw = toolbarSize.Width, th = toolbarSize.Height;
                double ty = r.Bottom + 12;
                if (ty + th > h - 8) ty = r.Top - th - 12;
                if (ty < 8) ty = r.Bottom - th - 16;
                Canvas.SetLeft(Toolbar, Clamp(r.Left + (r.Width - tw) / 2, 8, w - tw - 8));
                Canvas.SetTop(Toolbar, Clamp(ty, 8, h - th - 8));
            }
        }

        /// <summary>The selection in physical pixels relative to the monitor, even-sized and inside it.</summary>
        private Int32Rect ToPixels(Rect r)
        {
            var s = Scale;
            var win = NativeMethods.GetWindowRectPx(this);
            int offX = win.X - Monitor.Bounds.X, offY = win.Y - Monitor.Bounds.Y;
            int x = (int)Math.Round(r.Left * s) + offX, y = (int)Math.Round(r.Top * s) + offY;
            int right = (int)Math.Round(r.Right * s) + offX, bottom = (int)Math.Round(r.Bottom * s) + offY;
            x = Math.Max(0, x);
            y = Math.Max(0, y);
            right = Math.Min(Monitor.Bounds.Width, right);
            bottom = Math.Min(Monitor.Bounds.Height, bottom);
            int width = Math.Max(2, (right - x) / 2 * 2), height = Math.Max(2, (bottom - y) / 2 * 2);
            return new Int32Rect(x, y, width, height);
        }

        private void Confirm()
        {
            if (!(_selection is Rect r)) return;
            Confirmed?.Invoke(new SelectionResult { Monitor = Monitor, Region = ToPixels(r) });
        }

        private void Start_Click(object sender, RoutedEventArgs e) => Confirm();
        private void FullScreen_Click(object sender, RoutedEventArgs e) => SelectAll();
        private void Cancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();

        private static Size Measure(UIElement e)
        {
            e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return e.DesiredSize;
        }

        private static double Clamp(double v, double min, double max) => max < min ? min : Math.Max(min, Math.Min(max, v));
    }
}
