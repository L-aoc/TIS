using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TisViewer.Services;

public static class FileAssociation
{
    public const string ProgId = "TisViewer.TisFile";
    private const string Extension = ".tis";

    public static string ExecutablePath =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? Path.Combine(AppContext.BaseDirectory, "TisViewer.exe");

    public static bool IsRegistered()
    {
        try
        {
            var progId = Registry.GetValue($@"HKEY_CURRENT_USER\Software\Classes\{Extension}", string.Empty, null) as string;
            var command = Registry.GetValue($@"HKEY_CURRENT_USER\Software\Classes\{ProgId}\shell\open\command", string.Empty, null) as string;
            if (!string.Equals(progId, ProgId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            return command.Contains(Path.GetFileName(ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void Register()
    {
        var exe = ExecutablePath;
        var command = $"\"{exe}\" \"%1\"";

        using (var extensionKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Extension}"))
        {
            extensionKey.SetValue(string.Empty, ProgId);
            extensionKey.SetValue("Content Type", "application/x-tis");
        }

        using (var progIdKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progIdKey.SetValue(string.Empty, "TIS 相册文件");
            progIdKey.SetValue("FriendlyTypeName", "TIS 相册文件");
            using (var iconKey = progIdKey.CreateSubKey("DefaultIcon"))
            {
                iconKey.SetValue(string.Empty, $"\"{exe}\",0");
            }

            using (var openKey = progIdKey.CreateSubKey(@"shell\open"))
            {
                openKey.SetValue(string.Empty, "打开");
                using var commandKey = openKey.CreateSubKey("command");
                commandKey.SetValue(string.Empty, command);
            }
        }

        using (var openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Extension}\OpenWithProgids"))
        {
            openWith.SetValue(ProgId, string.Empty);
        }

        using (var applicationKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{Path.GetFileName(exe)}"))
        {
            applicationKey.SetValue("FriendlyAppName", "TIS 相册");
            using (var supported = applicationKey.CreateSubKey("SupportedTypes"))
            {
                supported.SetValue(Extension, string.Empty);
            }

            using var commandKey = applicationKey.CreateSubKey(@"shell\open\command");
            commandKey.SetValue(string.Empty, command);
        }

        NotifyShell();
    }

    public static void Unregister()
    {
        try
        {
            using (var extensionKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Extension}", writable: true))
            {
                if (extensionKey is not null &&
                    string.Equals(extensionKey.GetValue(string.Empty) as string, ProgId, StringComparison.OrdinalIgnoreCase))
                {
                    extensionKey.DeleteValue(string.Empty, throwOnMissingValue: false);
                }
            }

            using (var openWith = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Extension}\OpenWithProgids", writable: true))
            {
                openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
            }

            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"Software\Classes\Applications\{Path.GetFileName(ExecutablePath)}",
                throwOnMissingSubKey: false);
        }
        catch
        {
            // ignored
        }

        NotifyShell();
    }

    private static void NotifyShell()
    {
        try
        {
            SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // ignored
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
