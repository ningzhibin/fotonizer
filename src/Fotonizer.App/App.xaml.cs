using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Fotonizer.Core;
using Fotonizer.Core.Logging;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Fotonizer.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private bool _ownsMutex;
    private AppHost? _host;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _exiting;

    public AppHost? Host => _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SetupExceptionHandling();

        _instanceMutex = new Mutex(initiallyOwned: true, @"Local\Fotonizer.App.SingleInstance.v1", out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            try
            {
                EventWaitHandle.OpenExisting(@"Local\Fotonizer.App.ShowWindow.v1").Set();
            }
            catch
            {
            }

            Shutdown();
            return;
        }

        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Fotonizer.App.ShowWindow.v1");
        ThreadPool.RegisterWaitForSingleObject(_showWindowEvent,
            (_, _) => Dispatcher.BeginInvoke(new Action(ShowMainWindow)), null, Timeout.Infinite, false);

        try
        {
            var dataDirectory = ReadArgument(e.Args, "--data");
            AppHostOptions options;
            if (!string.IsNullOrWhiteSpace(dataDirectory))
            {
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataDirectory));
                options = new AppHostOptions
                {
                    DataDirectory = full,
                    SettingsPath = Path.Combine(full, "settings.json"),
                    DatabasePath = Path.Combine(full, "library.db"),
                    SecretsPath = Path.Combine(full, "secrets.bin")
                };
            }
            else
            {
                options = new AppHostOptions();
            }

            _host = new AppHost(options);
            await _host.StartAsync();
            Loc.SetLanguage(_host.SettingsStore.Current.Language);
        }
        catch (Exception ex)
        {
            AppLog.Shared.Error("启动失败", ex);
            MessageBox.Show(Loc.T("Fotonizer 启动失败：", "Fotonizer failed to start:") + "\n" + ex.Message, "Fotonizer",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _window = new MainWindow(_host);
        _tray = new TrayIcon(_host, _window);

        var startMinimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

        if (!_host.SettingsStore.Current.HasCompletedSetup)
        {
            _window.Show();
            var firstRun = new FirstRunWindow(_host) { Owner = _window };
            if (firstRun.ShowDialog() != true)
            {
                await ExitApplicationAsync();
                return;
            }

            _window.RefreshAfterSetup();
            _host.Monitor.Start();

            if (startMinimized)
            {
                _window.Hide();
                _tray.ShowStartedBalloon();
            }
            else
            {
                _window.ShowFromTray();
            }

            return;
        }

        if (startMinimized)
            _tray.ShowStartedBalloon();
        else
            ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_window == null) return;
        _window.ShowFromTray();
    }

    private static string? ReadArgument(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    public async Task ExitApplicationAsync()
    {
        if (_exiting) return;
        _exiting = true;

        _window?.PrepareForExit();
        _window?.FlushSettings();
        _tray?.Dispose();
        _tray = null;

        if (_host != null)
        {
            try
            {
                await _host.DisposeAsync();
            }
            catch (Exception ex)
            {
                AppLog.Shared.Warn($"停止服务时出错：{ex.Message}");
            }

            _host = null;
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _tray?.Dispose();
        }
        catch
        {
        }

        try
        {
            if (_host != null)
                Task.Run(async () => await _host.DisposeAsync()).Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        _showWindowEvent?.Dispose();

        if (_ownsMutex)
        {
            try
            {
                _instanceMutex?.ReleaseMutex();
            }
            catch
            {
            }
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void SetupExceptionHandling()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Shared.Error("界面线程未处理异常", args.Exception);
            MessageBox.Show(Loc.T("发生了一个未处理的错误：", "An unhandled error occurred:") + "\n" +
                            args.Exception.Message +
                            "\n\n" + Loc.T("详细信息已写入日志。", "Details have been written to the log."),
                "Fotonizer", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLog.Shared.Error("未处理异常", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Shared.Warn("后台任务异常：" + args.Exception.GetBaseException().Message);
            args.SetObserved();
        };
    }
}
