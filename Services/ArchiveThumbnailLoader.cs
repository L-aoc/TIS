using System.IO;
using System.Windows.Media.Imaging;
using TisViewer.Helpers;
using TisViewer.Models;

namespace TisViewer.Services;

public static class ArchiveThumbnailLoader
{
    /// <summary>Loads the cover (or first image) of an archive as a small thumbnail.</summary>
    public static BitmapSource? Load(string archivePath, int maxDimension)
    {
        try
        {
            using var archive = TisArchive.Open(archivePath);
            TisMediaItem? item = archive.CoverIndex >= 0 ? archive.Items[archive.CoverIndex] : null;
            if (item is null || item.Kind != TisMediaKind.Image)
            {
                item = archive.Items.FirstOrDefault(i => i.Kind == TisMediaKind.Image);
            }

            if (item is null)
            {
                return null;
            }

            var data = archive.ReadAllBytes(item);
            return ImageLoader.Decode(data, maxDimension).Image;
        }
        catch
        {
            return null;
        }
    }
}
