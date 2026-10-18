using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace HandBrakeTreemap.Controls;

/// <summary>A single rectangle in the treemap.</summary>
public sealed class TreemapTile
{
    public double Weight { get; init; }        // relative area (e.g. file size in bytes)
    public IBrush Fill { get; init; } = Brushes.Gray;
    public string Label { get; init; } = string.Empty;
    public object? Tag { get; init; }          // the backing VideoFile
    public Func<bool>? IsSelected { get; init; } // read live so we don't rebuild on toggle
}

/// <summary>
/// WinDirStat-style squarified treemap. Area ∝ Weight, colour comes from each tile's Fill.
/// Raises events on hover and click; selection is read live via <see cref="TreemapTile.IsSelected"/>.
/// </summary>
public sealed class TreemapControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<TreemapTile>?> TilesProperty =
        AvaloniaProperty.Register<TreemapControl, IReadOnlyList<TreemapTile>?>(nameof(Tiles));

    public IReadOnlyList<TreemapTile>? Tiles
    {
        get => GetValue(TilesProperty);
        set => SetValue(TilesProperty, value);
    }

    public event EventHandler<TreemapTile?>? TileHovered;
    public event EventHandler<TreemapTile>? TileClicked;

    private readonly List<(TreemapTile tile, Rect rect)> _layout = new();
    private Size _layoutSize;
    private IReadOnlyList<TreemapTile>? _layoutTiles;
    private TreemapTile? _hover;

    private static readonly IPen GapPen = new Pen(new SolidColorBrush(Color.FromArgb(160, 20, 22, 28)), 1);
    private static readonly IPen HoverPen = new Pen(Brushes.White, 2);
    private static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.FromRgb(88, 166, 255)), 2.5);

    static TreemapControl()
    {
        AffectsRender<TreemapControl>(TilesProperty);
    }

    public TreemapControl()
    {
        ClipToBounds = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TilesProperty || change.Property == BoundsProperty)
        {
            _layoutTiles = null; // force relayout
            InvalidateVisual();
        }
    }

    private void EnsureLayout()
    {
        var size = Bounds.Size;
        if (_layoutTiles == Tiles && _layoutSize == size && _layout.Count > 0)
            return;

        _layout.Clear();
        _layoutSize = size;
        _layoutTiles = Tiles;

        if (Tiles == null || size.Width < 2 || size.Height < 2)
            return;

        var tiles = Tiles.Where(t => t.Weight > 0)
                         .OrderByDescending(t => t.Weight)
                         .ToList();
        if (tiles.Count == 0)
            return;

        double totalWeight = tiles.Sum(t => t.Weight);
        double totalArea = size.Width * size.Height;
        double scale = totalArea / totalWeight;

        var nodes = tiles.Select(t => (tile: t, area: t.Weight * scale)).ToList();
        Squarify(nodes, new Rect(0, 0, size.Width, size.Height), _layout);
    }

    // --- Squarified treemap (Bruls, Huizing, van Wijk) ---------------------------------
    private static void Squarify(
        List<(TreemapTile tile, double area)> nodes,
        Rect rect,
        List<(TreemapTile, Rect)> output)
    {
        int start = 0;
        while (start < nodes.Count && rect.Width > 0.5 && rect.Height > 0.5)
        {
            double shortSide = Math.Min(rect.Width, rect.Height);

            // Grow the current row while the worst aspect ratio keeps improving.
            double sum = 0;
            double prevWorst = double.MaxValue;
            int i = start;
            while (i < nodes.Count)
            {
                double newSum = sum + nodes[i].area;
                double worst = Worst(nodes, start, i, newSum, shortSide);
                if (worst > prevWorst) break;
                sum = newSum;
                prevWorst = worst;
                i++;
            }

            rect = LayoutRow(nodes, start, i, sum, rect, output);
            start = i;
        }
    }

    private static double Worst(
        List<(TreemapTile tile, double area)> nodes,
        int start, int endInclusive, double sum, double w)
    {
        double max = double.MinValue, min = double.MaxValue;
        for (int k = start; k <= endInclusive; k++)
        {
            double a = nodes[k].area;
            if (a > max) max = a;
            if (a < min) min = a;
        }
        if (min <= 0 || sum <= 0) return double.MaxValue;
        double w2 = w * w;
        double s2 = sum * sum;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }

    private static Rect LayoutRow(
        List<(TreemapTile tile, double area)> nodes,
        int start, int end, double sum, Rect rect,
        List<(TreemapTile, Rect)> output)
    {
        if (sum <= 0 || end <= start) return rect;

        double shortSide = Math.Min(rect.Width, rect.Height);
        double thickness = sum / shortSide; // area / length

        if (rect.Width >= rect.Height)
        {
            // Short side is the height → stack items vertically in a column on the left.
            double offset = 0;
            for (int k = start; k < end; k++)
            {
                double h = nodes[k].area / thickness;
                output.Add((nodes[k].tile, new Rect(rect.X, rect.Y + offset, thickness, h)));
                offset += h;
            }
            return new Rect(rect.X + thickness, rect.Y,
                            Math.Max(0, rect.Width - thickness), rect.Height);
        }
        else
        {
            // Short side is the width → lay items horizontally in a band on top.
            double offset = 0;
            for (int k = start; k < end; k++)
            {
                double wi = nodes[k].area / thickness;
                output.Add((nodes[k].tile, new Rect(rect.X + offset, rect.Y, wi, thickness)));
                offset += wi;
            }
            return new Rect(rect.X, rect.Y + thickness,
                            rect.Width, Math.Max(0, rect.Height - thickness));
        }
    }

    // --- Rendering ----------------------------------------------------------------------
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        EnsureLayout();

        // Background.
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(13, 17, 23)),
            new Rect(Bounds.Size));

        if (_layout.Count == 0)
            return;

        var typeface = new Typeface(FontFamily.Default);

        foreach (var (tile, rect) in _layout)
        {
            if (rect.Width <= 0 || rect.Height <= 0) continue;

            var r = rect.Deflate(0.5);
            context.FillRectangle(tile.Fill, r);
            context.DrawRectangle(null, GapPen, r);

            bool selected = tile.IsSelected?.Invoke() ?? false;
            if (selected)
                context.DrawRectangle(null, SelectedPen, r.Deflate(1));

            // Label only when the tile is big enough to read.
            if (rect.Width > 54 && rect.Height > 16 && !string.IsNullOrEmpty(tile.Label))
            {
                var ft = new FormattedText(
                    tile.Label,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    11,
                    Brushes.Black)
                {
                    MaxTextWidth = rect.Width - 6,
                    MaxTextHeight = rect.Height - 4,
                    TextAlignment = TextAlignment.Left,
                    Trimming = TextTrimming.CharacterEllipsis,
                    MaxLineCount = 2
                };
                context.DrawText(ft, new Point(rect.X + 4, rect.Y + 3));
            }
        }

        if (_hover != null)
        {
            var hit = _layout.FirstOrDefault(l => ReferenceEquals(l.tile, _hover));
            if (hit.tile != null)
                context.DrawRectangle(null, HoverPen, hit.rect.Deflate(1));
        }
    }

    // --- Interaction --------------------------------------------------------------------
    private TreemapTile? HitTest(Point p)
    {
        for (int i = _layout.Count - 1; i >= 0; i--)
            if (_layout[i].rect.Contains(p))
                return _layout[i].tile;
        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        EnsureLayout();
        var hit = HitTest(e.GetPosition(this));
        if (!ReferenceEquals(hit, _hover))
        {
            _hover = hit;
            TileHovered?.Invoke(this, hit);
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover != null)
        {
            _hover = null;
            TileHovered?.Invoke(this, null);
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        EnsureLayout();
        var hit = HitTest(e.GetPosition(this));
        if (hit != null)
            TileClicked?.Invoke(this, hit);
    }

    /// <summary>Call when backing selection state changes so highlights refresh.</summary>
    public void RefreshSelection() => InvalidateVisual();
}
