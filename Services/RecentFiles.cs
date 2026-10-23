using System.IO;
using System.Text.Json;

namespace TisViewer.Services;

public sealed class RecentFileEntry
{
    public string Path { get; set; } = string.Empty;

    public DateTimeOffset OpenedAt { get; set; }
}

public static class RecentFiles
{
    private const int MaxEntries = 12;
    private static readonly object Sync = new();

    private static string StorePath => AppPaths.RecentFile;

    public static IReadOnlyList<RecentFileEntry> Load()
    {
        lock (Sync)
        {
            try
            {
                if (!File.Exists(StorePath))
                {
                    return [];
                }

                var json = File.ReadAllText(StorePath);
                var entries = JsonSerializer.Deserialize<List<RecentFileEntry>>(json, Models.TisJson.Options) ?? [];
                return entries
                    .Where(e => !string.IsNullOrWhiteSpace(e.Path) && File.Exists(e.Path))
                    .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(e => e.OpenedAt).First())
                    .OrderByDescending(e => e.OpenedAt)
                    .Take(MaxEntries)
                    .ToList();
            }
            catch
            {
                return [];
            }
        }
    }

    public static void Add(string path)
    {
        lock (Sync)
        {
            try
            {
                var entries = Load().ToList();
                entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
                entries.Insert(0, new RecentFileEntry { Path = path, OpenedAt = DateTimeOffset.Now });
                Save(entries.Take(MaxEntries).ToList());
            }
            catch
            {
                // ignored
            }
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Save([]);
        }
    }

    public static void Remove(string path)
    {
        lock (Sync)
        {
            var entries = Load().Where(e => !string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
            Save(entries);
        }
    }

    private static void Save(List<RecentFileEntry> entries)
    {
        var directory = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(entries, Models.TisJson.Options);
        File.WriteAllText(StorePath, json);
    }
}
