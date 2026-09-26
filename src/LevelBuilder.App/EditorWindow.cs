using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Google.Protobuf;
using System.Security.Cryptography;
using LevelBuilder.Core;
using LevelBuilder.Core.Format;
using IconPacks.Avalonia.Material;
using SixLabors.ImageSharp;
using Point = Avalonia.Point;

namespace LevelBuilder.App;

public enum EditorTool { Pencil, Erase, Rectangle, Flood, Pick, Select, Sprite, Anchor }
public sealed record PaletteChoice(string Asset, string? Tag, int Variant, int Frame)
{
    public override string ToString() => Tag is null ? $"⚠ {Asset} · untagged frame {Frame} (unstable)" : $"{Asset} · {Tag}[{Variant}]";
    public TileReference Reference() => Tag is null
        ? new TileReference { Tileset = Asset, UnstableFrame = (uint)Frame }
        : new TileReference { Tileset = Asset, Tagged = new TaggedVariant { Tag = Tag, Variant = (uint)Variant } };
}
public sealed class EditorWindow : Window
{
    private ProjectContext? project;
    private LevelDocument document = LevelStore.NewDocument(new ProjectConfig());
    private Level level;
    private int activeLevelIndex;
    private string? levelPath;
    private string? bundlePath;
    private BundleWorkspace? bundleWorkspace;
    private readonly Dictionary<string, AseAsset> assets = [];
    private readonly Dictionary<(string path, int frame), Bitmap> bitmaps = [];
    private FileSystemWatcher? watcher;
    private readonly Dictionary<string, int> queuedSourceChanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> sourceHashes = new(StringComparer.Ordinal);
    private int watcherGeneration;
    private readonly Stack<Level> undo = new();
    private readonly Stack<Level> redo = new();
    private readonly CanvasView canvas;
    private readonly ListBox palette = new();
    private readonly ListBox layers = new();
    private readonly StackPanel properties = new() { Spacing = 7, Margin = new Thickness(8) };
    private readonly TextBlock status = new();
    private readonly ComboBox mode = new();
    private readonly ComboBox levelSelector = new() { MinWidth = 130 };
    private readonly ComboBox collider = new();
    private readonly ComboBox anchorType = new();
    private readonly ComboBox sprites = new();
    private readonly ComboBox animation = new();
    private readonly CheckBox grid = new() { Content = "Grid", IsChecked = true };
    private readonly CheckBox snap = new() { Content = "Snap", IsChecked = true };
    private readonly CheckBox random = new() { Content = "Random variants" };
    private readonly TextBlock title = new();
    private bool dirty;
    private object? selected;
    private readonly DispatcherTimer animationTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer recoveryTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    public EditorWindow()
    {
        level = document.Levels[0];
        Title = "LevelBuilder"; Width = 1380; Height = 850; MinWidth = 850; MinHeight = 550;
        canvas = new CanvasView(this) { Focusable = true, ClipToBounds = true };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(6) };
        MenuItem Action(string name, Action click)
        {
            var item = new MenuItem { Header = name }; item.Click += (_, _) => click(); return item;
        }
        var projectMenu = new MenuItem { Header = "☰  Project", ItemsSource = new[]
        {
            Action("New project…", () => _ = NewProject()), Action("Open .level / .levelz…", () => _ = OpenLevel()),
            Action("Import asset…", () => _ = Import()), Action("Save as…", () => _ = Save(true)),
            Action("Add level", AddLevel), Action("Remove level", RemoveLevel),
            Action("Level settings…", () => _ = LevelSettings()), Action("Export game assets…", () => _ = Export()),
            Action("Share .levelz…", () => _ = ExportBundle()), Action("Debug JSON…", () => _ = DebugJson()),
            Action("Check updates…", () => _ = CheckUpdates())
        } };
        toolbar.Children.Add(new Menu { ItemsSource = new[] { projectMenu } });
        void IconAction(PackIconMaterialKind icon, string tip, Action click)
        {
            var button = new Button { Content = new PackIconMaterial { Kind = icon, Width = 20, Height = 20 }, Width = 38, Height = 36 };
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => click(); toolbar.Children.Add(button);
        }
        IconAction(PackIconMaterialKind.ContentSave, "Save (Ctrl+S)", () => _ = Save());
        IconAction(PackIconMaterialKind.Undo, "Undo (Ctrl+Z)", Undo);
        IconAction(PackIconMaterialKind.Redo, "Redo (Ctrl+Y)", Redo);
        toolbar.Children.Add(new TextBlock { Text = "Level", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        toolbar.Children.Add(levelSelector);
        levelSelector.SelectionChanged += (_, _) => SwitchLevel(levelSelector.SelectedIndex);
        mode.ItemsSource = new[] { "Visual", "Collider", "Sprite", "Anchor" }; mode.SelectedIndex = 0; mode.SelectionChanged += (_, _) => canvas.InvalidateVisual();
        collider.SelectionChanged += (_, _) => canvas.InvalidateVisual();
        toolbar.Children.Add(mode);
        var toolRail = new StackPanel { Spacing = 3, Margin = new Thickness(5) };
        var toolButtons = new List<ToggleButton>();
        var icons = new[] { PackIconMaterialKind.Pencil, PackIconMaterialKind.Eraser, PackIconMaterialKind.VectorRectangle,
            PackIconMaterialKind.FormatColorFill, PackIconMaterialKind.Eyedropper, PackIconMaterialKind.CursorDefault,
            PackIconMaterialKind.Image, PackIconMaterialKind.MapMarker };
        foreach (var tool in Enum.GetValues<EditorTool>())
        {
            var selectedTool = tool;
            var button = new ToggleButton { Content = new PackIconMaterial { Kind = icons[(int)tool], Width = 21, Height = 21 },
                Width = 42, Height = 40, IsChecked = tool == EditorTool.Pencil };
            ToolTip.SetTip(button, tool.ToString());
            button.Click += (_, _) =>
            {
                foreach (var other in toolButtons) other.IsChecked = other == button;
                canvas.Tool = selectedTool; SetStatus($"Tool: {selectedTool}"); canvas.Focus();
            };
            toolButtons.Add(button); toolRail.Children.Add(button);
        }
        toolRail.Children.Add(new Separator());
        toolRail.Children.Add(grid); toolRail.Children.Add(snap); toolRail.Children.Add(random);
        grid.Click += (_, _) => canvas.InvalidateVisual();
        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,*"), Width = 270 };
        left.Children.Add(new TextBlock { Text = "Tileset palette", FontWeight = FontWeight.Bold, Margin = new Thickness(8) });
        Grid.SetRow(palette, 1); left.Children.Add(palette); palette.SelectionChanged += (_, _) => canvas.InvalidateVisual();
        var layerHead = new StackPanel { Orientation = Orientation.Horizontal };
        var addLayer = new Button { Content = "+ Layer" }; addLayer.Click += (_, _) => { Commit(); level.VisualLayers.Add(new TileLayer { Name = $"Layer {level.VisualLayers.Count + 1}", Visible = true }); RefreshLayers(); Changed(); };
        var removeLayer = new Button { Content = "− Layer" }; removeLayer.Click += (_, _) => { if (level.VisualLayers.Count <= 1 || layers.SelectedIndex < 0) return; Commit(); level.VisualLayers.RemoveAt(layers.SelectedIndex); RefreshLayers(); Changed(); };
        layerHead.Children.Add(addLayer); layerHead.Children.Add(removeLayer); Grid.SetRow(layerHead, 2); left.Children.Add(layerHead);
        Grid.SetRow(layers, 3); left.Children.Add(layers);
        layers.SelectionChanged += (_, _) => ShowLayerProperties();
        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*"), Width = 245 };
        right.Children.Add(new TextBlock { Text = "Properties", FontWeight = FontWeight.Bold, Margin = new Thickness(8) });
        var c = new StackPanel { Margin = new Thickness(8) }; c.Children.Add(new TextBlock { Text = "Collider value" }); c.Children.Add(collider);
        Grid.SetRow(c, 1); right.Children.Add(c);
        var a = new StackPanel { Margin = new Thickness(8) }; a.Children.Add(new TextBlock { Text = "Anchor type" }); a.Children.Add(anchorType);
        Grid.SetRow(a, 2); right.Children.Add(a);
        var s = new StackPanel { Margin = new Thickness(8) }; s.Children.Add(new TextBlock { Text = "Sprite / animation" }); s.Children.Add(sprites); s.Children.Add(animation);
        Grid.SetRow(s, 3); right.Children.Add(s); sprites.SelectionChanged += (_, _) => RefreshAnimations();
        var scroll = new ScrollViewer { Content = properties }; Grid.SetRow(scroll, 4); right.Children.Add(scroll);
        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        main.Children.Add(left); Grid.SetColumn(toolRail, 1); main.Children.Add(toolRail);
        Grid.SetColumn(canvas, 2); main.Children.Add(canvas); Grid.SetColumn(right, 3); main.Children.Add(right);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(toolbar); Grid.SetRow(main, 1); root.Children.Add(main);
        var bar = new DockPanel { Margin = new Thickness(6) }; bar.Children.Add(status); Grid.SetRow(bar, 2); root.Children.Add(bar);
        Content = root;
        RefreshLevels(); RefreshLayers(); RefreshEnums(); SetStatus("Create or open a project to begin.");
        KeyDown += OnKeyDown;
        Closing += (_, e) => { if (dirty) { e.Cancel = true; _ = ConfirmClose(); } };
        Closed += (_, _) => { watcherGeneration++; watcher?.Dispose(); bundleWorkspace?.Dispose(); ClearAssets(); };
        animationTimer.Tick += (_, _) => canvas.InvalidateVisual(); animationTimer.Start();
        recoveryTimer.Tick += (_, _) => { if (dirty && levelPath is not null) { SyncDocument(); LevelStore.SaveDocument(document, levelPath + ".recovery"); } }; recoveryTimer.Start();
        Opened += (_, _) => _ = CheckUpdates(silent: true);
    }
    internal Level Level => level;
    internal ProjectContext? Project => project;
    internal IReadOnlyDictionary<string, AseAsset> Assets => assets;
    internal Bitmap? Bitmap(string path, int frame) => bitmaps.GetValueOrDefault((path, frame));
    internal PaletteChoice? Choice => (palette.SelectedItem as ListBoxItem)?.Tag as PaletteChoice;
    internal void SelectPaletteReference(TileReference reference)
    {
        if (palette.ItemsSource is not IEnumerable<ListBoxItem> items) return;
        var entry = items.FirstOrDefault(item => item.Tag is PaletteChoice choice && choice.Asset == reference.Tileset &&
            (reference.IdentityCase == TileReference.IdentityOneofCase.Tagged
                ? choice.Tag == reference.Tagged.Tag && choice.Variant == reference.Tagged.Variant
                : choice.Tag is null && choice.Frame == reference.UnstableFrame));
        if (entry is not null) { palette.SelectedItem = entry; SetStatus("Picked " + entry.Tag); }
    }
    internal void SelectCollider(uint value) { if (value < (uint)collider.ItemCount) collider.SelectedIndex = (int)value; }
    internal int Mode => mode.SelectedIndex;
    internal int ColliderValue => collider.SelectedIndex;
    internal int AnchorValue => anchorType.SelectedIndex;
    internal string? SpriteAsset => sprites.SelectedItem as string;
    internal string AnimationName => animation.SelectedItem as string ?? "";
    internal bool GridVisible => grid.IsChecked == true;
    internal bool Snapping => snap.IsChecked == true;
    internal bool RandomVariants => random.IsChecked == true;
    internal TileLayer? ActiveLayer => layers.SelectedIndex >= 0 && layers.SelectedIndex < level.VisualLayers.Count ? level.VisualLayers[layers.SelectedIndex] : null;
    internal object? Selected { get => selected; set { selected = value; ShowSelected(); canvas.InvalidateVisual(); } }
    internal void Commit() { undo.Push(level.Clone()); if (undo.Count > 100) { var oldestFirst = undo.Reverse().Skip(1).ToArray(); undo.Clear(); foreach (var item in oldestFirst) undo.Push(item); } redo.Clear(); }
    internal void Changed() { dirty = true; SyncDocument(); UpdateTitle(); canvas.MarkDataDirty(); }
    internal void SetStatus(string message) => status.Text = message;
    private void UpdateTitle() => Title = $"LevelBuilder · {Path.GetFileName(bundlePath ?? levelPath ?? project?.PathName ?? "Untitled")} / {level.Name}{(dirty ? " *" : "")}";
    private void SyncDocument()
    {
        document.Levels[activeLevelIndex] = level;
        if (project is not null) document.Project = project.Config.ToSettings();
    }
    private void RefreshLevels()
    {
        levelSelector.ItemsSource = document.Levels.Select(l => l.Name).ToArray();
        levelSelector.SelectedIndex = activeLevelIndex;
    }
    private void SwitchLevel(int index)
    {
        if (index < 0 || index >= document.Levels.Count || index == activeLevelIndex) return;
        SyncDocument(); activeLevelIndex = index; level = document.Levels[index]; selected = null; undo.Clear(); redo.Clear(); RefreshLayers(); ShowLayerProperties(); UpdateTitle(); canvas.MarkDataDirty();
    }
    private void Undo() { if (undo.Count == 0) return; redo.Push(level); level = undo.Pop(); selected = null; RefreshLayers(); Changed(); }
    private void Redo() { if (redo.Count == 0) return; undo.Push(level); level = redo.Pop(); selected = null; RefreshLayers(); Changed(); }
    private void RefreshEnums()
    {
        collider.ItemsSource = project?.Config.ColliderTypes ?? ["None"]; collider.SelectedIndex = Math.Min(1, (project?.Config.ColliderTypes.Count ?? 1) - 1);
        anchorType.ItemsSource = project?.Config.AnchorTypes ?? ["PlayerSpawn"]; anchorType.SelectedIndex = 0;
    }
    private void RefreshLayers()
    {
        var index = layers.SelectedIndex;
        layers.ItemsSource = level.VisualLayers.Select((l, i) => $"{i + 1}. {(l.Visible ? "◉" : "○")} {(l.Locked ? "🔒" : "")} {l.Name}").ToArray();
        layers.SelectedIndex = Math.Clamp(index, 0, level.VisualLayers.Count - 1);
    }
    private void RefreshPalette()
    {
        var choices = new List<PaletteChoice>();
        foreach (var (path, kind) in project?.Config.Assets ?? [])
        {
            if (!assets.TryGetValue(path, out var asset) || kind != AssetKind.Tileset) continue;
            foreach (var tag in asset.Tags) for (var i = tag.From; i <= tag.To; i++) choices.Add(new PaletteChoice(path, tag.Name, i - tag.From, i));
            choices.AddRange(asset.Untagged().Select(i => new PaletteChoice(path, null, 0, i)));
        }
        palette.ItemsSource = choices.Select(choice =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            var image = Bitmap(choice.Asset, choice.Frame);
            if (image is not null) row.Children.Add(new Avalonia.Controls.Image { Source = image, Width = 34, Height = 34, Stretch = Stretch.Uniform });
            row.Children.Add(new TextBlock { Text = choice.ToString(), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            return new ListBoxItem { Tag = choice, Content = row };
        }).ToArray();
        if (choices.Count > 0) palette.SelectedIndex = 0;
        sprites.ItemsSource = project?.Config.Assets.Where(kv => kv.Value == AssetKind.AnimatedSprite && assets.ContainsKey(kv.Key)).Select(kv => kv.Key).ToArray() ?? [];
        if (sprites.ItemCount > 0) sprites.SelectedIndex = 0;
        canvas.InvalidateVisual();
    }
    private void RefreshAnimations()
    {
        var path = sprites.SelectedItem as string;
        animation.ItemsSource = path is not null && assets.TryGetValue(path, out var asset) ? new[] { "" }.Concat(asset.Tags.Select(t => t.Name)).ToArray() : [];
        animation.SelectedIndex = 0;
    }
    private async Task NewProject()
    {
        if (!await ConfirmDiscard()) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "New project", SuggestedFileName = "game.level", FileTypeChoices = [new FilePickerFileType("Level project") { Patterns = ["*.level"] }] });
        if (file is null) return;
        try
        {
            var path = Path.ChangeExtension(file.Path.LocalPath, ".level");
            var config = new ProjectConfig();
            await LoadProject(path, config);
            levelPath = path;
            SyncDocument(); LevelStore.SaveDocument(document, path); dirty = false; UpdateTitle();
        }
        catch (Exception e) { await Error(e.Message); }
    }
    private async Task LoadProject(string path, ProjectConfig config, string? selectedFile = null, BundleWorkspace? openedBundle = null)
    {
        watcherGeneration++; watcher?.Dispose(); queuedSourceChanges.Clear(); sourceHashes.Clear(); ClearAssets(); bundleWorkspace?.Dispose(); bundleWorkspace = openedBundle;
        bundlePath = null;
        var provisional = new ProjectContext(path, config);
        Directory.CreateDirectory(provisional.Resolve(config.LevelDirectory));
        var first = selectedFile;
        if (first is not null) await LoadWithRecovery(first, config);
        else { document = LevelStore.NewDocument(config); level = document.Levels[0]; levelPath = null; dirty = false; }
        project = new ProjectContext(path, ProjectConfig.FromSettings(document.Project));
        activeLevelIndex = 0; level = document.Levels[0];
        RefreshEnums(); ReloadAssets(); RefreshCache();
        Directory.CreateDirectory(project.Resolve(project.Config.AssetDirectory));
        watcher = new FileSystemWatcher(project.Root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        watcher.Changed += AssetChanged; watcher.Created += AssetChanged; watcher.Deleted += AssetChanged;
        watcher.Renamed += (_, e) => { QueueAssetChange(e.OldFullPath); QueueAssetChange(e.FullPath); };
        watcher.Error += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (project is null) return;
            foreach (var path in project.Config.Assets.Keys) ScheduleAssetChange(path);
        });
        watcher.EnableRaisingEvents = true;
        undo.Clear(); redo.Clear(); RefreshLevels(); RefreshLayers(); RefreshPalette(); UpdateTitle(); Validate(); canvas.MarkDataDirty();
    }
    private void AssetChanged(object? sender, FileSystemEventArgs e) => QueueAssetChange(e.FullPath);
    private void QueueAssetChange(string fullPath)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (project is null) return;
            var path = project.Relative(fullPath);
            if (project.Config.Assets.ContainsKey(path)) ScheduleAssetChange(path);
        });
    }
    private async void ScheduleAssetChange(string path)
    {
        if (project is null) return;
        var currentProject = project;
        var generation = watcherGeneration;
        var serial = queuedSourceChanges.GetValueOrDefault(path) + 1;
        queuedSourceChanges[path] = serial;
        await Task.Delay(400);
        if (project != currentProject || watcherGeneration != generation || queuedSourceChanges.GetValueOrDefault(path) != serial) return;
        if (!currentProject.Config.Assets.TryGetValue(path, out var kind)) return;
        var full = currentProject.Resolve(path);
        if (!File.Exists(full))
        {
            sourceHashes.Remove(path);
            if (!assets.ContainsKey(path)) try { RestoreCached(path); RefreshPalette(); } catch (Exception error) { SetStatus($"Asset {path}: {error.Message}"); return; }
            SetStatus($"Source missing: {path}; using embedded cache.");
            return;
        }
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full)));
                if (sourceHashes.GetValueOrDefault(path) == hash && assets.ContainsKey(path)) return;
                var parsed = ReadAsset(path, kind);
                CachedAsset cached;
                try { if (kind == AssetKind.Tileset) parsed.ValidateTileset(); cached = AssetCache.Create(path, kind, parsed); }
                catch { parsed.Dispose(); throw; }
                UseAsset(path, kind, parsed);
                AssetCache.Replace(document, cached);
                sourceHashes[path] = hash;
                Changed(); RefreshPalette(); Validate();
                var issues = LevelValidator.ValidateDocument(document, currentProject, assets);
                SetStatus(issues.Count == 0 ? $"Reloaded {path}; embedded cache updated." : $"Reloaded {path}; {issues.Count} validation issue(s): {issues[0]}");
                return;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                if (attempt == 3) { SetStatus($"Asset {path}: {error.Message}; previous cache retained."); return; }
                await Task.Delay(200 * (attempt + 1));
                if (project != currentProject || watcherGeneration != generation || queuedSourceChanges.GetValueOrDefault(path) != serial) return;
            }
        }
    }
    private void ClearAssets() { foreach (var item in assets.Values) item.Dispose(); assets.Clear(); foreach (var image in bitmaps.Values) image.Dispose(); bitmaps.Clear(); }
    private void ReloadAssets()
    {
        if (project is null) return;
        ClearAssets(); sourceHashes.Clear();
        foreach (var (path, kind) in project.Config.Assets)
        {
            try { ReloadOne(path, kind); sourceHashes[path] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(project.Resolve(path)))); }
            catch (Exception e) { try { RestoreCached(path); SetStatus($"Using cached asset {path}: {e.Message}"); } catch { SetStatus($"Asset {path}: {e.Message}"); } }
        }
        canvas.InvalidateVisual();
    }
    private void ReloadOne(string path, AssetKind kind)
    {
        if (project is null) return;
        UseAsset(path, kind, ReadAsset(path, kind));
    }
    private AseAsset ReadAsset(string path, AssetKind kind)
    {
        if (project is null) throw new InvalidOperationException("Open a project first.");
        var full = project.Resolve(path);
        var cached = document.CachedAssets.FirstOrDefault(c => c.Path == path);
        var parsed = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? PngAssetReader.Read(full, kind, kind == AssetKind.Tileset ? (int)(cached?.FrameWidth ?? level.TileWidth) : 0,
                kind == AssetKind.Tileset ? (int)(cached?.FrameHeight ?? level.TileHeight) : 0)
            : AsepriteReader.Read(full);
        return parsed;
    }
    private void RestoreCached(string path)
    {
        var cache = document.CachedAssets.FirstOrDefault(c => c.Path == path) ?? throw new InvalidDataException($"No cached asset for {path}");
        if (!Enum.TryParse<AssetKind>(cache.Kind, out var kind)) throw new InvalidDataException($"Invalid cached asset kind: {cache.Kind}");
        UseAsset(path, kind, AssetCache.Restore(cache));
    }
    private void UseAsset(string path, AssetKind kind, AseAsset parsed)
    {
        try
        {
            if (kind == AssetKind.Tileset) parsed.ValidateTileset();
            var newImages = new Dictionary<(string path, int frame), Bitmap>();
            try
            {
                for (var i = 0; i < parsed.Frames.Count; i++)
                {
                    using var data = new MemoryStream(); parsed.Frames[i].Image.SaveAsPng(data); data.Position = 0;
                    newImages[(path, i)] = new Bitmap(data);
                }
            }
            catch { foreach (var image in newImages.Values) image.Dispose(); throw; }
            if (assets.Remove(path, out var old)) old.Dispose();
            foreach (var key in bitmaps.Keys.Where(k => k.path == path).ToArray()) { bitmaps[key].Dispose(); bitmaps.Remove(key); }
            assets[path] = parsed;
            foreach (var (key, image) in newImages) bitmaps[key] = image;
            canvas.InvalidateVisual();
        }
        catch { parsed.Dispose(); throw; }
    }
    private void RefreshCache()
    {
        if (project is null) return;
        document.CachedAssets.Clear();
        foreach (var (path, kind) in project.Config.Assets)
            if (assets.TryGetValue(path, out var asset)) document.CachedAssets.Add(AssetCache.Create(path, kind, asset));
    }
    private async Task Import()
    {
        if (project is null) { await Error("Open a project first."); return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import asset", AllowMultiple = true, FileTypeFilter = [new FilePickerFileType("Aseprite or PNG") { Patterns = ["*.ase", "*.aseprite", "*.png"] }] });
        if (files.Count == 0) return;
        var kind = await Ask("Import as", "Tileset", "Animated sprite"); if (kind is null) return;
        foreach (var file in files)
        {
            try
            {
                var dest = Path.Combine(project.Resolve(project.Config.AssetDirectory), Path.GetFileName(file.Path.LocalPath));
                if (Path.GetFullPath(dest) != Path.GetFullPath(file.Path.LocalPath)) File.Copy(file.Path.LocalPath, dest, true);
                var relative = project.Relative(dest);
                var assetKind = kind == "Tileset" ? AssetKind.Tileset : AssetKind.AnimatedSprite;
                using var parsed = dest.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? PngAssetReader.Read(dest, assetKind, (int)level.TileWidth, (int)level.TileHeight)
                    : AsepriteReader.Read(dest);
                if (kind == "Tileset") parsed.ValidateTileset();
                project.Config.Assets[relative] = assetKind;
            }
            catch (Exception e) { await Error($"{file.Name}: {e.Message}"); }
        }
        ReloadAssets(); RefreshCache(); RefreshPalette(); Changed(); Validate();
    }
    private void AddLevel()
    {
        SyncDocument();
        var n = 1; while (document.Levels.Any(l => l.Name == $"Level {n}")) n++;
        document.Levels.Add(LevelStore.New($"Level {n}", (int)level.Width, (int)level.Height, (int)level.TileWidth, (int)level.TileHeight));
        activeLevelIndex = document.Levels.Count - 1; level = document.Levels[activeLevelIndex]; selected = null; undo.Clear(); redo.Clear();
        RefreshLevels(); RefreshLayers(); Changed();
    }
    private void RemoveLevel()
    {
        if (document.Levels.Count <= 1) { SetStatus("A project needs at least one level."); return; }
        document.Levels.RemoveAt(activeLevelIndex); activeLevelIndex = Math.Min(activeLevelIndex, document.Levels.Count - 1);
        level = document.Levels[activeLevelIndex]; selected = null; undo.Clear(); redo.Clear(); RefreshLevels(); RefreshLayers(); Changed();
    }
    private async Task LevelSettings()
    {
        var dialog = new Window { Title = "Level settings", Width = 340, Height = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var fields = new[] { new TextBox { Text = level.Name }, new TextBox { Text = level.Width.ToString() }, new TextBox { Text = level.Height.ToString() }, new TextBox { Text = level.TileWidth.ToString() }, new TextBox { Text = level.TileHeight.ToString() } };
        var stack = new StackPanel { Margin = new Thickness(12), Spacing = 5 };
        var labels = new[] { "Name", "Width (tiles)", "Height (tiles)", "Tile width (pixels)", "Tile height (pixels)" };
        for (var i = 0; i < fields.Length; i++) { stack.Children.Add(new TextBlock { Text = labels[i] }); stack.Children.Add(fields[i]); }
        var saveButton = new Button { Content = "Apply", HorizontalAlignment = HorizontalAlignment.Right };
        saveButton.Click += (_, _) => dialog.Close(true); stack.Children.Add(saveButton); dialog.Content = new ScrollViewer { Content = stack };
        if (!await dialog.ShowDialog<bool>(this)) return;
        if (fields.Skip(1).Any(f => !uint.TryParse(f.Text, out var v) || v == 0 || v > 100000)) { await Error("Enter positive dimensions (at most 100000)."); return; }
        Commit(); level.Name = fields[0].Text ?? "";
        level.Width = uint.Parse(fields[1].Text!); level.Height = uint.Parse(fields[2].Text!);
        level.TileWidth = uint.Parse(fields[3].Text!); level.TileHeight = uint.Parse(fields[4].Text!);
        Changed(); RefreshLevels(); Validate();
    }
    public void OpenFile(string path) => _ = OpenLevel(path);
    private async Task OpenLevel(string? chosenPath = null)
    {
        if (!await ConfirmDiscard()) return;
        if (chosenPath is null)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, Title = "Open project", FileTypeFilter = [new FilePickerFileType("Level project or bundle") { Patterns = ["*.level", "*.levelz"] }] });
            if (files.Count == 0) return;
            chosenPath = files[0].Path.LocalPath;
        }
        try
        {
            var path = chosenPath;
            if (path.EndsWith(".levelz", StringComparison.OrdinalIgnoreCase))
            {
                var opened = LevelBundle.Open(path);
                await LoadProject(opened.Project.PathName, opened.Project.Config, opened.LevelPath, opened);
                bundlePath = path;
            }
            else
            {
                var config = ProjectConfig.FromSettings(LevelStore.LoadDocument(path).Project);
                await LoadProject(path, config, path);
            }
            selected = null; UpdateTitle(); Validate();
        }
        catch (Exception e) { await Error(e.Message); }
    }
    private async Task LoadWithRecovery(string path, ProjectConfig config)
    {
        levelPath = path; dirty = false;
        var recovery = path + ".recovery";
        var recover = File.Exists(recovery) && File.GetLastWriteTimeUtc(recovery) > File.GetLastWriteTimeUtc(path)
            && await Ask("A newer recovery file exists. Open recovered changes?", "Recover", "Original") == "Recover";
        document = LevelStore.LoadDocument(recover ? recovery : path, config);
        activeLevelIndex = 0; level = document.Levels[0];
        dirty = recover;
    }
    private async Task Save(bool asNew = false)
    {
        if (project is null) { await Error("Open a project first."); return; }
        SyncDocument(); RefreshCache();
        var issues = LevelValidator.ValidateDocument(document, project, assets);
        if (issues.Count > 0) { await Error("Fix validation errors before saving:\n" + string.Join("\n", issues.Take(20))); return; }
        var path = asNew ? null : bundlePath ?? levelPath;
        if (path is null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save project", SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(project.Resolve(project.Config.LevelDirectory)), SuggestedFileName = "game.level", FileTypeChoices = [new FilePickerFileType("Protobuf level") { Patterns = ["*.level"] }, new FilePickerFileType("Shareable bundle") { Patterns = ["*.levelz"] }] });
            if (file is null) return; path = file.Path.LocalPath;
        }
        try
        {
            if (path.EndsWith(".levelz", StringComparison.OrdinalIgnoreCase))
            {
                if (bundleWorkspace is not null) LevelStore.SaveDocument(document, bundleWorkspace.LevelPath);
                LevelBundle.Export(document, project, path);
                bundlePath = path;
            }
            else { LevelStore.SaveDocument(document, path); levelPath = path; bundlePath = null; }
            dirty = false; UpdateTitle();
            if (levelPath is not null && File.Exists(levelPath + ".recovery")) File.Delete(levelPath + ".recovery");
            SetStatus("Saved " + path);
        }
        catch (Exception e) { await Error(e.Message); }
    }
    private async Task Export()
    {
        if (project is null) { await Error("Open a project first."); return; }
        SyncDocument(); RefreshCache();
        var issues = LevelValidator.ValidateDocument(document, project, assets);
        if (issues.Count > 0) { await Error("Fix validation errors before export:\n" + string.Join("\n", issues.Take(20))); return; }
        try
        {
            var output = project.Resolve("build"); Directory.CreateDirectory(output);
            AssetExporter.Export(project, assets, Path.Combine(output, "assets"));
            File.WriteAllText(Path.Combine(output, "types.go"), GoGenerator.Generate(project.Config));
            LevelStore.SaveDocument(document, Path.Combine(output, "game.level"));
            SetStatus("Exported assets, Go types, and level to " + output);
        }
        catch (Exception e) { await Error(e.Message); }
    }
    private async Task ExportBundle()
    {
        if (project is null) { await Error("Open a project first."); return; }
        SyncDocument(); RefreshCache();
        var issues = LevelValidator.ValidateDocument(document, project, assets);
        if (issues.Count > 0) { await Error("Fix validation errors before sharing:\n" + string.Join("\n", issues.Take(20))); return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Share project", SuggestedFileName = "game.levelz", FileTypeChoices = [new FilePickerFileType("Shareable level bundle") { Patterns = ["*.levelz"] }] });
        if (file is null) return;
        try { LevelBundle.Export(document, project, file.Path.LocalPath); SetStatus("Exported " + file.Path.LocalPath); }
        catch (Exception e) { await Error(e.Message); }
    }
    private async Task DebugJson()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = "level.debug.json", FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }] });
        if (file is null) return;
        try { SyncDocument(); File.WriteAllText(file.Path.LocalPath, LevelStore.DebugJson(document)); SetStatus("Saved debug JSON"); }
        catch (Exception e) { await Error(e.Message); }
    }
    private void Validate()
    {
        if (project is null) return;
        SyncDocument();
        var issues = LevelValidator.ValidateDocument(document, project, assets);
        SetStatus(issues.Count == 0 ? "Level valid" : $"{issues.Count} validation issue(s): {issues[0]}");
    }
    private async Task CheckUpdates(bool silent = false)
    {
        try
        {
            var release = await UpdateService.CheckAsync();
            if (release is null) { if (!silent) SetStatus("Already up to date."); return; }
            var answer = await Ask($"Version {release.Tag} is available. Download and restart to update?", "Update", "Later");
            if (answer != "Update") return;
            if (dirty) { await Save(); if (dirty) return; }
            var progress = new Progress<double>(p => SetStatus($"Downloading update: {p:P0}"));
            await UpdateService.DownloadAndRestartAsync(release, progress);
            dirty = false; Close();
        }
        catch (Exception e) { if (!silent) await Error("Update failed: " + e.Message); }
    }
    private async Task<bool> ConfirmDiscard() => !dirty || await Ask("Unsaved changes will be lost.", "Discard", "Cancel") == "Discard";
    private async Task ConfirmClose() { if (!await ConfirmDiscard()) return; dirty = false; Close(); }
    private async Task<string?> Ask(string message, string yes, string no)
    {
        var dialog = new Window { Title = "LevelBuilder", Width = 420, Height = 150, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        foreach (var label in new[] { yes, no }) { var b = new Button { Content = label }; b.Click += (_, _) => dialog.Close(label); buttons.Children.Add(b); }
        dialog.Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, buttons } };
        return await dialog.ShowDialog<string?>(this);
    }
    private async Task Error(string message) { SetStatus(message); await Ask(message, "OK", "Close"); }
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox && !(e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.S)) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.S: _ = Save(); break;
                case Key.Z: Undo(); break;
                case Key.Y: Redo(); break;
                case Key.C: canvas.Copy(); break;
                case Key.X: canvas.Copy(); canvas.Delete(); break;
                case Key.V: canvas.Paste(); break;
                case Key.D: canvas.Duplicate(); break;
                case Key.O: _ = OpenLevel(); break;
                default: return;
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Delete) { canvas.Delete(); e.Handled = true; }
        else if (e.Key == Key.I) { canvas.Tool = EditorTool.Pick; e.Handled = true; }
        else if (e.Key == Key.Tab) { mode.SelectedIndex = mode.SelectedIndex == 0 ? 1 : 0; e.Handled = true; }
        else if (e.Key is Key.Add or Key.OemPlus) { canvas.ZoomAt(1.2, new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2)); e.Handled = true; }
        else if (e.Key is Key.Subtract or Key.OemMinus) { canvas.ZoomAt(1 / 1.2, new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2)); e.Handled = true; }
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) { canvas.MoveSelection(e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0, e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0); }
    }
    private void ShowLayerProperties()
    {
        if (selected is not null) return;
        properties.Children.Clear();
        var layer = ActiveLayer; if (layer is null) return;
        properties.Children.Add(new TextBlock { Text = "Active tile layer" });
        Field("Name", layer.Name, text => { Commit(); layer.Name = text; RefreshLayers(); Changed(); });
        Toggle("Visible", layer.Visible, value => { Commit(); layer.Visible = value; RefreshLayers(); Changed(); });
        Toggle("Locked", layer.Locked, value => { Commit(); layer.Locked = value; RefreshLayers(); Changed(); });
        ActionButton("Move layer up", () => ReorderLayer(-1)); ActionButton("Move layer down", () => ReorderLayer(1));
    }
    private void ReorderLayer(int change)
    {
        var from = layers.SelectedIndex; var to = from + change;
        if (from < 0 || to < 0 || to >= level.VisualLayers.Count) return;
        Commit(); var item = level.VisualLayers[from]; level.VisualLayers.RemoveAt(from); level.VisualLayers.Insert(to, item); RefreshLayers(); layers.SelectedIndex = to; Changed();
    }
    private void ShowSelected()
    {
        properties.Children.Clear();
        if (selected is SpriteObject sprite)
        {
            properties.Children.Add(new TextBlock { Text = "Sprite · " + sprite.Asset });
            Field("Animation", sprite.Animation, text => { Commit(); sprite.Animation = text; Changed(); });
            Field("Frame (when no animation)", sprite.Frame.ToString(), text => { if (uint.TryParse(text, out var v)) { Commit(); sprite.Frame = v; Changed(); } });
            Field("Draw order", sprite.DrawOrder.ToString(), text => { if (int.TryParse(text, out var v)) { Commit(); sprite.DrawOrder = v; Changed(); } });
            Toggle("Loop", sprite.Loop, v => { Commit(); sprite.Loop = v; Changed(); });
            Toggle("Flip horizontal", sprite.FlipX, v => { Commit(); sprite.FlipX = v; Changed(); });
            Toggle("Flip vertical", sprite.FlipY, v => { Commit(); sprite.FlipY = v; Changed(); });
            ActionButton("Duplicate", canvas.Duplicate); ActionButton("Delete", canvas.Delete);
        }
        else if (selected is EntityAnchor anchor)
        {
            properties.Children.Add(new TextBlock { Text = "Anchor" });
            Field("Name", anchor.Name, text => { Commit(); anchor.Name = text; Changed(); });
            properties.Children.Add(new TextBlock { Text = "Type" });
            var types = new ComboBox { ItemsSource = project?.Config.AnchorTypes ?? [], SelectedIndex = (int)anchor.Type };
            types.SelectionChanged += (_, _) => { if (types.SelectedIndex < 0 || types.SelectedIndex == anchor.Type) return; Commit(); anchor.Type = (uint)types.SelectedIndex; Changed(); };
            properties.Children.Add(types);
            ActionButton("Duplicate", canvas.Duplicate); ActionButton("Delete", canvas.Delete);
        }
        else ShowLayerProperties();
    }
    internal void ShowRegionProperties((int left, int top, int right, int bottom) region, bool colliders)
    {
        properties.Children.Clear();
        properties.Children.Add(new TextBlock { Text = colliders ? "Collider selection" : "Tile selection" });
        properties.Children.Add(new TextBlock { Text = $"({Math.Min(region.left, region.right)}, {Math.Min(region.top, region.bottom)}) to ({Math.Max(region.left, region.right)}, {Math.Max(region.top, region.bottom)})" });
        ActionButton("Copy", canvas.Copy); ActionButton("Cut", () => { canvas.Copy(); canvas.Delete(); }); ActionButton("Delete", canvas.Delete);
    }
    private void Field(string label, string text, Action<string> set)
    {
        properties.Children.Add(new TextBlock { Text = label });
        var field = new TextBox { Text = text }; field.LostFocus += (_, _) => { if (field.Text != text) set(field.Text ?? ""); }; properties.Children.Add(field);
    }
    private void Toggle(string label, bool value, Action<bool> set)
    {
        var check = new CheckBox { Content = label, IsChecked = value }; check.Click += (_, _) => set(check.IsChecked == true); properties.Children.Add(check);
    }
    private void ActionButton(string label, Action action) { var button = new Button { Content = label }; button.Click += (_, _) => action(); properties.Children.Add(button); }
}
