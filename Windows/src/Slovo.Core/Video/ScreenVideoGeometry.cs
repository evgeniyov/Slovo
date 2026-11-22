namespace Slovo.Core.Video;

/// <summary>A rectangle of whole pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// The sizes a meeting's screen video is planned with (the sizing half of the macOS
/// <c>MeetingScreenCapture.videoOutputSize</c>): one fixed canvas for the whole file, taken from
/// the main display, and where each captured picture goes in it.
/// </summary>
public static class ScreenVideoGeometry
{
    /// <summary>
    /// The longer edge of the video at most: screen video is reference material, not a smooth
    /// recording, so this stays modest to bound the file over a multi-hour meeting.
    /// </summary>
    public const int MaximumEdge = 1920;

    /// <summary>
    /// The canvas for a display of <paramref name="nativeWidth"/> × <paramref name="nativeHeight"/>
    /// pixels: the same size scaled down to <see cref="MaximumEdge"/> on the longer side, with
    /// both edges even, as H.264 wants them. The canvas is fixed for the file: an encoder's
    /// frame size cannot change mid-file, so a display swap or a window resize is scaled into it.
    /// </summary>
    public static (int Width, int Height) OutputSize(int nativeWidth, int nativeHeight)
    {
        nativeWidth = Math.Max(2, nativeWidth);
        nativeHeight = Math.Max(2, nativeHeight);
        var longerEdge = Math.Max(nativeWidth, nativeHeight);
        var factor = longerEdge > MaximumEdge ? (double)MaximumEdge / longerEdge : 1;
        var width = Math.Max(2, (int)(nativeWidth * factor)) & ~1;
        var height = Math.Max(2, (int)(nativeHeight * factor)) & ~1;
        return (width, height);
    }

    /// <summary>
    /// Where a picture of <paramref name="contentWidth"/> × <paramref name="contentHeight"/> goes
    /// on the canvas: as large as fits with its proportions kept, centred, on even pixels so
    /// the half-size chroma lines up. With <paramref name="allowUpscale"/> off a smaller picture
    /// (an app's window) keeps its own size instead of being blown up.
    /// </summary>
    public static PixelRect Fit(int contentWidth, int contentHeight, int canvasWidth, int canvasHeight, bool allowUpscale)
    {
        if (contentWidth <= 0 || contentHeight <= 0 || canvasWidth < 2 || canvasHeight < 2)
        {
            return default;
        }
        var scale = Math.Min((double)canvasWidth / contentWidth, (double)canvasHeight / contentHeight);
        if (!allowUpscale)
        {
            scale = Math.Min(scale, 1);
        }
        var width = Math.Clamp((int)Math.Round(contentWidth * scale), 2, canvasWidth) & ~1;
        var height = Math.Clamp((int)Math.Round(contentHeight * scale), 2, canvasHeight) & ~1;
        var x = ((canvasWidth - width) / 2) & ~1;
        var y = ((canvasHeight - height) / 2) & ~1;
        return new PixelRect(x, y, width, height);
    }
}
