using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace TisViewer.Models;

public enum TisMediaKind
{
    Unknown,
    Image,
    Video,
}

public static class TisMediaTypes
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff",
        ".webp", ".heic", ".heif", ".avif", ".ico",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg",
        ".mpe", ".ts", ".m2ts", ".mts", ".3gp", ".flv", ".ogv", ".asf", ".rmvb",
    };

    public static TisMediaKind Detect(string fileNameOrPath)
    {
        var ext = Path.GetExtension(fileNameOrPath);
        if (string.IsNullOrEmpty(ext))
        {
            return TisMediaKind.Unknown;
        }

        if (ImageExtensions.Contains(ext))
        {
            return TisMediaKind.Image;
        }

        return VideoExtensions.Contains(ext) ? TisMediaKind.Video : TisMediaKind.Unknown;
    }

    public static bool IsSupportedMedia(string path) => Detect(path) != TisMediaKind.Unknown;

    public static string ToManifestValue(TisMediaKind kind) => kind switch
    {
        TisMediaKind.Video => "video",
        TisMediaKind.Image => "image",
        _ => "unknown",
    };

    public static TisMediaKind ParseManifestValue(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "video" => TisMediaKind.Video,
        "image" => TisMediaKind.Image,
        _ => TisMediaKind.Unknown,
    };
}

public sealed class TisMediaItem
{
    public TisMediaItem(string entryKey, string displayName, TisMediaKind kind, long size, DateTime? modified)
    {
        EntryKey = entryKey;
        DisplayName = displayName;
        Kind = kind;
        Size = size;
        Modified = modified;
    }

    public string EntryKey { get; }

    public string DisplayName { get; }

    public string FileName => Path.GetFileName(EntryKey.Replace('\\', '/'));

    public string Extension => Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant();

    public TisMediaKind Kind { get; }

    public long Size { get; }

    public DateTime? Modified { get; }

    public string KindLabel => Kind switch
    {
        TisMediaKind.Video => "视频",
        TisMediaKind.Image => "图片",
        _ => "文件",
    };

    public string SizeLabel => FormatSize(Size);

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1024 && unit < units.Length - 1);

        return value >= 100 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
}

public sealed class TisManifest
{
    public string Format { get; set; } = "tis";

    public int Version { get; set; } = 1;

    public string? Title { get; set; }

    public string? Cover { get; set; }

    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;

    public List<TisManifestItem> Items { get; set; } = [];
}

public sealed class TisManifestItem
{
    public string Entry { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Kind { get; set; } = "image";

    public long Size { get; set; }
}

public static class TisJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
