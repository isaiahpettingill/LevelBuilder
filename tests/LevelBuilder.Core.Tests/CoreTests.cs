using System.Text;
using LevelBuilder.Core;
using LevelBuilder.Core.Format;
using Xunit;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LevelBuilder.Core.Tests;

public class CoreTests
{
    [Fact]
    public void ProtobufRoundTripAndValidationPreserveTagRelativeReference()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var config = ProjectConfig.Create(Path.Combine(root, "project.json"));
            var level = LevelStore.New("test", 8, 8, 1, 1);
            level.VisualLayers[0].Tiles.Add(new TileCell { X = 2, Y = 3, Tile = new TileReference { Tileset = "assets/test.ase", Tagged = new TaggedVariant { Tag = "grass", Variant = 1 } } });
            config.Assets["assets/test.ase"] = AssetKind.Tileset;
            var path = Path.Combine(root, "levels", "test.level"); LevelStore.Save(level, path);
            var loaded = LevelStore.Load(path);
            Assert.Equal("grass", loaded.VisualLayers[0].Tiles[0].Tile.Tagged.Tag);
            Assert.Equal((uint)1, loaded.VisualLayers[0].Tiles[0].Tile.Tagged.Variant);
            Assert.Contains(LevelValidator.Validate(loaded, new ProjectContext(Path.Combine(root, "project.json"), config), new Dictionary<string, AseAsset>()), i => i.Message.Contains("Missing asset file"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void GoEnumsAreDeterministicAndRejectDuplicates()
    {
        var config = new ProjectConfig();
        var a = GoGenerator.Generate(config); Assert.Equal(a, GoGenerator.Generate(config));
        Assert.Contains("ColliderTypeSpikes ColliderType = 2", a);
        config.AnchorTypes.Add("PlayerSpawn");
        Assert.Throws<InvalidDataException>(() => GoGenerator.Generate(config));
    }
    [Fact]
    public void AsepriteTagsMapStableVariantsAndDetectOverlap()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ase");
        try
        {
            using (var bytes = new MemoryStream()) using (var w = new BinaryWriter(bytes, Encoding.UTF8, true))
            {
                w.Write(new byte[128]);
                for (var i = 0; i < 3; i++)
                {
                    var start = bytes.Position; w.Write((uint)0); w.Write((ushort)0xF1FA); w.Write((ushort)(i == 0 ? 1 : 0)); w.Write((ushort)80); w.Write((ushort)0); w.Write((uint)0);
                    if (i == 0)
                    {
                        var chunk = bytes.Position; w.Write((uint)0); w.Write((ushort)0x2018); w.Write((ushort)1); w.Write(new byte[8]);
                        w.Write((ushort)0); w.Write((ushort)1); w.Write((byte)0); w.Write((ushort)0); w.Write(new byte[6]); w.Write(new byte[4]);
                        w.Write((ushort)5); w.Write(Encoding.UTF8.GetBytes("grass"));
                        var end = bytes.Position; bytes.Position = chunk; w.Write((uint)(end - chunk)); bytes.Position = end;
                    }
                    var frameEnd = bytes.Position; bytes.Position = start; w.Write((uint)(frameEnd - start)); bytes.Position = frameEnd;
                }
                var length = bytes.Position; bytes.Position = 0;
                w.Write((uint)length); w.Write((ushort)0xA5E0); w.Write((ushort)3); w.Write((ushort)1); w.Write((ushort)1); w.Write((ushort)32);
                File.WriteAllBytes(path, bytes.ToArray());
            }
            using var asset = AsepriteReader.Read(path);
            Assert.Equal(1, asset.FrameFor("grass", 1));
            Assert.Null(asset.FrameFor("grass", 2));
            Assert.Equal(new[] { 2 }, asset.Untagged());
            asset.ValidateTileset();
            asset.Tags.Add(new AseTag("overlap", 1, 2, 0));
            Assert.Throws<InvalidDataException>(() => asset.ValidateTileset());
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void AsepriteCompositesRawRgbaCel()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".aseprite");
        try
        {
            using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
            writer.Write(new byte[128]);
            var frameStart = bytes.Position;
            writer.Write((uint)0); writer.Write((ushort)0xF1FA); writer.Write((ushort)2); writer.Write((ushort)120); writer.Write((ushort)0); writer.Write((uint)0);
            WriteChunk(0x2004, w =>
            {
                w.Write((ushort)1); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1);
                w.Write((ushort)0); w.Write((byte)255); w.Write(new byte[3]); w.Write((ushort)4); w.Write(Encoding.UTF8.GetBytes("base"));
            });
            WriteChunk(0x2005, w =>
            {
                w.Write((ushort)0); w.Write((short)0); w.Write((short)0); w.Write((byte)255); w.Write((ushort)0); w.Write((short)0); w.Write(new byte[5]);
                w.Write((ushort)1); w.Write((ushort)1); w.Write(new byte[] { 255, 20, 10, 255 });
            });
            var end = bytes.Position; bytes.Position = frameStart; writer.Write((uint)(end - frameStart));
            bytes.Position = 0; writer.Write((uint)end); writer.Write((ushort)0xA5E0); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write((ushort)32);
            File.WriteAllBytes(path, bytes.ToArray());
            using var asset = AsepriteReader.Read(path);
            Assert.Equal((byte)255, asset.Frames[0].Image[0, 0].R);
            Assert.Equal((byte)20, asset.Frames[0].Image[0, 0].G);
            Assert.Equal(120, asset.Frames[0].DurationMs);
            void WriteChunk(ushort type, Action<BinaryWriter> content)
            {
                var start = bytes.Position; writer.Write((uint)0); writer.Write(type); content(writer);
                var finish = bytes.Position; bytes.Position = start; writer.Write((uint)(finish - start)); bytes.Position = finish;
            }
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void DocumentContainsMultipleLevelsAndCachedPngSurvivesMissingSource()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "project.json"); var config = ProjectConfig.Create(projectPath);
            var png = Path.Combine(root, "assets", "tiles.png");
            using (var image = new Image<Rgba32>(2, 1))
            {
                image[0, 0] = new Rgba32(255, 0, 0); image[1, 0] = new Rgba32(0, 255, 0); image.SaveAsPng(png);
            }
            config.Assets["assets/tiles.png"] = AssetKind.Tileset;
            var doc = LevelStore.NewDocument(config);
            doc.Levels[0].Name = "Village";
            doc.Levels.Add(LevelStore.New("Cave", 16, 16, 1, 1));
            using (var asset = PngAssetReader.Read(png, AssetKind.Tileset, 1, 1))
            {
                Assert.Equal(1, asset.FrameFor("tiles", 1));
                doc.CachedAssets.Add(AssetCache.Create("assets/tiles.png", AssetKind.Tileset, asset));
            }
            doc.Levels[1].VisualLayers[0].Tiles.Add(new TileCell { X = 1, Y = 2, Tile = new TileReference { Tileset = "assets/tiles.png", Tagged = new TaggedVariant { Tag = "tiles", Variant = 1 } } });
            var path = Path.Combine(root, "levels", "game.level"); LevelStore.SaveDocument(doc, path);
            var loaded = LevelStore.LoadDocument(path);
            Assert.Equal(new[] { "Village", "Cave" }, loaded.Levels.Select(l => l.Name));
            Assert.Equal(AssetKind.Tileset, ProjectConfig.FromSettings(loaded.Project).Assets["assets/tiles.png"]);
            File.Delete(png);
            using (var restored = AssetCache.Restore(loaded.CachedAssets[0])) Assert.Equal((byte)255, restored.Frames[1].Image[0, 0].G);
            var bundle = Path.Combine(root, "game.levelz"); LevelBundle.Export(loaded, new ProjectContext(projectPath, config), bundle);
            using var opened = LevelBundle.Open(bundle);
            Assert.Equal(2, opened.Document.Levels.Count);
            Assert.False(File.Exists(opened.Project.Resolve("assets/tiles.png")));
            Assert.Single(opened.Document.CachedAssets);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void RefreshingOneSourceUpdatesItsEmbeddedAtlasWithoutChangingLevelReferences()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "tiles.png");
            var doc = LevelStore.NewDocument(new ProjectConfig());
            doc.Levels[0].VisualLayers[0].Tiles.Add(new TileCell { X = 2, Y = 4,
                Tile = new TileReference { Tileset = "tiles.png", Tagged = new TaggedVariant { Tag = "tiles", Variant = 0 } } });
            using (var image = new Image<Rgba32>(1, 1)) { image[0, 0] = new Rgba32(255, 0, 0); image.SaveAsPng(source); }
            using (var old = PngAssetReader.Read(source, AssetKind.Tileset, 1, 1))
            {
                AssetCache.Replace(doc, AssetCache.Create("tiles.png", AssetKind.Tileset, old));
                AssetCache.Replace(doc, AssetCache.Create("other.png", AssetKind.Tileset, old));
            }
            var otherBytes = doc.CachedAssets.Single(c => c.Path == "other.png").AtlasPng;
            using (var image = new Image<Rgba32>(1, 1)) { image[0, 0] = new Rgba32(0, 255, 0); image.SaveAsPng(source); }
            using (var updated = PngAssetReader.Read(source, AssetKind.Tileset, 1, 1))
                AssetCache.Replace(doc, AssetCache.Create("tiles.png", AssetKind.Tileset, updated));
            Assert.Equal(2, doc.CachedAssets.Count);
            Assert.Equal(otherBytes, doc.CachedAssets.Single(c => c.Path == "other.png").AtlasPng);
            using var restored = AssetCache.Restore(doc.CachedAssets.Single(c => c.Path == "tiles.png"));
            Assert.Equal((byte)255, restored.Frames[0].Image[0, 0].G);
            Assert.Equal("tiles", doc.Levels[0].VisualLayers[0].Tiles[0].Tile.Tagged.Tag);
        }
        finally { Directory.Delete(root, true); }
    }
}
