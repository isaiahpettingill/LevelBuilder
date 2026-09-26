using Google.Protobuf;
using LevelBuilder.Core.Format;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LevelBuilder.Core;

public static class PngAssetReader
{
    public static AseAsset Read(string path, AssetKind kind, int tileWidth, int tileHeight)
    {
        using var image = Image.Load<Rgba32>(path);
        var width = kind == AssetKind.Tileset ? tileWidth : image.Width;
        var height = kind == AssetKind.Tileset ? tileHeight : image.Height;
        if (width <= 0 || height <= 0 || image.Width % width != 0 || image.Height % height != 0)
            throw new InvalidDataException($"PNG dimensions {image.Width}×{image.Height} are not divisible by tile size {width}×{height}");
        var asset = new AseAsset { Path = path, Width = width, Height = height };
        for (var y = 0; y < image.Height; y += height) for (var x = 0; x < image.Width; x += width)
        {
            var frame = new Image<Rgba32>(width, height);
            for (var yy = 0; yy < height; yy++) for (var xx = 0; xx < width; xx++) frame[xx, yy] = image[x + xx, y + yy];
            asset.Frames.Add(new AseFrame(100, frame));
        }
        asset.Tags.Add(new AseTag(kind == AssetKind.Tileset ? "tiles" : "default", 0, asset.Frames.Count - 1, 0));
        return asset;
    }
}

public static class AssetCache
{
    public static CachedAsset Create(string path, AssetKind kind, AseAsset asset)
    {
        using var atlas = new Image<Rgba32>(checked(asset.Width * asset.Frames.Count), asset.Height);
        for (var i = 0; i < asset.Frames.Count; i++)
            for (var y = 0; y < asset.Height; y++) for (var x = 0; x < asset.Width; x++) atlas[i * asset.Width + x, y] = asset.Frames[i].Image[x, y];
        using var stream = new MemoryStream(); atlas.SaveAsPng(stream);
        var cached = new CachedAsset { Path = path, Kind = kind.ToString(), FrameWidth = (uint)asset.Width, FrameHeight = (uint)asset.Height, AtlasPng = ByteString.CopyFrom(stream.ToArray()) };
        cached.DurationsMs.Add(asset.Frames.Select(f => (uint)f.DurationMs));
        cached.Tags.Add(asset.Tags.Select(t => new FrameTag { Name = t.Name, From = (uint)t.From, To = (uint)t.To, Direction = (uint)t.Direction }));
        return cached;
    }
    public static AseAsset Restore(CachedAsset cached)
    {
        if (cached.FrameWidth == 0 || cached.FrameHeight == 0 || cached.DurationsMs.Count == 0 || cached.DurationsMs.Count > 65535 || cached.AtlasPng.Length > 128 * 1024 * 1024)
            throw new InvalidDataException($"Invalid cached asset {cached.Path}");
        using var stream = new MemoryStream(cached.AtlasPng.ToByteArray());
        using var image = Image.Load<Rgba32>(stream);
        var w = checked((int)cached.FrameWidth); var h = checked((int)cached.FrameHeight);
        if ((long)w * h * cached.DurationsMs.Count > 100_000_000) throw new InvalidDataException($"Cached image is too large: {cached.Path}");
        if (image.Height != h || image.Width != checked(w * cached.DurationsMs.Count)) throw new InvalidDataException($"Invalid cached atlas dimensions for {cached.Path}");
        var asset = new AseAsset { Path = cached.Path, Width = w, Height = h };
        try
        {
            for (var i = 0; i < cached.DurationsMs.Count; i++)
            {
                var frame = new Image<Rgba32>(w, h);
                for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) frame[x, y] = image[i * w + x, y];
                asset.Frames.Add(new AseFrame(checked((int)cached.DurationsMs[i]), frame));
            }
            asset.Tags.AddRange(cached.Tags.Select(t => new AseTag(t.Name, checked((int)t.From), checked((int)t.To), checked((int)t.Direction))));
            return asset;
        }
        catch { asset.Dispose(); throw; }
    }
}
