using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Ensures every child of a GridLayoutGroup fits inside the container at any screen
/// size, scaling the cells while PRESERVING their authored proportions (captured from
/// the grid's cellSize when first enabled, or set explicitly via preferredCellSize).
/// With 14 room buttons in 4 columns this guarantees all 4 rows are visible instead of
/// the last row overflowing out of the panel.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(GridLayoutGroup))]
public sealed class SquareGridFitter : MonoBehaviour
{
    [Tooltip("Cell proportions to preserve while fitting. Zero = capture the GridLayoutGroup's current cellSize on enable.")]
    public Vector2 preferredCellSize = Vector2.zero;

    private GridLayoutGroup grid;
    private RectTransform rectTransform;

    private void OnEnable()
    {
        Cache();
        if (preferredCellSize.x <= 0f || preferredCellSize.y <= 0f)
        {
            if (grid != null) preferredCellSize = grid.cellSize;
        }
        Fit();
    }

    private void OnRectTransformDimensionsChange()
    {
        Fit();
    }

    private void Cache()
    {
        grid = GetComponent<GridLayoutGroup>();
        rectTransform = (RectTransform)transform;
    }

    private void Fit()
    {
        if (grid == null || rectTransform == null) Cache();
        if (grid == null || grid.constraint != GridLayoutGroup.Constraint.FixedColumnCount) return;
        if (preferredCellSize.x <= 0f || preferredCellSize.y <= 0f) return;

        int columns = Mathf.Max(1, grid.constraintCount);

        int count = 0;
        for (int i = 0; i < transform.childCount; i++)
        {
            if (transform.GetChild(i).gameObject.activeSelf) count++;
        }
        if (count == 0) return;

        int rows = Mathf.CeilToInt(count / (float)columns);
        Rect rect = rectTransform.rect;
        if (rect.width <= 0f || rect.height <= 0f) return;

        float maxCellWidth = (rect.width - grid.padding.horizontal - grid.spacing.x * (columns - 1)) / columns;
        float maxCellHeight = (rect.height - grid.padding.vertical - grid.spacing.y * (rows - 1)) / rows;
        if (maxCellWidth <= 0f || maxCellHeight <= 0f) return;

        // Largest uniform scale of the authored cell that fits both dimensions.
        float scale = Mathf.Min(maxCellWidth / preferredCellSize.x, maxCellHeight / preferredCellSize.y);
        var size = new Vector2(
            Mathf.Max(10f, Mathf.Floor(preferredCellSize.x * scale)),
            Mathf.Max(10f, Mathf.Floor(preferredCellSize.y * scale)));

        if (!Mathf.Approximately(grid.cellSize.x, size.x) || !Mathf.Approximately(grid.cellSize.y, size.y))
        {
            grid.cellSize = size;
        }
    }
}
