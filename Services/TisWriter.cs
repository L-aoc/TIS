using System.IO;
using System.Text;
using System.Text.Json;
using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;
using TisViewer.Models;

namespace TisViewer.Services;

public enum TisCompressionPreset
{
    Fastest,
    Fast,
    Balanced,
    Maximum,
}

public sealed class TisCreateRequest
{
    public required string OutputPath { get; init; }

    /// <summary>Source files in the exact order they should be viewed.</summary>
    public required IReadOnlyList<string> Files { get; init; }

    public string? CoverSourcePath { get; init; }

    public TisCompressionPreset Compression { get; init; } = TisCompressionPreset.Balanced;
}

public sealed record TisWriteProgress(int Completed, int Total, string CurrentFile, double Fraction);

public static class TisWriter
{
    public const long MaxEntryNameLength = 120;

    public static async Task WriteAsync(TisCreateRequest request, IProgress<TisWriteProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (request.Files.Count == 0)
        {
            throw new InvalidOperationException("请先添加要打包的照片或视频。");
        }

        var outputPath = Path.GetFullPath(request.OutputPath);
        if (!outputPath.EndsWith(".tis", StringComparison.OrdinalIgnoreCase))
        {
            outputPath += ".tis";
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(outputDirectory))
        {
            throw new InvalidOperationException("保存位置无效。");
        }

        Directory.CreateDirectory(outputDirectory);

        foreach (var file in request.Files)
        {
            if (!File.Exists(file))
            {
                throw new FileNotFoundException($"文件不存在：{file}", file);
            }
        }

        var entries = BuildEntries(request.Files);
        var coverEntry = entries.FirstOrDefault(e => string.Equals(e.SourcePath, request.CoverSourcePath, StringComparison.OrdinalIgnoreCase))?.EntryName
                         ?? entries[0].EntryName;

        var manifest = new TisManifest
        {
            Title = Path.GetFileNameWithoutExtension(outputPath),
            Cover = coverEntry,
            Created = DateTimeOffset.Now,
            Items = entries.Select(e => new TisManifestItem
            {
                Entry = e.EntryName,
                Name = e.DisplayName,
                Kind = TisMediaTypes.ToManifestValue(TisMediaTypes.Detect(e.SourcePath)),
                Size = TryGetLength(e.SourcePath),
            }).ToList(),
        };

        var created = await Task.Run(
            () => WriteCore(request, outputPath, entries, manifest, progress, cancellationToken),
            cancellationToken);

        if (!created)
        {
            throw new InvalidOperationException("生成 TIS 文件失败。");
        }
    }

    private static bool WriteCore(
        TisCreateRequest request,
        string outputPath,
        IReadOnlyList<PlannedEntry> entries,
        TisManifest manifest,
        IProgress<TisWriteProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tempPath = outputPath + ".building";
        var total = entries.Count + 1;

        try
        {
            TryDelete(tempPath);

            var options = CreateOptions(request.Compression);
            using (var writer = SevenZipWriter.OpenWriter(tempPath, options))
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var planned = entries[i];
                    progress?.Report(new TisWriteProgress(i, total, Path.GetFileName(planned.SourcePath), i / (double)total));

                    using var source = new FileStream(planned.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                    writer.Write(planned.EntryName, source, SafeLastWriteTime(planned.SourcePath));
                }

                progress?.Report(new TisWriteProgress(entries.Count, total, "封面与目录信息", entries.Count / (double)total));
                var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, TisJson.Options);
                using (var manifestStream = new MemoryStream(manifestBytes))
                {
                    writer.Write(TisArchive.ManifestKey, manifestStream, DateTime.Now);
                }
            }

            if (!LooksLike7z(tempPath))
            {
                throw new InvalidOperationException("生成的文件不是有效的 7z 压缩包。");
            }

            File.Move(tempPath, outputPath, true);
            progress?.Report(new TisWriteProgress(total, total, string.Empty, 1));
            return true;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static SevenZipWriterOptions CreateOptions(TisCompressionPreset preset) => preset switch
    {
        TisCompressionPreset.Fastest => new SevenZipWriterOptions(CompressionType.LZMA2)
        {
            CompressionLevel = 0,
            LeaveStreamOpen = false,
        },
        TisCompressionPreset.Fast => new SevenZipWriterOptions(CompressionType.LZMA2)
        {
            CompressionLevel = 2,
            LeaveStreamOpen = false,
        },
        TisCompressionPreset.Maximum => new SevenZipWriterOptions(CompressionType.LZMA2)
        {
            CompressionLevel = 9,
            LeaveStreamOpen = false,
        },
        _ => new SevenZipWriterOptions(CompressionType.LZMA2)
        {
            CompressionLevel = 5,
            LeaveStreamOpen = false,
        },
    };

    private static List<PlannedEntry> BuildEntries(IReadOnlyList<string> files)
    {
        var result = new List<PlannedEntry>(files.Count);
        var usedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "__tis__" };
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);

            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "未命名";
            }

            baseName = Truncate(baseName, (int)MaxEntryNameLength - extension.Length);
            extension = Truncate(extension, 16);

            var entryName = baseName + extension;
            var counter = 2;
            while (usedEntries.Contains(entryName))
            {
                var suffix = $" ({counter++})";
                entryName = Truncate(baseName, (int)MaxEntryNameLength - extension.Length - suffix.Length) + suffix + extension;
            }

            usedEntries.Add(entryName);

            var displayName = baseName;
            counter = 2;
            while (!usedNames.Add(displayName))
            {
                displayName = $"{baseName} ({counter++})";
            }

            result.Add(new PlannedEntry(file, entryName, displayName));
        }

        return result;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (maxLength <= 0)
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static DateTime? SafeLastWriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTime(path);
        }
        catch
        {
            return null;
        }
    }

    private static long TryGetLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static bool LooksLike7z(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> signature = stackalloc byte[6];
            if (stream.Read(signature) != signature.Length)
            {
                return false;
            }

            return signature[0] == 0x37 && signature[1] == 0x7A && signature[2] == 0xBC &&
                   signature[3] == 0xAF && signature[4] == 0x27 && signature[5] == 0x1C;
        }
        catch
        {
            return false;
        }
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

    private sealed record PlannedEntry(string SourcePath, string EntryName, string DisplayName);
}
