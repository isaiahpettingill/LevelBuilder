using System.Text.Json;
using System.Text.Json.Serialization;

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
    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    public static ProjectConfig Load(string path) => JsonSerializer.Deserialize<ProjectConfig>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Empty project file");
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
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
