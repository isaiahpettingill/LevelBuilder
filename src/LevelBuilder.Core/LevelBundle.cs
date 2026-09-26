using System.IO.Compression;
using Google.Protobuf;
using LevelBuilder.Core.Format;

namespace LevelBuilder.Core;

public sealed class BundleWorkspace(string directory, ProjectContext project, LevelDocument document) : IDisposable
{
    public string DirectoryPath { get; } = directory;
    public ProjectContext Project { get; } = project;
    public LevelDocument Document { get; } = document;
    public string LevelPath => Path.Combine(DirectoryPath, "levels", "level.level");
    public void Dispose() { try { Directory.Delete(DirectoryPath, true); } catch { /* Cleanup may be retried by the OS. */ } }
}

public static class LevelBundle
{
    public static void Export(LevelDocument document, ProjectContext project, string destination)
    {
        if (document.Project is null || document.Levels.Count == 0) throw new InvalidDataException("Document has no project or levels");
        var embedded = ProjectConfig.FromSettings(document.Project);
        var used = embedded.Assets.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        foreach (var asset in used)
        {
            if (!embedded.Assets.ContainsKey(asset)) throw new InvalidDataException($"Asset missing from document project: {asset}");
            var archivePath = ArchivePath(asset);
            if (archivePath is "level.proto" or "levels/level.level") throw new InvalidDataException($"Asset path conflicts with bundle metadata: {asset}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var temp = destination + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                using (var stream = archive.CreateEntry("levels/level.level").Open()) document.WriteTo(stream);
                var schema = project.Resolve(project.Config.Schema);
                if (File.Exists(schema)) archive.CreateEntryFromFile(schema, "level.proto");
                else
                {
                    using var source = typeof(ProjectConfig).Assembly.GetManifestResourceStream("LevelBuilder.Core.level.proto") ?? throw new InvalidDataException("Embedded schema missing");
                    using var target = archive.CreateEntry("level.proto").Open(); source.CopyTo(target);
                }
                foreach (var asset in used)
                {
                    var path = project.Resolve(asset);
                    // The cached atlas in .level remains usable when an editable source has gone missing.
                    if (File.Exists(path)) archive.CreateEntryFromFile(path, ArchivePath(asset), CompressionLevel.Optimal);
                }
            }
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static BundleWorkspace Open(string path)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LevelBuilder-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (var archive = ZipFile.OpenRead(path))
            {
                if (archive.Entries.Count > 4096 || archive.Entries.Sum(e => e.Length) > 512L * 1024 * 1024)
                    throw new InvalidDataException("Bundle is too large");
                foreach (var entry in archive.Entries) ArchivePath(entry.FullName.TrimEnd('/'));
                archive.ExtractToDirectory(directory);
            }
            var levelPath = Path.Combine(directory, "levels", "level.level");
            var document = LevelStore.LoadDocument(levelPath);
            var config = ProjectConfig.FromSettings(document.Project);
            var projectPath = Path.Combine(directory, "project.json");
            config.Save(projectPath); // Local editing settings; the archive itself stores these in .level.
            var project = new ProjectContext(projectPath, config);
            foreach (var asset in config.Assets.Keys)
            {
                if (!asset.EndsWith(".ase", StringComparison.OrdinalIgnoreCase) && !asset.EndsWith(".aseprite", StringComparison.OrdinalIgnoreCase) && !asset.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Bundle contains unsupported asset reference: {asset}");
                if (!File.Exists(project.Resolve(asset)) && !document.CachedAssets.Any(c => c.Path == asset))
                    throw new InvalidDataException($"Missing source and cache for {asset}");
            }
            return new BundleWorkspace(directory, project, document);
        }
        catch { try { Directory.Delete(directory, true); } catch { } throw; }
    }
    private static string ArchivePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains(':') || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException($"Unsafe bundle path: {path}");
        return normalized;
    }
}
