using System.IO;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Readers;
using TisViewer.Models;

namespace TisViewer.Services;

public sealed class TisArchiveException : Exception
{
    public TisArchiveException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// A read-only view over a .tis file (a 7z archive). Entries are exposed in
/// manifest order when the archive was created by TIS, otherwise in archive order.
/// </summary>
public sealed class TisArchive : IDisposable
{
    public const string ManifestKey = "__tis__/manifest.json";

    private readonly IArchive _archive;
    private readonly Dictionary<string, IArchiveEntry> _entries;
    private readonly SemaphoreSlim _streamGate = new(1, 1);
    private bool _disposed;

    private TisArchive(string filePath, IArchive archive)
    {
        FilePath = filePath;
        _archive = archive;
        _entries = new Dictionary<string, IArchiveEntry>(StringComparer.OrdinalIgnoreCase);

        var files = new List<IArchiveEntry>();
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            var key = NormalizeKey(entry.Key);
            if (!_entries.TryAdd(key, entry))
            {
                continue;
            }

            files.Add(entry);
        }

        Manifest = TryReadManifest(files);

        var items = new List<TisMediaItem>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Manifest?.Items is { Count: > 0 })
        {
            foreach (var manifestItem in Manifest.Items)
            {
                var key = NormalizeKey(manifestItem.Entry);
                if (string.IsNullOrWhiteSpace(key) || key == ManifestKey || !_entries.TryGetValue(key, out var entry) || !used.Add(key))
                {
                    continue;
                }

                var kind = TisMediaTypes.ParseManifestValue(manifestItem.Kind);
                if (kind == TisMediaKind.Unknown)
                {
                    kind = TisMediaTypes.Detect(key);
                }

                var name = manifestItem.Name?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    name = Path.GetFileNameWithoutExtension(key);
                }

                items.Add(new TisMediaItem(key, name!, kind, entry.Size, entry.LastModifiedTime));
            }
        }

        foreach (var entry in files)
        {
            var key = NormalizeKey(entry.Key);
            if (key == ManifestKey || used.Contains(key))
            {
                continue;
            }

            var kind = TisMediaTypes.Detect(key);
            if (kind == TisMediaKind.Unknown)
            {
                continue;
            }

            used.Add(key);
            items.Add(new TisMediaItem(key, Path.GetFileNameWithoutExtension(key), kind, entry.Size, entry.LastModifiedTime));
        }

        if (items.Count == 0)
        {
            throw new TisArchiveException("这个压缩包里没有可查看的图片或视频。");
        }

        Items = items;

        var coverIndex = -1;
        if (!string.IsNullOrWhiteSpace(Manifest?.Cover))
        {
            var coverKey = NormalizeKey(Manifest.Cover);
            coverIndex = items.FindIndex(i => string.Equals(i.EntryKey, coverKey, StringComparison.OrdinalIgnoreCase));
        }

        CoverIndex = coverIndex;
        ArchiveSize = TryGetFileSize(filePath);
    }

    public string FilePath { get; }

    public string FileName => Path.GetFileName(FilePath);

    public IReadOnlyList<TisMediaItem> Items { get; }

    public int CoverIndex { get; }

    public TisManifest? Manifest { get; }

    public long ArchiveSize { get; }

    public static TisArchive Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new TisArchiveException("没有选择文件。");
        }

        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            throw new TisArchiveException($"文件不存在：{path}");
        }

        IArchive archive;
        try
        {
            archive = ArchiveFactory.OpenArchive(new FileInfo(path), new ReaderOptions());
        }
        catch (Exception ex)
        {
            throw new TisArchiveException("无法打开这个 .tis 文件。它可能已损坏，或者不是 7z 压缩包。", ex);
        }

        try
        {
            return new TisArchive(path, archive);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    public byte[] ReadAllBytes(TisMediaItem item, CancellationToken cancellationToken = default)
    {
        using var stream = OpenEntryStream(item);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    public Task<byte[]> ReadAllBytesAsync(TisMediaItem item, CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadAllBytes(item, cancellationToken), cancellationToken);

    public void ExtractToFile(TisMediaItem item, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var source = OpenEntryStream(item);
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
            total += read;
            if (item.Size > 0)
            {
                progress?.Report(Math.Clamp(total / (double)item.Size, 0, 1));
            }
        }
    }

    public Task ExtractToFileAsync(TisMediaItem item, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ExtractToFile(item, destinationPath, progress, cancellationToken), cancellationToken);

    private static TisManifest? TryReadManifest(IEnumerable<IArchiveEntry> files)
    {
        var manifestEntry = files.FirstOrDefault(e => string.Equals(NormalizeKey(e.Key), ManifestKey, StringComparison.OrdinalIgnoreCase));
        if (manifestEntry is null)
        {
            return null;
        }

        try
        {
            using var stream = manifestEntry.OpenEntryStream();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var manifest = JsonSerializer.Deserialize<TisManifest>(memory.ToArray(), TisJson.Options);
            return manifest is { Items.Count: > 0 } ? manifest : null;
        }
        catch
        {
            return null;
        }
    }

    private Stream OpenEntryStream(TisMediaItem item)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_entries.TryGetValue(item.EntryKey, out var entry))
        {
            throw new TisArchiveException($"压缩包中找不到文件：{item.EntryKey}");
        }

        _streamGate.Wait();
        try
        {
            return new GateStream(entry.OpenEntryStream(), _streamGate);
        }
        catch
        {
            _streamGate.Release();
            throw;
        }
    }

    private static string NormalizeKey(string? key) =>
        (key ?? string.Empty).Replace('\\', '/').TrimStart('/');

    private static long TryGetFileSize(string path)
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _archive.Dispose();
    }

    /// <summary>Holds the archive stream gate until the entry stream is disposed.</summary>
    private sealed class GateStream : Stream
    {
        private readonly Stream _inner;
        private readonly SemaphoreSlim _gate;
        private int _released;

        public GateStream(Stream inner, SemaphoreSlim gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void Flush() => _inner.Flush();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    _gate.Release();
                }
            }

            base.Dispose(disposing);
        }
    }
}
