using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LevelBuilder.Core.Format;

namespace LevelBuilder.Core;

public sealed class ProjectConfig
{
    public int FormatVersion { get; set; } = 1;
    public string AssetDirectory { get; set; } = "assets";
    public string LevelDirectory { get; set; } = "levels";
    public string CacheDirectory { get; set; } = ".cache";
    public string Schema { get; set; } = "level.proto";
    public List<string> ColliderTypes { get; set; } = ["None", "Ground", "Spikes", "Water", "Ladder", "Platform", "Hazard"];
    public List<string> AnchorTypes { get; set; } = ["PlayerSpawn", "EnemySpawn", "BossSpawn", "ChestSpawn", "DoorEntry", "Checkpoint", "TriggerPoint"];
    public Dictionary<string, AssetKind> Assets { get; set; } = [];
    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter<AssetKind>() } };
    public static ProjectJsonContext Context { get; } = new(JsonOptions);
    public static ProjectConfig Load(string path) => JsonSerializer.Deserialize(File.ReadAllText(path), Context.ProjectConfig) ?? throw new InvalidDataException("Empty project file");
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Context.ProjectConfig));
    public ProjectSettings ToSettings()
    {
        var settings = new ProjectSettings { SchemaVersion = (uint)FormatVersion, AssetDirectory = AssetDirectory, LevelDirectory = LevelDirectory };
        settings.ColliderTypes.Add(ColliderTypes);
        settings.AnchorTypes.Add(AnchorTypes);
        settings.Assets.Add(Assets.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => new AssetSource { Path = a.Key, Kind = a.Value.ToString() }));
        return settings;
    }
    public static ProjectConfig FromSettings(ProjectSettings settings)
    {
        if (settings.SchemaVersion != 1) throw new InvalidDataException($"Unsupported project schema version {settings.SchemaVersion}");
        var config = new ProjectConfig { FormatVersion = 1, AssetDirectory = settings.AssetDirectory, LevelDirectory = settings.LevelDirectory,
            ColliderTypes = [.. settings.ColliderTypes], AnchorTypes = [.. settings.AnchorTypes] };
        foreach (var asset in settings.Assets)
        {
            if (!Enum.TryParse<AssetKind>(asset.Kind, out var kind) || !Enum.IsDefined(kind) || !config.Assets.TryAdd(asset.Path, kind))
                throw new InvalidDataException($"Invalid or duplicate asset definition: {asset.Path}");
        }
        return config;
    }
    public static ProjectConfig Create(string path)
    {
        var project = new ProjectConfig();
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, project.AssetDirectory));
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, project.LevelDirectory));
        using (var source = typeof(ProjectConfig).Assembly.GetManifestResourceStream("LevelBuilder.Core.level.proto") ?? throw new InvalidOperationException("Embedded level schema missing"))
        using (var target = File.Create(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, project.Schema))) source.CopyTo(target);
        project.Save(path);
        return project;
    }
}
public enum AssetKind { Tileset, AnimatedSprite }

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ProjectConfig))]
[JsonSerializable(typeof(AssetMetadata))]
public partial class ProjectJsonContext : JsonSerializerContext;

public sealed record AssetTagMetadata(string Name, int From, int To, int Direction);
public sealed record AssetMetadata(string Source, string Kind, int FrameWidth, int FrameHeight, List<int> DurationsMs, List<AssetTagMetadata> Tags, List<int> UnstableFrames);
public sealed class ProjectContext(string path, ProjectConfig config)
{
    public string PathName { get; } = Path.GetFullPath(path);
    public ProjectConfig Config { get; } = config;
    public string Root => Path.GetDirectoryName(PathName)!;
    public string Resolve(string relative)
    {
        var root = Root + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException($"Path escapes project: {relative}");
        return full;
    }
    public string Relative(string path) => Path.GetRelativePath(Root, Path.GetFullPath(path)).Replace('\\', '/');
}
