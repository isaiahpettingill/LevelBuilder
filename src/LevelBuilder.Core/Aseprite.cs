using System.IO.Compression;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LevelBuilder.Core;

public sealed record AseTag(string Name, int From, int To, int Direction);
public sealed record AseFrame(int DurationMs, Image<Rgba32> Image);
public sealed class AseAsset : IDisposable
{
    public required string Path { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public List<AseFrame> Frames { get; } = [];
    public List<AseTag> Tags { get; } = [];
    public void ValidateTileset()
    {
        var used = new HashSet<int>();
        foreach (var tag in Tags)
        {
            if (string.IsNullOrWhiteSpace(tag.Name) || Tags.Count(t => t.Name == tag.Name) > 1) throw new InvalidDataException($"Duplicate or empty tag: {tag.Name}");
            if (tag.From < 0 || tag.To >= Frames.Count || tag.From > tag.To) throw new InvalidDataException($"Tag {tag.Name} is outside the frame range");
            for (var i = tag.From; i <= tag.To; i++) if (!used.Add(i)) throw new InvalidDataException($"Overlapping tileset tags at frame {i}");
        }
    }
    public int? FrameFor(string tag, int variant)
    {
        var group = Tags.FirstOrDefault(t => t.Name == tag);
        return group is not null && variant >= 0 && group.From + variant <= group.To ? group.From + variant : null;
    }
    public IEnumerable<int> Untagged() => Enumerable.Range(0, Frames.Count).Where(i => !Tags.Any(t => i >= t.From && i <= t.To));
    public void Dispose() { foreach (var frame in Frames) frame.Image.Dispose(); }
}
internal sealed record Cel(int Layer, int X, int Y, byte Opacity, int? Linked, int Width, int Height, byte[]? Pixels);
public static class AsepriteReader
{
    public static AseAsset Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != stream.Length) throw new InvalidDataException("Aseprite file size does not match header");
        if (reader.ReadUInt16() != 0xA5E0) throw new InvalidDataException("Not an Aseprite file");
        var frameCount = reader.ReadUInt16();
        var width = reader.ReadUInt16(); var height = reader.ReadUInt16(); var depth = reader.ReadUInt16();
        if (frameCount == 0 || width == 0 || height == 0 || width > 8192 || height > 8192 || depth is not (8 or 16 or 32)) throw new InvalidDataException("Unsupported Aseprite dimensions or color depth");
        stream.Position = 28; var transparentIndex = reader.ReadByte();
        stream.Position = 128;
        var asset = new AseAsset { Path = path, Width = width, Height = height };
        var layers = new List<(bool visible, byte opacity)>();
        var frames = new List<List<Cel>>();
        var palette = new Dictionary<int, Rgba32>();
        try
        {
            for (var f = 0; f < frameCount; f++)
            {
                var frameStart = stream.Position;
                var frameBytes = reader.ReadUInt32();
                if (reader.ReadUInt16() != 0xF1FA || frameBytes < 16 || frameStart + frameBytes > stream.Length) throw new InvalidDataException($"Invalid frame {f}");
                var oldChunks = reader.ReadUInt16();
                var duration = reader.ReadUInt16();
                stream.Position += 2;
                var newChunks = reader.ReadUInt32();
                var count = newChunks == 0 ? oldChunks : newChunks;
                var cels = new List<Cel>();
                for (var i = 0; i < count; i++)
                {
                    var chunkStart = stream.Position;
                    var size = reader.ReadUInt32(); var type = reader.ReadUInt16();
                    var end = chunkStart + size;
                    if (size < 6 || end > frameStart + frameBytes) throw new InvalidDataException($"Invalid chunk in frame {f}");
                    if (type == 0x2004)
                    {
                        var flags = reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt16();
                        stream.Position += 4; reader.ReadUInt16();
                        var opacity = reader.ReadByte(); stream.Position += 3;
                        ReadString(reader);
                        layers.Add(((flags & 1) != 0, opacity));
                    }
                    else if (type == 0x2005)
                    {
                        var layer = reader.ReadUInt16(); var x = reader.ReadInt16(); var y = reader.ReadInt16();
                        var opacity = reader.ReadByte(); var celType = reader.ReadUInt16();
                        stream.Position += 7;
                        if (celType == 1) cels.Add(new Cel(layer, x, y, opacity, reader.ReadUInt16(), 0, 0, null));
                        else if (celType is 0 or 2)
                        {
                            var w = reader.ReadUInt16(); var h = reader.ReadUInt16();
                            var expected = checked(w * h * depth / 8);
                            if (expected > 256 * 1024 * 1024) throw new InvalidDataException("Cel is too large");
                            byte[] pixels;
                            if (celType == 0) pixels = reader.ReadBytes(expected);
                            else
                            {
                                using var compressed = new MemoryStream(reader.ReadBytes(checked((int)(end - stream.Position))));
                                using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
                                pixels = new byte[expected]; zlib.ReadExactly(pixels);
                            }
                            if (pixels.Length != expected) throw new InvalidDataException("Truncated cel");
                            cels.Add(new Cel(layer, x, y, opacity, null, w, h, pixels));
                        }
                        else if (celType != 3) throw new InvalidDataException($"Unsupported cel type {celType}");
                    }
                    else if (type == 0x2018)
                    {
                        var tags = reader.ReadUInt16(); stream.Position += 8;
                        for (var t = 0; t < tags; t++)
                        {
                            var from = reader.ReadUInt16(); var to = reader.ReadUInt16(); var direction = reader.ReadByte();
                            stream.Position += 2 + 6 + 3 + 1;
                            asset.Tags.Add(new AseTag(ReadString(reader), from, to, direction));
                        }
                    }
                    else if (type == 0x2019)
                    {
                        reader.ReadUInt32(); var first = reader.ReadUInt32(); var last = reader.ReadUInt32(); stream.Position += 8;
                        if (last < first || last > 65535) throw new InvalidDataException("Invalid palette");
                        for (var p = first; p <= last; p++)
                        {
                            var flags = reader.ReadUInt16();
                            var rgba = new Rgba32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                            palette[(int)p] = rgba;
                            if ((flags & 1) != 0) ReadString(reader);
                        }
                    }
                    stream.Position = end;
                }
                frames.Add(cels);
                asset.Frames.Add(new AseFrame(duration == 0 ? 100 : duration, new Image<Rgba32>(width, height)));
                stream.Position = frameStart + frameBytes;
            }
            for (var f = 0; f < frames.Count; f++)
            {
                foreach (var cel in frames[f].OrderBy(c => c.Layer))
                {
                    if (cel.Layer >= layers.Count || !layers[cel.Layer].visible) continue;
                    var source = cel;
                    var seen = new HashSet<int>();
                    while (source.Linked is int link)
                    {
                        if (!seen.Add(link) || link >= frames.Count) throw new InvalidDataException("Invalid linked cel");
                        source = frames[link].FirstOrDefault(c => c.Layer == cel.Layer) ?? throw new InvalidDataException("Missing linked cel");
                    }
                    if (source.Pixels is null) continue;
                    var img = asset.Frames[f].Image;
                    for (var yy = 0; yy < source.Height; yy++) for (var xx = 0; xx < source.Width; xx++)
                    {
                        var dx = cel.X + xx; var dy = cel.Y + yy;
                        if (dx < 0 || dy < 0 || dx >= width || dy >= height) continue;
                        var offset = (yy * source.Width + xx) * depth / 8;
                        Rgba32 color = depth switch
                        {
                            32 => new Rgba32(source.Pixels[offset], source.Pixels[offset + 1], source.Pixels[offset + 2], source.Pixels[offset + 3]),
                            16 => new Rgba32(source.Pixels[offset], source.Pixels[offset], source.Pixels[offset], source.Pixels[offset + 1]),
                            _ => source.Pixels[offset] == transparentIndex ? new Rgba32(0, 0, 0, 0) : palette.GetValueOrDefault(source.Pixels[offset], new Rgba32(0, 0, 0, 0))
                        };
                        var alpha = color.A / 255f * cel.Opacity / 255f * layers[cel.Layer].opacity / 255f;
                        if (alpha == 0) continue;
                        var old = img[dx, dy]; var oldAlpha = old.A / 255f;
                        var outputAlpha = alpha + oldAlpha * (1 - alpha);
                        img[dx, dy] = new Rgba32(
                            (byte)((color.R * alpha + old.R * oldAlpha * (1 - alpha)) / outputAlpha),
                            (byte)((color.G * alpha + old.G * oldAlpha * (1 - alpha)) / outputAlpha),
                            (byte)((color.B * alpha + old.B * oldAlpha * (1 - alpha)) / outputAlpha),
                            (byte)(outputAlpha * 255));
                    }
                }
            }
            return asset;
        }
        catch { asset.Dispose(); throw; }
    }
    private static string ReadString(BinaryReader reader)
    {
        var size = reader.ReadUInt16();
        return System.Text.Encoding.UTF8.GetString(reader.ReadBytes(size));
    }
}
