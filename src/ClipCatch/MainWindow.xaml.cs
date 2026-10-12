using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClipCatch
{
    public sealed class RecordingEntry : INotifyPropertyChanged
    {
        private bool _isNew;
        public string FilePath { get; set; }
        public string Name => System.IO.Path.GetFileNameWithoutExtension(FilePath);
        public string Details { get; set; }
        public bool IsNew
        {
            get => _isNew;
            set { _isNew = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNew))); }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class MainWindow : Window
    {
        private enum State { Idle, Selecting, Countdown, Recording, Finishing }

        private const int HotkeyId = 0xC11C;
        private const string HotkeyText = "Strg+Umschalt+R";

        private readonly AppSettings _settings = AppSettings.Load();
        private readonly ObservableCollection<RecordingEntry> _recordings = new ObservableCollection<RecordingEntry>();
        private readonly RecordingService _service;
        private readonly DispatcherTimer _tick;
        private readonly DispatcherTimer _toastTimer;
        private State _state = State.Idle;
        private SelectionResult _lastSelection;
        private RegionFrame _frame;
        private RecordingBar _bar;
        private bool _hotkeyRegistered;
        private bool _loading;
        private Point _dragStart;
        private RecordingEntry _dragItem;

        public MainWindow()
        {
            InitializeComponent();
            // Fit small laptop screens (e.g. 1366 × 768 at 125 %); the content scrolls if needed.
            Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 24));
            RecordingsList.ItemsSource = _recordings;
            _recordings.CollectionChanged += (s, e) => UpdateEmptyState();

            _service = new RecordingService(Dispatcher);
            _service.Completed += OnRecordingCompleted;
            _service.Failed += OnRecordingFailed;

            _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _tick.Tick += (s, e) => _bar?.SetTime(_service.Elapsed);
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _toastTimer.Tick += (s, e) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };

            LoadSettingsIntoUi();
            RefreshRecordings();

            SourceInitialized += (s, e) => RegisterHotkey();
            Closing += OnClosing;
            Activated += (s, e) => RefreshRecordingsIfChanged();
        }

        // ---- settings -------------------------------------------------------------------------

        private void LoadSettingsIntoUi()
        {
            _loading = true;
            (_settings.Fps == 30 ? Fps30 : Fps60).IsChecked = true;
            Check(ResolutionPanel, _settings.Resolution.ToString());
            Check(QualityPanel, _settings.Quality.ToString());
            SystemAudioBox.IsChecked = _settings.SystemAudio;
            MicBox.IsChecked = _settings.Microphone;
            CursorBox.IsChecked = _settings.ShowCursor;
            ClicksBox.IsChecked = _settings.ShowClicks;
            CountdownBox.IsChecked = _settings.Countdown;
            FolderText.Text = _settings.EffectiveOutputFolder;
            FolderText.ToolTip = _settings.EffectiveOutputFolder;
            _loading = false;
        }

        private static void Check(Panel panel, string tag)
        {
            foreach (var rb in panel.Children.OfType<RadioButton>())
                rb.IsChecked = (string)rb.Tag == tag;
        }

        private void Fps_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _settings.Fps = int.Parse((string)((RadioButton)sender).Tag);
            _settings.Save();
        }

        private void Resolution_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _settings.Resolution = (OutputResolution)Enum.Parse(typeof(OutputResolution), (string)((RadioButton)sender).Tag);
            _settings.Save();
        }

        private void Quality_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _settings.Quality = (VideoQuality)Enum.Parse(typeof(VideoQuality), (string)((RadioButton)sender).Tag);
            _settings.Save();
        }

        private void Toggle_Click(object sender, RoutedEventArgs e)
        {
            _settings.SystemAudio = SystemAudioBox.IsChecked == true;
            _settings.Microphone = MicBox.IsChecked == true;
            _settings.ShowCursor = CursorBox.IsChecked == true;
            _settings.ShowClicks = ClicksBox.IsChecked == true;
            _settings.Countdown = CountdownBox.IsChecked == true;
            _settings.Save();
        }

        private void ChangeFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var folder = FolderPicker.Pick(new WindowInteropHelper(this).Handle, "Speicherort für Aufnahmen", _settings.EffectiveOutputFolder);
                if (string.IsNullOrEmpty(folder)) return;
                _settings.OutputFolder = folder;
                _settings.Save();
                LoadSettingsIntoUi();
                RefreshRecordings();
            }
            catch (Exception ex)
            {
                App.Log(ex);
                MessageBox.Show(this, "Der Ordner konnte nicht gewählt werden:\n" + ex.Message, "ClipCatch", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private string Summary()
        {
            string res;
            switch (_settings.Resolution)
            {
                case OutputResolution.P720: res = "720p"; break;
                case OutputResolution.P1080: res = "1080p"; break;
                case OutputResolution.P1440: res = "1440p"; break;
                case OutputResolution.P2160: res = "4K"; break;
                default: res = "Original"; break;
            }
            string audio = _settings.SystemAudio && _settings.Microphone ? "Ton + Mikro"
                : _settings.SystemAudio ? "mit Ton" : _settings.Microphone ? "Mikro" : "ohne Ton";
            return $"{_settings.Fps} FPS · {res} · {audio}";
        }

        // ---- hotkey ---------------------------------------------------------------------------

        private void RegisterHotkey()
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);
            _hotkeyRegistered = NativeMethods.RegisterHotKey(handle, HotkeyId,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(Key.R));
            HotkeyHint.Text = _hotkeyRegistered
                ? $"{HotkeyText} startet und beendet die Aufnahme – auch wenn ClipCatch im Hintergrund ist"
                : "Tipp: Die Aufnahme lässt sich über die Leiste unter dem Bereich beenden";
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                if (_state == State.Idle) StartFlow();
                else if (_state == State.Recording) StopRecording();
            }
            return IntPtr.Zero;
        }

        // ---- recording flow -------------------------------------------------------------------

        private void Record_Click(object sender, RoutedEventArgs e) => StartFlow();

        private async void StartFlow()
        {
            if (_state != State.Idle) return;
            _state = State.Selecting;
            Hide();
            // Let the main window disappear before the screen is dimmed.
            await Task.Delay(180);

            SelectionResult selection;
            try
            {
                selection = await SelectionSession.Run(Summary(), _lastSelection);
            }
            catch (Exception ex)
            {
                App.Log(ex);
                selection = null;
            }
            if (selection == null)
            {
                BackToIdle();
                return;
            }
            _lastSelection = selection;

            var m = selection.Monitor.Bounds;
            var onScreen = new Int32Rect(m.X + selection.Region.X, m.Y + selection.Region.Y, selection.Region.Width, selection.Region.Height);
            _frame = new RegionFrame(onScreen);
            _frame.Show();

            if (_settings.Countdown)
            {
                _state = State.Countdown;
                bool go = await new CountdownWindow(onScreen).RunAsync();
                if (!go)
                {
                    CloseRecordingWindows();
                    BackToIdle();
                    return;
                }
            }

            var path = NewFilePath();
            try
            {
                _service.Start(new RecordingRequest
                {
                    Monitor = selection.Monitor,
                    Region = selection.Region,
                    Settings = _settings,
                    FilePath = path,
                });
            }
            catch (Exception ex)
            {
                App.Log(ex);
                CloseRecordingWindows();
                BackToIdle();
                ShowError("Die Aufnahme konnte nicht gestartet werden.", ex.Message);
                return;
            }

            _state = State.Recording;
            _bar = new RecordingBar(onScreen, selection.Monitor, _hotkeyRegistered ? HotkeyText : null);
            _bar.StopRequested += StopRecording;
            _bar.PauseRequested += () =>
            {
                if (_state != State.Recording) return;
                _service.TogglePause();
                _bar.SetPaused(_service.IsPaused);
                _frame?.SetPaused(_service.IsPaused);
            };
            _bar.Show();
            _tick.Start();
        }

        private void StopRecording()
        {
            if (_state != State.Recording) return;
            _state = State.Finishing;
            _bar?.SetFinishing();
            _service.Stop();
        }

        private void OnRecordingCompleted(string path)
        {
            var elapsed = _service.Elapsed;
            CloseRecordingWindows();
            BackToIdle();
            RefreshRecordings();
            var entry = _recordings.FirstOrDefault(r => string.Equals(r.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (entry != null)
            {
                entry.IsNew = true;
                RecordingsList.SelectedItem = entry;
                RecordingsList.ScrollIntoView(entry);
            }
            ShowToast($"Aufnahme gespeichert ({Format.Duration(elapsed)}) – zum Schneiden einfach in Premiere ziehen");
        }

        private void OnRecordingFailed(string error)
        {
            CloseRecordingWindows();
            BackToIdle();
            RefreshRecordings();
            ShowError("Die Aufnahme ist fehlgeschlagen.", error);
        }

        private void CloseRecordingWindows()
        {
            _tick.Stop();
            _bar?.Close();
            _bar = null;
            _frame?.Close();
            _frame = null;
        }

        private void BackToIdle()
        {
            _state = State.Idle;
            BringToFront();
        }

        public void BringToFront()
        {
            if (_state != State.Idle) return;
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            NativeMethods.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        }

        private string NewFilePath()
        {
            var folder = _settings.EffectiveOutputFolder;
            Directory.CreateDirectory(folder);
            var name = $"Aufnahme {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
            var path = Path.Combine(folder, name + ".mp4");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name} ({i}).mp4");
            return path;
        }

        private void ShowError(string message, string detail)
        {
            var log = Path.Combine(AppSettings.LogDir, "recorder.log");
            MessageBox.Show(this,
                $"{message}\n\n{detail}\n\nDetails stehen im Protokoll:\n{log}",
                "ClipCatch", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void ShowToast(string text)
        {
            ToastText.Text = text;
            Toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        // ---- recordings list ------------------------------------------------------------------

        private string _listedFolder;
        private DateTime _listedStamp;

        private void RefreshRecordingsIfChanged()
        {
            if (_state != State.Idle) return;
            var folder = _settings.EffectiveOutputFolder;
            var stamp = Directory.Exists(folder) ? Directory.GetLastWriteTimeUtc(folder) : DateTime.MinValue;
            if (folder != _listedFolder || stamp != _listedStamp) RefreshRecordings();
        }

        private void RefreshRecordings()
        {
            var folder = _settings.EffectiveOutputFolder;
            var newOnes = _recordings.Where(r => r.IsNew).Select(r => r.FilePath).ToList();
            _recordings.Clear();
            _listedFolder = folder;
            _listedStamp = Directory.Exists(folder) ? Directory.GetLastWriteTimeUtc(folder) : DateTime.MinValue;
            if (Directory.Exists(folder))
            {
                var files = new DirectoryInfo(folder).GetFiles("*.mp4")
                    .OrderByDescending(f => f.CreationTimeUtc).Take(50);
                foreach (var f in files)
                {
                    var duration = Mp4Info.Duration(f.FullName);
                    var details = $"{f.CreationTime:dd.MM.yyyy HH:mm}  ·  {Format.Size(f.Length)}";
                    if (duration.HasValue) details = $"{Format.Duration(duration.Value)}  ·  " + details;
                    _recordings.Add(new RecordingEntry
                    {
                        FilePath = f.FullName,
                        Details = details,
                        IsNew = newOnes.Contains(f.FullName),
                    });
                }
            }
            UpdateEmptyState();
        }

        private void UpdateEmptyState() =>
            EmptyState.Visibility = _recordings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private static RecordingEntry EntryOf(object sender) => (sender as FrameworkElement)?.Tag as RecordingEntry;

        private void Play_Click(object sender, RoutedEventArgs e) => Open(EntryOf(sender)?.FilePath);

        private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ItemUnder(e.OriginalSource as DependencyObject) is RecordingEntry entry && !(IsInButton(e.OriginalSource as DependencyObject)))
                Open(entry.FilePath);
        }

        private void Open(string path)
        {
            if (path == null || !File.Exists(path)) return;
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Die Datei konnte nicht geöffnet werden:\n" + ex.Message, "ClipCatch");
            }
        }

        private void Reveal_Click(object sender, RoutedEventArgs e)
        {
            var path = EntryOf(sender)?.FilePath;
            if (path != null && File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = _settings.EffectiveOutputFolder;
            Directory.CreateDirectory(folder);
            Process.Start("explorer.exe", $"\"{folder}\"");
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            var entry = EntryOf(sender);
            if (entry == null || !File.Exists(entry.FilePath)) return;
            var dialog = new SaveFileDialog
            {
                Title = "Aufnahme speichern unter",
                FileName = Path.GetFileName(entry.FilePath),
                Filter = "MP4-Video (*.mp4)|*.mp4",
                DefaultExt = ".mp4",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            };
            if (dialog.ShowDialog(this) != true) return;
            if (string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(entry.FilePath), StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                File.Copy(entry.FilePath, dialog.FileName, overwrite: true);
                ShowToast("Gespeichert: " + dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Speichern fehlgeschlagen:\n" + ex.Message, "ClipCatch", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            var entry = EntryOf(sender);
            if (entry == null || !File.Exists(entry.FilePath)) return;
            var files = new System.Collections.Specialized.StringCollection { entry.FilePath };
            Clipboard.SetFileDropList(files);
            ShowToast("Datei kopiert – mit Strg+V im Explorer oder in Premiere einfügen");
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var entry = EntryOf(sender);
            if (entry == null) return;
            if (MessageBox.Show(this, $"„{entry.Name}“ in den Papierkorb verschieben?", "ClipCatch",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(entry.FilePath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                _recordings.Remove(entry);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Löschen fehlgeschlagen:\n" + ex.Message, "ClipCatch", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Drag a recording out of the list into Premiere, the Explorer or any other program.
        private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            _dragItem = IsInButton(source) ? null : ItemUnder(source);
            _dragStart = e.GetPosition(RecordingsList);
        }

        private void List_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(RecordingsList);
            if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var item = _dragItem;
            _dragItem = null;
            if (!File.Exists(item.FilePath)) return;
            var data = new DataObject(DataFormats.FileDrop, new[] { item.FilePath });
            DragDrop.DoDragDrop(RecordingsList, data, DragDropEffects.Copy);
        }

        private RecordingEntry ItemUnder(DependencyObject d)
        {
            while (d != null && !(d is ListBoxItem)) d = VisualTreeHelper.GetParent(d);
            return (d as ListBoxItem)?.DataContext as RecordingEntry;
        }

        private static bool IsInButton(DependencyObject d)
        {
            while (d != null && !(d is ListBoxItem))
            {
                if (d is ButtonBase) return true;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        // ---- window ---------------------------------------------------------------------------

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (_state == State.Recording || _state == State.Finishing)
            {
                var answer = MessageBox.Show(this, "Es läuft gerade eine Aufnahme. Beenden und speichern?", "ClipCatch",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _service.Stop();
            }
            if (_hotkeyRegistered) NativeMethods.UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
            _service.Dispose();
            CloseRecordingWindows();
        }
    }
}
