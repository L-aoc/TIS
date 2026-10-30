using System.IO;
using System.Windows.Media.Imaging;
using TisViewer.Helpers;
using TisViewer.Models;

namespace TisViewer.Services;

public sealed record ArchivePreview(BitmapSource? Thumbnail, TisMediaKind Kind);

public static class ArchiveThumbnailLoader
{
    /// <summary>Loads the cover image (or first image) plus the kind of the cover item.</summary>
    public static ArchivePreview? LoadPreview(string archivePath, int maxDimension)
    {
        try
        {
            using var archive = TisArchive.Open(archivePath);
            var cover = archive.CoverIndex >= 0 ? archive.Items[archive.CoverIndex] : null;
            var previewItem = cover;
            if (previewItem is null || previewItem.Kind != TisMediaKind.Image)
            {
                previewItem = archive.Items.FirstOrDefault(i => i.Kind == TisMediaKind.Image);
            }

            var kind = cover?.Kind ?? previewItem?.Kind ?? TisMediaKind.Unknown;
            if (previewItem is null)
            {
                return new ArchivePreview(null, kind);
            }

            var data = archive.ReadAllBytes(previewItem);
            var image = ImageLoader.Decode(data, maxDimension).Image;
            return new ArchivePreview(image, kind);
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource? Load(string archivePath, int maxDimension) =>
        LoadPreview(archivePath, maxDimension)?.Thumbnail;
}
