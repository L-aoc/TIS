using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TisViewer.Helpers;

public sealed record DecodedImage(BitmapSource Image, int PixelWidth, int PixelHeight);

public static class ImageLoader
{
    public const int DefaultMaxDimension = 6000;

    public static DecodedImage Decode(byte[] data, int maxDimension = DefaultMaxDimension)
    {
        using var probe = new MemoryStream(data, false);
        return DecodeCore(() => new MemoryStream(data, false), probe, maxDimension);
    }

    public static DecodedImage DecodeFile(string path, int maxDimension = DefaultMaxDimension)
    {
        using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return DecodeCore(
            () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
            probe,
            maxDimension);
    }

    public static BitmapSource? TryDecodeFile(string path, int maxDimension)
    {
        try
        {
            return DecodeFile(path, maxDimension).Image;
        }
        catch
        {
            return null;
        }
    }

    private static DecodedImage DecodeCore(Func<Stream> openImageStream, Stream probeStream, int maxDimension)
    {
        var width = 0;
        var height = 0;
        var orientation = 1;

        try
        {
            var decoder = BitmapDecoder.Create(probeStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
            {
                throw new NotSupportedException("图片没有可用的画面。");
            }

            var frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            orientation = ReadOrientation(frame.Metadata as BitmapMetadata);
        }
        catch (Exception ex) when (ex is not NotSupportedException)
        {
            throw new NotSupportedException("无法读取图片信息。", ex);
        }

        if (width <= 0 || height <= 0)
        {
            throw new NotSupportedException("图片尺寸无效。");
        }

        var bitmap = new BitmapImage();
        using (var stream = openImageStream())
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            if (maxDimension > 0 && Math.Max(width, height) > maxDimension)
            {
                if (width >= height)
                {
                    bitmap.DecodePixelWidth = maxDimension;
                }
                else
                {
                    bitmap.DecodePixelHeight = maxDimension;
                }
            }

            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }

        BitmapSource result = bitmap;
        if (orientation is > 1 and < 9)
        {
            result = ApplyOrientation(result, orientation);
        }

        if (result.CanFreeze)
        {
            result.Freeze();
        }

        return new DecodedImage(result, result.PixelWidth, result.PixelHeight);
    }

    private static int ReadOrientation(BitmapMetadata? metadata)
    {
        if (metadata is null)
        {
            return 1;
        }

        try
        {
            var value = metadata.GetQuery("/app1/ifd/{ushort=274}");
            return value switch
            {
                ushort ushortValue => ushortValue,
                short shortValue => shortValue,
                int intValue => intValue,
                uint uintValue => (int)uintValue,
                _ => 1,
            };
        }
        catch
        {
            return 1;
        }
    }

    private static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            8 => new RotateTransform(270),
            _ => null,
        };

        if (transform is null)
        {
            return source;
        }

        try
        {
            return new TransformedBitmap(source, transform);
        }
        catch
        {
            return source;
        }
    }
}
