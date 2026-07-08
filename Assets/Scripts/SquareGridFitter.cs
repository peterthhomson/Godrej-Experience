using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Keeps every cell of a GridLayoutGroup perfectly square at any screen size.
/// Computes the largest square that lets all rows and columns fit the container,
/// so buttons never stretch when the window or device aspect changes.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(GridLayoutGroup))]
public sealed class SquareGridFitter : MonoBehaviour
{
    private GridLayoutGroup grid;
    private RectTransform rectTransform;

    private void OnEnable()
    {
        Cache();
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
        float size = Mathf.Floor(Mathf.Max(10f, Mathf.Min(maxCellWidth, maxCellHeight)));

        if (!Mathf.Approximately(grid.cellSize.x, size) || !Mathf.Approximately(grid.cellSize.y, size))
        {
            grid.cellSize = new Vector2(size, size);
        }
    }
}
