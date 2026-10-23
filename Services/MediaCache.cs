using System.IO;
using System.Security.Cryptography;
using System.Text;
using TisViewer.Models;

namespace TisViewer.Services;

public sealed class MediaCache
{
    private readonly string _root;

    public MediaCache()
    {
        _root = AppPaths.CacheDirectory;
        Directory.CreateDirectory(_root);
    }

    public string GetCachePath(TisArchive archive, TisMediaItem item)
    {
        var info = new FileInfo(archive.FilePath);
        var fingerprint = $"{archive.FilePath.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{item.EntryKey}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..20].ToLowerInvariant();
        var name = SanitizeFileName(item.FileName);
        return Path.Combine(_root, hash, name);
    }

    public async Task<string> EnsureAsync(TisArchive archive, TisMediaItem item, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var path = GetCachePath(archive, item);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        if (File.Exists(path))
        {
            var existing = new FileInfo(path);
            if (item.Size <= 0 || existing.Length == item.Size)
            {
                return path;
            }

            TryDelete(path);
        }

        var tempPath = path + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
        try
        {
            await archive.ExtractToFileAsync(item, tempPath, progress, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, path, true);
            return path;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public void CleanupOldCache(TimeSpan maxAge)
    {
        try
        {
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    {
                        Directory.Delete(directory, true);
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(invalid.Contains(c) || c is '#' or '%' ? '_' : c);
        }

        var result = builder.ToString();
        if (result.Length > 90)
        {
            var extension = Path.GetExtension(result);
            result = result[..(90 - extension.Length)] + extension;
        }

        return string.IsNullOrWhiteSpace(result) ? "media" : result;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignored
        }
    }
}
