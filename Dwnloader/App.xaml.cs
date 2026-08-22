using System.IO;
using System.Windows;
using System.Windows.Threading;
using Dwnloader.Core;

namespace Dwnloader;

public partial class App : Application
{
    private SingleInstance? _guard;
    private AppSettings? _settings;
    private History? _history;
    private QueueStore? _queueStore;
    private Session? _session;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 自己テストは画面を出さずに走らせる（移植の突き合わせ用）
        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            int code = SelfTest.Run();
            Shutdown(code);
            return;
        }

        // Python 版との突き合わせ用に、同じ入力への答えを JSON で吐く
        if (e.Args.Any(a => a.Equals("--dump", StringComparison.OrdinalIgnoreCase)))
        {
            int code = Compare.Run();
            Shutdown(code);
            return;
        }

        if (e.Args.Any(a => a.Equals("--speedtest", StringComparison.OrdinalIgnoreCase)))
        {
            _ = Task.Run(() =>
            {
                int code = Compare.RunSpeedTest();
                Dispatcher.Invoke(() => Shutdown(code));
            });
            return;
        }

        // yt-dlp を実際に動かして1本落とす
        int mediaAt = Array.FindIndex(e.Args,
            a => a.Equals("--media", StringComparison.OrdinalIgnoreCase));
        if (mediaAt >= 0 && mediaAt + 1 < e.Args.Length)
        {
            var target = e.Args[mediaAt + 1];
            _ = Task.Run(async () =>
            {
                int code = await Compare.RunMediaAsync(target).ConfigureAwait(false);
                Dispatcher.Invoke(() => Shutdown(code));
            });
            return;
        }

        // 実サーバ・実データで、出荷するコードそのものを通す
        if (e.Args.Any(a => a.Equals("--live", StringComparison.OrdinalIgnoreCase)))
        {
            _ = Task.Run(async () =>
            {
                int code = await Compare.RunLiveAsync().ConfigureAwait(false);
                Dispatcher.Invoke(() => Shutdown(code));
            });
            return;
        }

        // 2つ動くと同じURLを両方が拾って二重にダウンロードしてしまう。
        // 後続の起動は、動いている側を前に出して自分は終了する。
        _guard = new SingleInstance();
        if (!_guard.IsPrimary)
        {
            SingleInstance.Signal();
            Shutdown(0);
            return;
        }

        // 想定外の例外でアプリごと消えないようにする。1件の失敗より、
        // 常駐が死ぬ方が困る。
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        Directory.CreateDirectory(AppPaths.ConfigDir);

        _settings = new AppSettings();
        _history = new History();
        _queueStore = new QueueStore();

        _session = new Session(Dispatcher, _settings, _history, _queueStore);

        _tray = new TrayIcon(
            onShow: () => Dispatcher.Invoke(() => _window?.ShowFromTray()),
            onToggleWatch: () => Dispatcher.Invoke(() => _session?.ToggleWatch()),
            onQuit: () => Dispatcher.Invoke(QuitAll));

        _window = new MainWindow();
        _window.Attach(_session, _tray);
        _window.Closed += (_, _) => QuitAll();

        _guard.Listen(() => Dispatcher.Invoke(() => _window?.ShowFromTray()));

        _window.Show();
    }

    private void QuitAll()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        _session?.Shutdown();
        _session?.Dispose();
        _tray?.Dispose();
        _settings?.Dispose();
        _history?.Dispose();
        _queueStore?.Dispose();
        _guard?.Dispose();

        Shutdown(0);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        e.Handled = true;               // UI スレッドの例外で落とさない
    }

    private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) LogCrash(ex);
    }

    /// <summary>落ちた理由を残す。画面が出せない状況でも後から追える。</summary>
    private static void LogCrash(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            File.AppendAllText(
                Path.Combine(AppPaths.ConfigDir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }
}
