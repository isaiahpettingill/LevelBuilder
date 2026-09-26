using System.Text;
using Google.Protobuf;
using LevelBuilder.Core.Format;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LevelBuilder.Core;

public static class LevelStore
{
    public static LevelDocument NewDocument(ProjectConfig config)
    {
        var document = new LevelDocument { FormatVersion = 1, Project = config.ToSettings() };
        document.Levels.Add(New());
        return document;
    }
    public static LevelDocument LoadDocument(string path, ProjectConfig? legacyProject = null)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var document = LevelDocument.Parser.ParseFrom(bytes);
            if (document.FormatVersion == 1 && document.Project is not null && document.Levels.Count > 0)
            {
                ProjectConfig.FromSettings(document.Project);
                return document;
            }
            if (document.FormatVersion != 0) throw new InvalidDataException($"Unsupported document format {document.FormatVersion}");
            // Import version 0.1.0 files that contained one bare Level and used project.json.
            if (legacyProject is null) throw new InvalidDataException("Legacy level needs its project configuration for import");
            var old = Level.Parser.ParseFrom(bytes);
            if (old.FormatVersion != 1) throw new InvalidDataException("Malformed or unsupported legacy level");
            var imported = new LevelDocument { FormatVersion = 1, Project = legacyProject.ToSettings() };
            imported.Levels.Add(old);
            return imported;
        }
        catch (InvalidProtocolBufferException e) { throw new InvalidDataException($"Malformed protobuf: {e.Message}", e); }
    }
    public static void SaveDocument(LevelDocument document, string path)
    {
        if (document.FormatVersion != 1 || document.Project is null || document.Levels.Count == 0)
            throw new InvalidDataException("Document needs a supported version, project settings, and at least one level");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        using (var file = File.Create(temp)) document.WriteTo(file);
        File.Move(temp, path, true);
    }
    public static Level New(string name = "New level", int width = 100, int height = 60, int tileWidth = 16, int tileHeight = 16)
    {
        var level = new Level { FormatVersion = 1, Name = name, Width = (uint)width, Height = (uint)height, TileWidth = (uint)tileWidth, TileHeight = (uint)tileHeight };
        level.VisualLayers.Add(new TileLayer { Name = "Background", Visible = true });
        return level;
    }
    public static Level Load(string path)
    {
        try { using var file = File.OpenRead(path); var level = Level.Parser.ParseFrom(file); if (level.FormatVersion != 1) throw new InvalidDataException($"Unsupported level format version {level.FormatVersion}"); return level; }
        catch (InvalidProtocolBufferException e) { throw new InvalidDataException($"Malformed protobuf: {e.Message}", e); }
    }
    public static void Save(Level level, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        using (var file = File.Create(temp)) level.WriteTo(file);
        File.Move(temp, path, true);
    }
    public static string DebugJson(Level level) => JsonFormatter.Default.Format(level);
    public static string DebugJson(LevelDocument document) => JsonFormatter.Default.Format(document);
}

public sealed record ValidationIssue(string Location, string Message)
{
    public override string ToString() => $"{Location}: {Message}";
}
public static class LevelValidator
{
    public static List<ValidationIssue> ValidateDocument(LevelDocument document, ProjectContext project, IReadOnlyDictionary<string, AseAsset> assets)
    {
        var issues = new List<ValidationIssue>();
        if (document.FormatVersion != 1 || document.Project is null) { issues.Add(new ValidationIssue("Document", "Missing project settings or unsupported format")); return issues; }
        ProjectConfig embedded;
        try { embedded = ProjectConfig.FromSettings(document.Project); }
        catch (InvalidDataException e) { issues.Add(new ValidationIssue("Document project", e.Message)); return issues; }
        var cachedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cached in document.CachedAssets)
        {
            if (!cachedPaths.Add(cached.Path)) issues.Add(new ValidationIssue("Asset cache", $"Duplicate cached asset {cached.Path}"));
            if (!embedded.Assets.TryGetValue(cached.Path, out var expected) || expected.ToString() != cached.Kind)
                issues.Add(new ValidationIssue("Asset cache", $"Unknown or mismatched cached asset {cached.Path}"));
            if (cached.AtlasPng.IsEmpty || cached.DurationsMs.Count == 0) issues.Add(new ValidationIssue("Asset cache", $"Missing atlas or frames for {cached.Path}"));
        }
        foreach (var path in embedded.Assets.Keys)
            if (!cachedPaths.Contains(path)) issues.Add(new ValidationIssue("Asset cache", $"Missing embedded texture for {path}"));
        if (document.Levels.Count == 0) issues.Add(new ValidationIssue("Document", "At least one level is required"));
        var names = new HashSet<string>(StringComparer.Ordinal);
        var embeddedProject = new ProjectContext(project.PathName, embedded);
        foreach (var level in document.Levels)
        {
            if (string.IsNullOrWhiteSpace(level.Name) || !names.Add(level.Name)) issues.Add(new ValidationIssue("Level", $"Duplicate or empty level name: {level.Name}"));
            issues.AddRange(Validate(level, embeddedProject, assets).Select(i => new ValidationIssue($"{level.Name} / {i.Location}", i.Message)));
        }
        return issues;
    }
    public static List<ValidationIssue> Validate(Level level, ProjectContext project, IReadOnlyDictionary<string, AseAsset> assets)
    {
        var issues = new List<ValidationIssue>();
        void Add(string location, string message) => issues.Add(new ValidationIssue(location, message));
        if (level.FormatVersion != 1) Add("Level", "Unsupported format version");
        if (level.Width == 0 || level.Height == 0 || level.TileWidth == 0 || level.TileHeight == 0) Add("Level", "Dimensions and tile size must be positive");
        try { GoGenerator.Generate(project.Config); } catch (InvalidDataException e) { Add("Project types", e.Message); }
        var layerNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (layer, index) in level.VisualLayers.Select((v, i) => (v, i)))
        {
            if (string.IsNullOrWhiteSpace(layer.Name) || !layerNames.Add(layer.Name)) Add($"Layer {index + 1}", "Duplicate or empty layer name");
            var coordinates = new HashSet<(int, int)>();
            foreach (var cell in layer.Tiles)
            {
                var loc = $"Layer {index + 1} ({layer.Name}) tile ({cell.X},{cell.Y})";
                if (!coordinates.Add((cell.X, cell.Y))) Add(loc, "Duplicate tile coordinate");
                if (!Within(cell.X, cell.Y, level)) Add(loc, "Outside level bounds");
                if (cell.Tile is null) { Add(loc, "Missing tile reference"); continue; }
                var reference = cell.Tile;
                if (!project.Config.Assets.TryGetValue(reference.Tileset, out var kind) || kind != AssetKind.Tileset) { Add(loc, $"Missing tileset {reference.Tileset}"); continue; }
                if (!assets.TryGetValue(reference.Tileset, out var asset)) { Add(loc, $"Missing asset file {reference.Tileset}"); continue; }
                if (asset.Width != level.TileWidth || asset.Height != level.TileHeight) Add(loc, "Tileset canvas dimensions differ from level tile size");
                if (reference.IdentityCase == TileReference.IdentityOneofCase.Tagged && asset.FrameFor(reference.Tagged.Tag, (int)reference.Tagged.Variant) is null) Add(loc, $"Missing tag or invalid variant {reference.Tagged.Tag}[{reference.Tagged.Variant}]");
                else if (reference.IdentityCase == TileReference.IdentityOneofCase.UnstableFrame && (reference.UnstableFrame >= asset.Frames.Count || !asset.Untagged().Contains((int)reference.UnstableFrame))) Add(loc, $"Invalid unstable frame {reference.UnstableFrame}");
                else if (reference.IdentityCase == TileReference.IdentityOneofCase.None) Add(loc, "No variant specified");
            }
        }
        var colliderCoordinates = new HashSet<(int, int)>();
        foreach (var cell in level.Colliders)
        {
            if (!colliderCoordinates.Add((cell.X, cell.Y))) Add($"Collider ({cell.X},{cell.Y})", "Duplicate collider coordinate");
            if (!Within(cell.X, cell.Y, level)) Add($"Collider ({cell.X},{cell.Y})", "Outside level bounds");
            if (cell.Property >= project.Config.ColliderTypes.Count || cell.Property == 0) Add($"Collider ({cell.X},{cell.Y})", $"Invalid collider value {cell.Property}");
        }
        var ids = new HashSet<string>(); var names = new HashSet<string>();
        foreach (var sprite in level.Sprites)
        {
            var loc = $"Sprite {sprite.Id}";
            if (string.IsNullOrWhiteSpace(sprite.Id) || !ids.Add(sprite.Id)) Add(loc, "Duplicate or empty object ID");
            if (!project.Config.Assets.TryGetValue(sprite.Asset, out var kind) || kind != AssetKind.AnimatedSprite || !assets.TryGetValue(sprite.Asset, out var asset)) { Add(loc, $"Missing sprite asset {sprite.Asset}"); continue; }
            if (sprite.Animation.Length > 0 && !asset.Tags.Any(t => t.Name == sprite.Animation)) Add(loc, $"Missing animation {sprite.Animation}");
            if (sprite.Animation.Length == 0 && sprite.Frame >= asset.Frames.Count) Add(loc, $"Invalid frame {sprite.Frame}");
        }
        foreach (var anchor in level.Anchors)
        {
            var loc = $"Anchor {anchor.Id}";
            if (string.IsNullOrWhiteSpace(anchor.Id) || !ids.Add(anchor.Id)) Add(loc, "Duplicate or empty object ID");
            if (anchor.Type >= project.Config.AnchorTypes.Count) Add(loc, $"Invalid anchor type {anchor.Type}");
            if (anchor.Name.Length > 0 && !names.Add(anchor.Name)) Add(loc, $"Duplicate anchor name {anchor.Name}");
        }
        foreach (var (path, kind) in project.Config.Assets)
        {
            if (!assets.TryGetValue(path, out var asset)) { Add("Project assets", $"Missing or invalid {path}"); continue; }
            if (kind == AssetKind.Tileset) try { asset.ValidateTileset(); } catch (Exception e) { Add(path, e.Message); }
        }
        return issues;
    }
    private static bool Within(int x, int y, Level level) => x >= 0 && y >= 0 && x < level.Width && y < level.Height;
}

public static class GoGenerator
{
    public static string Generate(ProjectConfig config, string package = "levels")
    {
        if (!Valid(package)) throw new InvalidDataException("Invalid Go package name");
        Check(config.ColliderTypes, "collider"); Check(config.AnchorTypes, "anchor");
        if (config.ColliderTypes.Count == 0 || config.ColliderTypes[0] != "None") throw new InvalidDataException("Collider type 0 must be None");
        var sb = new StringBuilder("// Code generated by LevelBuilder; DO NOT EDIT.\npackage ").Append(package).Append("\n\n");
        Emit("ColliderType", config.ColliderTypes);
        Emit("AnchorType", config.AnchorTypes);
        sb.Append("type TileCoord struct { X, Y int32 }\n");
        sb.Append("type Anchor struct { ID, Name string; Type AnchorType; X, Y float32 }\n");
        sb.Append("type LevelIndex struct { Properties map[TileCoord]ColliderType; Entities []Anchor }\n");
        sb.Append("func (l *LevelIndex) PropertyAt(x, y int32) ColliderType { return l.Properties[TileCoord{x, y}] }\n");
        sb.Append("func (l *LevelIndex) Anchors(t AnchorType) []Anchor { out := []Anchor{}; for _, a := range l.Entities { if a.Type == t { out = append(out, a) } }; return out }\n");
        sb.Append("func (l *LevelIndex) FirstAnchor(t AnchorType) (Anchor, bool) { for _, a := range l.Entities { if a.Type == t { return a, true } }; return Anchor{}, false }\n");
        sb.Append("func (l *LevelIndex) AnchorByName(name string) (Anchor, bool) { for _, a := range l.Entities { if a.Name == name { return a, true } }; return Anchor{}, false }\n");
        return sb.ToString();
        void Emit(string type, List<string> names)
        {
            sb.Append("type ").Append(type).Append(" uint32\nconst (\n");
            for (var i = 0; i < names.Count; i++) sb.Append('\t').Append(type).Append(names[i]).Append(' ').Append(type).Append(" = ").Append(i).Append('\n');
            sb.Append(")\n\n");
        }
    }
    private static void Check(List<string> names, string label)
    {
        if (names.Count == 0 || names.Any(n => !Valid(n)) || names.Count != names.Distinct(StringComparer.Ordinal).Count()) throw new InvalidDataException($"Invalid or duplicate {label} type name");
    }
    private static bool Valid(string name) => name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
}

public static class AssetExporter
{
    public static void Export(ProjectContext project, IReadOnlyDictionary<string, AseAsset> assets, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var (path, kind) in project.Config.Assets)
        {
            if (!assets.TryGetValue(path, out var asset)) throw new InvalidDataException($"Missing asset: {path}");
            if (kind == AssetKind.Tileset) asset.ValidateTileset();
            var stem = Path.ChangeExtension(path, null)!.Replace('/', '_').Replace('\\', '_');
            using var atlas = new Image<Rgba32>(asset.Width * asset.Frames.Count, asset.Height);
            for (var i = 0; i < asset.Frames.Count; i++)
            {
                var frame = asset.Frames[i].Image;
                for (var y = 0; y < asset.Height; y++) for (var x = 0; x < asset.Width; x++) atlas[i * asset.Width + x, y] = frame[x, y];
            }
            atlas.SaveAsPng(Path.Combine(destination, stem + ".png"));
            var metadata = new AssetMetadata(path, kind.ToString(), asset.Width, asset.Height,
                asset.Frames.Select(f => f.DurationMs).ToList(),
                asset.Tags.Select(t => new AssetTagMetadata(t.Name, t.From, t.To, t.Direction)).ToList(),
                asset.Untagged().ToList());
            File.WriteAllText(Path.Combine(destination, stem + ".json"), System.Text.Json.JsonSerializer.Serialize(metadata, ProjectConfig.Context.AssetMetadata));
        }
    }
}
