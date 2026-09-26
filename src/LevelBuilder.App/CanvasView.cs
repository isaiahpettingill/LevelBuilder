using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LevelBuilder.Core.Format;

namespace LevelBuilder.App;

public sealed class CanvasView(EditorWindow editor) : Control
{
    public EditorTool Tool { get; set; } = EditorTool.Pencil;
    private double zoom = 2;
    private Vector pan = new(60, 60);
    private bool dragging;
    private bool panning;
    private Point start;
    private Point last;
    private (int x, int y) startCell;
    private (int x, int y) hover;
    private object? moving;
    private readonly List<(int x, int y, TileReference? tile, uint property)> clipboard = [];
    private bool clipboardCollider;
    private (int left, int top, int right, int bottom)? selection;
    private readonly Dictionary<TileLayer, Dictionary<(int, int), TileCell>> tileIndexes = [];
    private readonly Dictionary<(int, int), ColliderCell> colliderIndex = [];
    private bool indexDirty = true;
    private int randomSeed = 0;
    private readonly IBrush background = new SolidColorBrush(Color.Parse("#1d222b"));
    private readonly IBrush gridBrush = new SolidColorBrush(Color.Parse("#3a4352"));
    private readonly IBrush selectionBrush = new SolidColorBrush(Color.Parse("#55f4d35e"));
    private readonly IBrush[] colliderBrushes = [Brushes.Transparent, Brushes.Green, Brushes.Red, Brushes.Blue, Brushes.Yellow, Brushes.Orange, Brushes.Purple, Brushes.Cyan, Brushes.Magenta];
    private double TileW => editor.Level.TileWidth * zoom;
    private double TileH => editor.Level.TileHeight * zoom;
    private Point World(Point screen) => new((screen.X - pan.X) / zoom, (screen.Y - pan.Y) / zoom);
    private Point Screen(float x, float y) => new(x * zoom + pan.X, y * zoom + pan.Y);
    private (int x, int y) Cell(Point position) { var w = World(position); return ((int)Math.Floor(w.X / editor.Level.TileWidth), (int)Math.Floor(w.Y / editor.Level.TileHeight)); }
    private bool Inside(int x, int y) => x >= 0 && y >= 0 && x < editor.Level.Width && y < editor.Level.Height;
    public void MarkDataDirty() { indexDirty = true; base.InvalidateVisual(); }
    private void Index()
    {
        if (!indexDirty) return;
        tileIndexes.Clear(); colliderIndex.Clear();
        foreach (var layer in editor.Level.VisualLayers)
        {
            var dictionary = new Dictionary<(int, int), TileCell>();
            foreach (var tile in layer.Tiles) dictionary[(tile.X, tile.Y)] = tile;
            tileIndexes[layer] = dictionary;
        }
        foreach (var cell in editor.Level.Colliders) colliderIndex[(cell.X, cell.Y)] = cell;
        indexDirty = false;
    }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(background, new Rect(Bounds.Size));
        Index();
        var level = editor.Level;
        var min = Cell(new Point(0, 0)); var max = Cell(new Point(Bounds.Width, Bounds.Height));
        var left = Math.Max(0, min.x); var top = Math.Max(0, min.y);
        var right = Math.Min((int)level.Width - 1, max.x + 1); var bottom = Math.Min((int)level.Height - 1, max.y + 1);
        context.DrawRectangle(null, new Pen(Brushes.SlateGray), new Rect(pan.X, pan.Y, level.Width * TileW, level.Height * TileH));
        foreach (var layer in level.VisualLayers)
        {
            if (!layer.Visible) continue;
            var dictionary = tileIndexes[layer];
            for (var y = top; y <= bottom; y++) for (var x = left; x <= right; x++)
            {
                if (!dictionary.TryGetValue((x, y), out var cell) || cell.Tile is null) continue;
                var reference = cell.Tile;
                var frame = reference.IdentityCase == TileReference.IdentityOneofCase.Tagged
                    ? editor.Assets.GetValueOrDefault(reference.Tileset)?.FrameFor(reference.Tagged.Tag, (int)reference.Tagged.Variant)
                    : (int?)reference.UnstableFrame;
                if (frame is null) continue;
                var bitmap = editor.Bitmap(reference.Tileset, frame.Value);
                if (bitmap is not null) context.DrawImage(bitmap, new Rect(bitmap.Size), new Rect(pan.X + x * TileW, pan.Y + y * TileH, TileW, TileH));
            }
        }
        var seconds = DateTime.UtcNow.TimeOfDay.TotalMilliseconds;
        foreach (var sprite in level.Sprites.OrderBy(s => s.DrawOrder))
        {
            if (!editor.Assets.TryGetValue(sprite.Asset, out var asset)) continue;
            var frame = (int)sprite.Frame;
            var tag = asset.Tags.FirstOrDefault(t => t.Name == sprite.Animation);
            if (tag is not null)
            {
                frame = tag.From;
                if (sprite.Loop)
                {
                    var sequence = Enumerable.Range(tag.From, tag.To - tag.From + 1).ToList();
                    if (tag.Direction == 1) sequence.Reverse();
                    if (tag.Direction is 2 or 3 && sequence.Count > 1) sequence.AddRange(sequence.Skip(1).SkipLast(1).Reverse());
                    if (tag.Direction == 3) sequence.Reverse();
                    var duration = sequence.Sum(i => asset.Frames[i].DurationMs);
                    var elapsed = seconds % Math.Max(duration, 1);
                    foreach (var i in sequence) { frame = i; elapsed -= asset.Frames[i].DurationMs; if (elapsed < 0) break; }
                }
            }
            var image = editor.Bitmap(sprite.Asset, frame); if (image is null) continue;
            var point = Screen(sprite.X, sprite.Y); var rect = new Rect(point, new Size(asset.Width * zoom, asset.Height * zoom));
            if (!rect.Intersects(new Rect(Bounds.Size))) continue;
            if (sprite.FlipX || sprite.FlipY)
            {
                using (context.PushTransform(Matrix.CreateTranslation(-rect.X, -rect.Y) * Matrix.CreateScale(sprite.FlipX ? -1 : 1, sprite.FlipY ? -1 : 1) * Matrix.CreateTranslation(rect.X + (sprite.FlipX ? rect.Width : 0), rect.Y + (sprite.FlipY ? rect.Height : 0))))
                    context.DrawImage(image, new Rect(image.Size), rect);
            }
            else context.DrawImage(image, new Rect(image.Size), rect);
            if (ReferenceEquals(editor.Selected, sprite)) context.DrawRectangle(null, new Pen(Brushes.Yellow, 2), rect);
        }
        if (editor.Mode == 1)
        {
            for (var y = top; y <= bottom; y++) for (var x = left; x <= right; x++)
            {
                if (!colliderIndex.TryGetValue((x, y), out var cell)) continue;
                var brush = colliderBrushes[cell.Property % (uint)colliderBrushes.Length];
                context.FillRectangle(new SolidColorBrush(((SolidColorBrush)brush).Color, 0.5), new Rect(pan.X + x * TileW, pan.Y + y * TileH, TileW, TileH));
            }
        }
        foreach (var anchor in level.Anchors)
        {
            var point = Screen(anchor.X, anchor.Y);
            if (!new Rect(Bounds.Size).Contains(point)) continue;
            var color = ReferenceEquals(editor.Selected, anchor) ? Brushes.Yellow : Brushes.DeepSkyBlue;
            context.DrawEllipse(color, new Pen(Brushes.Black, 1), point, 7, 7);
            Text(context, anchor.Name.Length == 0 ? AnchorName(anchor.Type) : $"{AnchorName(anchor.Type)}: {anchor.Name}", point + new Vector(10, -8), color);
        }
        if (editor.GridVisible && TileW >= 8 && TileH >= 8)
        {
            var pen = new Pen(gridBrush, 1);
            for (var x = left; x <= right + 1; x++) context.DrawLine(pen, new Point(pan.X + x * TileW, Math.Max(0, pan.Y + top * TileH)), new Point(pan.X + x * TileW, Math.Min(Bounds.Height, pan.Y + (bottom + 1) * TileH)));
            for (var y = top; y <= bottom + 1; y++) context.DrawLine(pen, new Point(Math.Max(0, pan.X + left * TileW), pan.Y + y * TileH), new Point(Math.Min(Bounds.Width, pan.X + (right + 1) * TileW), pan.Y + y * TileH));
        }
        if (selection is { } region)
        {
            var rect = RegionRect(region);
            context.DrawRectangle(selectionBrush, new Pen(Brushes.Yellow, 1), rect);
        }
        if (dragging && Tool == EditorTool.Rectangle && editor.Mode < 2)
            context.DrawRectangle(selectionBrush, new Pen(Brushes.Yellow, 1), RegionRect((startCell.x, startCell.y, hover.x, hover.y)));
    }
    private string AnchorName(uint type) => editor.Project?.Config.AnchorTypes.ElementAtOrDefault((int)type) ?? $"Anchor {type}";
    private static void Text(DrawingContext context, string text, Point location, IBrush brush) => context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, brush), location);
    private Rect RegionRect((int left, int top, int right, int bottom) r) => new(pan.X + Math.Min(r.left, r.right) * TileW, pan.Y + Math.Min(r.top, r.bottom) * TileH, (Math.Abs(r.left - r.right) + 1) * TileW, (Math.Abs(r.top - r.bottom) + 1) * TileH);
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { ZoomAt(e.Delta.Y > 0 ? 1.15 : 1 / 1.15, e.GetPosition(this)); e.Handled = true; }
    public void ZoomAt(double multiplier, Point center)
    {
        var world = World(center); zoom = Math.Clamp(zoom * multiplier, 0.25, 16);
        pan = new Vector(center.X - world.X * zoom, center.Y - world.Y * zoom); InvalidateVisual();
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus(); start = last = e.GetPosition(this); hover = startCell = Cell(start);
        var pointer = e.GetCurrentPoint(this).Properties;
        if (pointer.IsMiddleButtonPressed) { panning = true; e.Pointer.Capture(this); return; }
        if (!pointer.IsLeftButtonPressed) return;
        dragging = true; e.Pointer.Capture(this);
        if (Tool == EditorTool.Select)
        {
            moving = HitTestObject(start);
            if (moving is not null) { editor.Selected = moving; editor.Commit(); }
            else { selection = (hover.x, hover.y, hover.x, hover.y); editor.Selected = null; editor.ShowRegionProperties(selection.Value, editor.Mode == 1); }
            InvalidateVisual(); return;
        }
        if (Tool == EditorTool.Pick) { Pick(hover.x, hover.y); dragging = false; return; }
        if (Tool is EditorTool.Sprite or EditorTool.Anchor || editor.Mode >= 2) { PlaceObject(start); dragging = false; return; }
        if (Tool == EditorTool.Flood) { editor.Commit(); Flood(hover.x, hover.y); editor.Changed(); dragging = false; return; }
        editor.Commit(); randomSeed = Random.Shared.Next();
        if (Tool != EditorTool.Rectangle) { Paint(hover.x, hover.y); editor.Changed(); }
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var position = e.GetPosition(this);
        hover = Cell(position);
        var world = World(position);
        editor.SetStatus($"Level ({world.X:0}, {world.Y:0}) · tile ({hover.x}, {hover.y}) · {Tool} · {zoom:P0}");
        if (panning) { pan += position - last; InvalidateVisual(); }
        else if (dragging && moving is SpriteObject sprite)
        {
            var delta = (position - last) / zoom; sprite.X += (float)delta.X; sprite.Y += (float)delta.Y; SnapObject(sprite); editor.Changed();
        }
        else if (dragging && moving is EntityAnchor anchor)
        {
            var delta = (position - last) / zoom; anchor.X += (float)delta.X; anchor.Y += (float)delta.Y; SnapObject(anchor); editor.Changed();
        }
        else if (dragging && Tool == EditorTool.Select && moving is null && selection is not null)
        { selection = (startCell.x, startCell.y, hover.x, hover.y); editor.ShowRegionProperties(selection.Value, editor.Mode == 1); InvalidateVisual(); }
        else if (dragging && Tool is EditorTool.Pencil or EditorTool.Erase && editor.Mode < 2)
        { PaintLine(Cell(last), hover); editor.Changed(); }
        else InvalidateVisual();
        last = position;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (dragging && Tool == EditorTool.Rectangle && editor.Mode < 2)
        {
            for (var y = Math.Max(0, Math.Min(startCell.y, hover.y)); y <= Math.Min((int)editor.Level.Height - 1, Math.Max(startCell.y, hover.y)); y++)
                for (var x = Math.Max(0, Math.Min(startCell.x, hover.x)); x <= Math.Min((int)editor.Level.Width - 1, Math.Max(startCell.x, hover.x)); x++) Paint(x, y);
            editor.Changed();
        }
        moving = null; dragging = panning = false; e.Pointer.Capture(null); InvalidateVisual();
    }
    private void PaintLine((int x, int y) from, (int x, int y) to)
    {
        var dx = Math.Abs(to.x - from.x); var dy = -Math.Abs(to.y - from.y);
        var sx = from.x < to.x ? 1 : -1; var sy = from.y < to.y ? 1 : -1; var error = dx + dy;
        for (var i = 0; i < 10000; i++)
        {
            Paint(from.x, from.y);
            if (from == to) break;
            var e2 = 2 * error;
            if (e2 >= dy) { error += dy; from.x += sx; }
            if (e2 <= dx) { error += dx; from.y += sy; }
        }
    }
    private void Paint(int x, int y)
    {
        if (!Inside(x, y)) return;
        Index();
        if (editor.Mode == 1)
        {
            var value = Tool == EditorTool.Erase ? 0 : editor.ColliderValue;
            if (colliderIndex.TryGetValue((x, y), out var old)) { editor.Level.Colliders.Remove(old); colliderIndex.Remove((x, y)); }
            if (value > 0) { var cell = new ColliderCell { X = x, Y = y, Property = (uint)value }; editor.Level.Colliders.Add(cell); colliderIndex[(x, y)] = cell; }
            return;
        }
        var layer = editor.ActiveLayer; if (layer is null || layer.Locked) return;
        var index = tileIndexes[layer];
        if (index.TryGetValue((x, y), out var existing)) { layer.Tiles.Remove(existing); index.Remove((x, y)); }
        if (Tool == EditorTool.Erase || editor.Choice is not { } choice) return;
        var reference = choice.Reference();
        if (editor.RandomVariants && choice.Tag is not null && editor.Assets.TryGetValue(choice.Asset, out var asset))
        {
            var tag = asset.Tags.First(t => t.Name == choice.Tag);
            reference.Tagged.Variant = (uint)(unchecked((uint)HashCode.Combine(x, y, randomSeed)) % (tag.To - tag.From + 1));
        }
        var added = new TileCell { X = x, Y = y, Tile = reference };
        layer.Tiles.Add(added); index[(x, y)] = added;
        if (!editor.Level.Tilesets.Contains(choice.Asset)) editor.Level.Tilesets.Add(choice.Asset);
    }
    private void Flood(int x, int y)
    {
        if (!Inside(x, y)) return;
        Index();
        var mode = editor.Mode;
        var tiles = editor.ActiveLayer is { } layer ? tileIndexes[layer] : null;
        var target = mode == 1 ? colliderIndex.GetValueOrDefault((x, y))?.Property.ToString() ?? "" : tiles?.GetValueOrDefault((x, y))?.Tile?.ToString() ?? "";
        var replacement = mode == 1 ? (Tool == EditorTool.Erase ? "" : editor.ColliderValue.ToString()) : editor.Choice?.Reference().ToString() ?? "";
        if (target == replacement) return;
        var visited = new HashSet<(int, int)>(); var pending = new Queue<(int x, int y)>(); pending.Enqueue((x, y));
        while (pending.Count > 0 && visited.Count < 1_000_000)
        {
            var current = pending.Dequeue(); if (!Inside(current.x, current.y) || !visited.Add(current)) continue;
            var currentValue = mode == 1 ? colliderIndex.GetValueOrDefault(current)?.Property.ToString() ?? "" : tiles?.GetValueOrDefault(current)?.Tile?.ToString() ?? "";
            if (currentValue != target) continue;
            Paint(current.x, current.y);
            pending.Enqueue((current.x - 1, current.y)); pending.Enqueue((current.x + 1, current.y)); pending.Enqueue((current.x, current.y - 1)); pending.Enqueue((current.x, current.y + 1));
        }
    }
    private void Pick(int x, int y)
    {
        Index();
        if (editor.Mode == 1) { editor.SelectCollider(colliderIndex.GetValueOrDefault((x, y))?.Property ?? 0); return; }
        var reference = editor.ActiveLayer is { } layer ? tileIndexes[layer].GetValueOrDefault((x, y))?.Tile : null;
        if (reference is not null) editor.SelectPaletteReference(reference);
    }
    private object? HitTestObject(Point position)
    {
        var world = World(position);
        var anchor = editor.Level.Anchors.LastOrDefault(a => Math.Abs(a.X - world.X) < 10 / zoom && Math.Abs(a.Y - world.Y) < 10 / zoom);
        if (anchor is not null) return anchor;
        return editor.Level.Sprites.LastOrDefault(s => editor.Assets.TryGetValue(s.Asset, out var asset) && world.X >= s.X && world.Y >= s.Y && world.X < s.X + asset.Width && world.Y < s.Y + asset.Height);
    }
    private void PlaceObject(Point position)
    {
        var world = World(position); editor.Commit();
        if (Tool == EditorTool.Anchor || editor.Mode == 3)
        {
            var anchor = new EntityAnchor { Id = Guid.NewGuid().ToString("N"), Type = (uint)Math.Max(0, editor.AnchorValue), X = (float)world.X, Y = (float)world.Y };
            SnapObject(anchor); editor.Level.Anchors.Add(anchor); editor.Selected = anchor;
        }
        else if (editor.SpriteAsset is { } asset)
        {
            var sprite = new SpriteObject { Id = Guid.NewGuid().ToString("N"), Asset = asset, Animation = editor.AnimationName, Loop = true, X = (float)world.X, Y = (float)world.Y };
            SnapObject(sprite); editor.Level.Sprites.Add(sprite); editor.Selected = sprite;
        }
        editor.Changed();
    }
    private void SnapObject(SpriteObject s) { if (!editor.Snapping) return; s.X = MathF.Round(s.X / editor.Level.TileWidth) * editor.Level.TileWidth; s.Y = MathF.Round(s.Y / editor.Level.TileHeight) * editor.Level.TileHeight; }
    private void SnapObject(EntityAnchor a) { if (!editor.Snapping) return; a.X = MathF.Round(a.X / editor.Level.TileWidth) * editor.Level.TileWidth; a.Y = MathF.Round(a.Y / editor.Level.TileHeight) * editor.Level.TileHeight; }
    public void Delete()
    {
        if (editor.Selected is SpriteObject sprite) { editor.Commit(); editor.Level.Sprites.Remove(sprite); editor.Selected = null; editor.Changed(); }
        else if (editor.Selected is EntityAnchor anchor) { editor.Commit(); editor.Level.Anchors.Remove(anchor); editor.Selected = null; editor.Changed(); }
        else if (selection is { } s)
        {
            editor.Commit(); var left = Math.Min(s.left, s.right); var right = Math.Max(s.left, s.right); var top = Math.Min(s.top, s.bottom); var bottom = Math.Max(s.top, s.bottom);
            if (editor.Mode == 1) { foreach (var cell in editor.Level.Colliders.Where(c => c.X >= left && c.X <= right && c.Y >= top && c.Y <= bottom).ToArray()) editor.Level.Colliders.Remove(cell); }
            else if (editor.ActiveLayer is { } layer) { foreach (var tile in layer.Tiles.Where(t => t.X >= left && t.X <= right && t.Y >= top && t.Y <= bottom).ToArray()) layer.Tiles.Remove(tile); }
            selection = null; editor.Selected = null; editor.Changed();
        }
    }
    public void Copy()
    {
        clipboard.Clear();
        if (editor.Selected is SpriteObject sprite) { clipboard.Add((0, 0, null, 0)); objectClipboard = sprite.Clone(); return; }
        if (editor.Selected is EntityAnchor anchor) { clipboard.Add((0, 0, null, 0)); objectClipboard = anchor.Clone(); return; }
        objectClipboard = null;
        if (selection is not { } region) return;
        var left = Math.Min(region.left, region.right); var right = Math.Max(region.left, region.right);
        var top = Math.Min(region.top, region.bottom); var bottom = Math.Max(region.top, region.bottom);
        clipboardCollider = editor.Mode == 1;
        if (clipboardCollider) clipboard.AddRange(editor.Level.Colliders.Where(c => c.X >= left && c.X <= right && c.Y >= top && c.Y <= bottom).Select(c => (c.X - left, c.Y - top, (TileReference?)null, c.Property)));
        else if (editor.ActiveLayer is { } layer) clipboard.AddRange(layer.Tiles.Where(c => c.X >= left && c.X <= right && c.Y >= top && c.Y <= bottom).Select(c => (c.X - left, c.Y - top, c.Tile?.Clone(), 0u)));
    }
    private object? objectClipboard;
    public void Paste()
    {
        if (objectClipboard is SpriteObject sprite)
        {
            editor.Commit(); var copy = sprite.Clone(); copy.Id = Guid.NewGuid().ToString("N"); copy.X += editor.Level.TileWidth; copy.Y += editor.Level.TileHeight; editor.Level.Sprites.Add(copy); editor.Selected = copy; editor.Changed(); return;
        }
        if (objectClipboard is EntityAnchor anchor)
        {
            editor.Commit(); var copy = anchor.Clone(); copy.Id = Guid.NewGuid().ToString("N"); copy.Name = ""; copy.X += editor.Level.TileWidth; copy.Y += editor.Level.TileHeight; editor.Level.Anchors.Add(copy); editor.Selected = copy; editor.Changed(); return;
        }
        if (clipboard.Count == 0) return;
        editor.Commit(); Index();
        foreach (var item in clipboard)
        {
            var x = hover.x + item.x; var y = hover.y + item.y; if (!Inside(x, y)) continue;
            if (clipboardCollider)
            {
                if (colliderIndex.TryGetValue((x, y), out var old)) editor.Level.Colliders.Remove(old);
                editor.Level.Colliders.Add(new ColliderCell { X = x, Y = y, Property = item.property });
            }
            else if (editor.ActiveLayer is { Locked: false } layer && item.tile is not null)
            {
                if (tileIndexes[layer].TryGetValue((x, y), out var old)) layer.Tiles.Remove(old);
                layer.Tiles.Add(new TileCell { X = x, Y = y, Tile = item.tile.Clone() });
            }
        }
        selection = null; editor.Changed();
    }
    public void Duplicate() { Copy(); Paste(); }
    public void MoveSelection(int dx, int dy)
    {
        if (editor.Selected is SpriteObject s) { editor.Commit(); s.X += dx * (editor.Snapping ? editor.Level.TileWidth : 1); s.Y += dy * (editor.Snapping ? editor.Level.TileHeight : 1); editor.Changed(); }
        else if (editor.Selected is EntityAnchor a) { editor.Commit(); a.X += dx * (editor.Snapping ? editor.Level.TileWidth : 1); a.Y += dy * (editor.Snapping ? editor.Level.TileHeight : 1); editor.Changed(); }
        else { pan += new Vector(dx * 20, dy * 20); InvalidateVisual(); }
    }
}
