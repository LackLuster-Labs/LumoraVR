using Lumora.Core.Math;
using System.Linq;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Grid layout constraint mode.
/// </summary>
public enum GridConstraint
{
    /// <summary>Flexible grid that wraps based on available space.</summary>
    Flexible,
    /// <summary>Fixed number of columns.</summary>
    FixedColumnCount,
    /// <summary>Fixed number of rows.</summary>
    FixedRowCount
}

/// <summary>
/// Corner to start grid layout from.
/// </summary>
public enum GridCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>
/// Primary axis for grid fill direction.
/// </summary>
public enum GridAxis
{
    Horizontal,
    Vertical
}

/// <summary>
/// Helio grid layout component.
/// Arranges children in a grid pattern.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioGridLayout : HelioLayoutGroup
{
    /// <summary>
    /// Size of each cell in the grid.
    /// </summary>
    public Sync<float2> CellSize { get; private set; }

    /// <summary>
    /// Spacing between cells.
    /// </summary>
    public new Sync<float2> Spacing { get; private set; }

    /// <summary>
    /// Constraint mode for the grid.
    /// </summary>
    public Sync<GridConstraint> Constraint { get; private set; }

    /// <summary>
    /// Number of rows/columns when using fixed constraint.
    /// </summary>
    public Sync<int> ConstraintCount { get; private set; }

    /// <summary>
    /// Corner to start laying out from.
    /// </summary>
    public Sync<GridCorner> StartCorner { get; private set; }

    /// <summary>
    /// Primary axis for filling the grid.
    /// </summary>
    public Sync<GridAxis> StartAxis { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();

        CellSize = new Sync<float2>(this, new float2(100f, 100f));
        Spacing = new Sync<float2>(this, new float2(8f, 8f));
        Constraint = new Sync<GridConstraint>(this, GridConstraint.Flexible);
        ConstraintCount = new Sync<int>(this, 2);
        StartCorner = new Sync<GridCorner>(this, GridCorner.TopLeft);
        StartAxis = new Sync<GridAxis>(this, GridAxis.Horizontal);

        CellSize.OnChanged += _ => MarkDirty();
        Spacing.OnChanged += _ => MarkDirty();
        Constraint.OnChanged += _ => MarkDirty();
        ConstraintCount.OnChanged += _ => MarkDirty();
    }

    protected override void ApplyLayout(HelioRectTransform parentRect)
    {
        var children = GatherChildren();
        if (children.Count == 0) return;

        var rect = parentRect.Rect;
        float2 cellSize = CellSize.Value;
        float2 spacing = Spacing.Value;
        float4 padding = Padding.Value;

        float availableWidth = rect.Size.x - padding.x - padding.z;
        float availableHeight = rect.Size.y - padding.y - padding.w;

        // Calculate grid dimensions
        int columns, rows;
        CalculateGridDimensions(children.Count, availableWidth, availableHeight, cellSize, spacing, out columns, out rows);

        // Determine start position and direction
        float2 startPos = GetStartPosition(rect, padding, columns, rows, cellSize, spacing);
        float2 cellStep = GetCellStep(cellSize, spacing);

        // Layout each child
        for (int i = 0; i < children.Count; i++)
        {
            int col, row;
            GetCellPosition(i, columns, rows, out col, out row);

            float2 pos = startPos + new float2(col * cellStep.x, row * cellStep.y);

            var (childRect, _) = children[i];
            childRect.SetLayoutRect(new HelioRect(pos, cellSize), rewriteOffsets: false);
        }
    }

    private void CalculateGridDimensions(int itemCount, float availableWidth, float availableHeight,
        float2 cellSize, float2 spacing, out int columns, out int rows)
    {
        switch (Constraint.Value)
        {
            case GridConstraint.FixedColumnCount:
                columns = System.Math.Max(1, ConstraintCount.Value);
                rows = (itemCount + columns - 1) / columns;
                break;

            case GridConstraint.FixedRowCount:
                rows = System.Math.Max(1, ConstraintCount.Value);
                columns = (itemCount + rows - 1) / rows;
                break;

            default: // Flexible
                if (StartAxis.Value == GridAxis.Horizontal)
                {
                    columns = System.Math.Max(1, (int)((availableWidth + spacing.x) / (cellSize.x + spacing.x)));
                    rows = (itemCount + columns - 1) / columns;
                }
                else
                {
                    rows = System.Math.Max(1, (int)((availableHeight + spacing.y) / (cellSize.y + spacing.y)));
                    columns = (itemCount + rows - 1) / rows;
                }
                break;
        }
    }

    private float2 GetStartPosition(HelioRect rect, float4 padding, int columns, int rows, float2 cellSize, float2 spacing)
    {
        float2 pos = rect.Min;

        switch (StartCorner.Value)
        {
            case GridCorner.TopLeft:
                pos.x += padding.x;
                pos.y += padding.y;
                break;
            case GridCorner.TopRight:
                pos.x = rect.Max.x - padding.z - cellSize.x;
                pos.y += padding.y;
                break;
            case GridCorner.BottomLeft:
                pos.x += padding.x;
                pos.y = rect.Max.y - padding.w - cellSize.y;
                break;
            case GridCorner.BottomRight:
                pos.x = rect.Max.x - padding.z - cellSize.x;
                pos.y = rect.Max.y - padding.w - cellSize.y;
                break;
        }

        return pos;
    }

    private float2 GetCellStep(float2 cellSize, float2 spacing)
    {
        float2 step = cellSize + spacing;

        // Reverse direction based on corner
        if (StartCorner.Value == GridCorner.TopRight || StartCorner.Value == GridCorner.BottomRight)
            step.x = -step.x;
        if (StartCorner.Value == GridCorner.BottomLeft || StartCorner.Value == GridCorner.BottomRight)
            step.y = -step.y;

        return step;
    }

    private void GetCellPosition(int index, int columns, int rows, out int col, out int row)
    {
        if (StartAxis.Value == GridAxis.Horizontal)
        {
            col = index % columns;
            row = index / columns;
        }
        else
        {
            row = index % rows;
            col = index / rows;
        }
    }
}
