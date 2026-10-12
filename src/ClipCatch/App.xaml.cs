using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace ClipCatch
{
    public partial class App : Application
    {
        private const string InstanceName = "ClipCatch.SingleInstance.7b1f0c2e";
        private Mutex _instance;
        private EventWaitHandle _activate;

        protected override void OnStartup(StartupEventArgs e)
        {
            // One ClipCatch at a time: a second start brings the running one to the front.
            _instance = new Mutex(true, InstanceName, out bool first);
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".activate");
            if (!first)
            {
                _activate.Set();
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (s, args) =>
            {
                Log(args.Exception);
                MessageBox.Show("Ein unerwarteter Fehler ist aufgetreten:\n\n" + args.Exception.Message,
                    "ClipCatch", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            base.OnStartup(e);
            var window = new MainWindow();
            MainWindow = window;
            window.Show();

            var listener = new Thread(() =>
            {
                while (_activate.WaitOne())
                {
                    Dispatcher.BeginInvoke(new Action(window.BringToFront));
                }
            }) { IsBackground = true, Name = "ClipCatch activation" };
            listener.Start();
        }

        public static void Log(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.LogDir);
                File.AppendAllText(Path.Combine(AppSettings.LogDir, "clipcatch.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never throw.
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _instance?.Dispose();
            base.OnExit(e);
        }
    }
}
