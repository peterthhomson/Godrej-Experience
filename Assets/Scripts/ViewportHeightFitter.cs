using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Layout element that derives its height from its own width (default 16:9), for
/// panels inside a VerticalLayoutGroup. The parent layout stretches this element
/// edge-to-edge horizontally; this component then reports the exact matching height,
/// so the child content can simply stretch-fill with no AspectRatioFitter fighting
/// the layout group.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(RectTransform))]
public sealed class ViewportHeightFitter : MonoBehaviour, ILayoutElement
{
    [Tooltip("Height as a fraction of width. 0.5625 = 16:9, 0.75 = 4:3.")]
    [Range(0.1f, 2f)]
    public float heightPerWidth = 9f / 16f;

    private float computedHeight;

    public void CalculateLayoutInputHorizontal() { }

    public void CalculateLayoutInputVertical()
    {
        // The vertical pass runs after widths are assigned, so rect.width is valid here.
        computedHeight = ((RectTransform)transform).rect.width * heightPerWidth;
    }

    public float minWidth => -1f;
    public float preferredWidth => -1f;
    public float flexibleWidth => -1f;
    public float minHeight => computedHeight;
    public float preferredHeight => computedHeight;
    public float flexibleHeight => 0f;
    public int layoutPriority => 2; // beat the Image component's own layout inputs

    private void OnValidate()
    {
        if (transform is RectTransform rect) LayoutRebuilder.MarkLayoutForRebuild(rect);
    }
}
