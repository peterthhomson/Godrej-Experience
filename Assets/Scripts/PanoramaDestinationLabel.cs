using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// A visual-only panorama marker made from one finished image. The source image may
/// already contain its room text, icon and directional arrow.
/// </summary>
[DisallowMultipleComponent]
public sealed class PanoramaDestinationLabel : MonoBehaviour
{
    [SerializeField] private string destinationRoomName;
    [SerializeField] private Texture2D sourceImage;
    [FormerlySerializedAs("iconImage")]
    [SerializeField] private Sprite markerSprite;
    [FormerlySerializedAs("iconRenderer")]
    [SerializeField] private SpriteRenderer imageRenderer;
    [SerializeField, Min(0.05f)] private float imageHeight = 0.65f;

    public string DestinationRoomName => destinationRoomName;
    public Texture2D SourceImage => sourceImage != null
        ? sourceImage
        : markerSprite != null ? markerSprite.texture : null;
    public Sprite MarkerSprite => markerSprite;
    public float ImageHeight => imageHeight;
    public string DisplayName => SourceImage != null
        ? SourceImage.name
        : string.IsNullOrWhiteSpace(destinationRoomName) ? "Unassigned image" : destinationRoomName;

    public void Configure(
        string destination,
        Texture2D image,
        Sprite sprite,
        SpriteRenderer renderer,
        float height)
    {
        destinationRoomName = destination;
        sourceImage = image;
        markerSprite = sprite;
        imageRenderer = renderer;
        imageHeight = Mathf.Max(0.05f, height);
        RefreshVisual();
    }

    public void RefreshVisual()
    {
        if (imageRenderer == null) imageRenderer = GetComponentInChildren<SpriteRenderer>(true);
        if (imageRenderer == null) return;

        imageRenderer.sprite = markerSprite;
        imageRenderer.enabled = markerSprite != null;
        if (markerSprite == null) return;

        imageRenderer.transform.localPosition = new Vector3(0f, 0f, -0.02f);
        imageRenderer.transform.localRotation = Quaternion.identity;

        float spriteHeight = Mathf.Max(0.001f, markerSprite.bounds.size.y);
        float scale = imageHeight / spriteHeight;
        imageRenderer.transform.localScale = Vector3.one * scale;
        imageRenderer.sortingOrder = 1;
    }

    private void OnValidate()
    {
        imageHeight = Mathf.Max(0.05f, imageHeight);
        RefreshVisual();
    }
}
