using System.IO;
using System.Windows;
using System.Windows.Threading;
using TisViewer.Services;

namespace TisViewer;

public partial class App : Application
{
    private SingleInstance? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        var args = e.Args;

        if (args.Any(a => a.Equals("--register", StringComparison.OrdinalIgnoreCase)))
        {
            RunAssociationCommand(register: true);
            Shutdown();
            return;
        }

        if (args.Any(a => a.Equals("--unregister", StringComparison.OrdinalIgnoreCase)))
        {
            RunAssociationCommand(register: false);
            Shutdown();
            return;
        }

        var requestedFile = FindArchiveArgument(args);

        _singleInstance = new SingleInstance();

        if (!_singleInstance.IsFirstInstance)
        {
            _singleInstance.TrySend(requestedFile ?? "ACTIVATE");
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (requestedFile is not null)
        {
            window.OpenFile(requestedFile);
        }

        _singleInstance.StartServer(message => Dispatcher.BeginInvoke(() =>
        {
            window.ActivateFromSecondInstance(
                string.Equals(message, "ACTIVATE", StringComparison.Ordinal) ? null : message);
        }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static string? FindArchiveArgument(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (File.Exists(arg) && arg.EndsWith(".tis", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFullPath(arg);
                }
            }
            catch
            {
                // ignored
            }
        }

        return null;
    }

    /// <summary>Command-line helper used by installer scripts.</summary>
    private static void RunAssociationCommand(bool register)
    {
        try
        {
            if (register)
            {
                FileAssociation.Register();
            }
            else
            {
                FileAssociation.Unregister();
            }
        }
        catch (Exception ex)
        {
            LogException(ex);
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);
        MessageBox.Show(
            $"发生了一个意外错误：\n\n{e.Exception.Message}\n\n如果问题持续出现，请查看日志：\n{LogPath}",
            "TIS 相册",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogException(exception);
        }
    }

    private static string LogPath => AppPaths.LogFile;

    private static void LogException(Exception exception)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(directory);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {exception}\n\n");
        }
        catch
        {
            // ignored
        }
    }
}
