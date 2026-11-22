namespace Slovo.Core.Video;

/// <summary>
/// Turns captured BGRA pictures into the NV12 frames the H.264 encoder takes, scaled into the
/// video's fixed canvas (<see cref="ScreenVideoGeometry.Fit"/>) with black around them. Colours
/// are BT.709 with limited range, which the MP4 file declares (<c>colr</c>).
/// </summary>
/// <remarks>
/// Scaling down averages every source pixel under an output pixel (a box filter), which keeps
/// small text legible where skipping pixels would break its strokes; scaling up repeats the
/// nearest pixel. A frame is converted in two passes over the source: rows are summed into
/// column totals, then the columns under each output pixel are summed. Not thread-safe.
/// </remarks>
public sealed class ScreenFrameConverter
{
    const byte BlackLuma = 16;
    const byte NeutralChroma = 128;

    readonly byte[] frame;
    int[] columnStart = [];
    int[] columnEnd = [];
    int[] rowTotals = [];
    int[] lineR = [];
    int[] lineG = [];
    int[] lineB = [];

    public ScreenFrameConverter(int canvasWidth, int canvasHeight)
    {
        if (canvasWidth < 2 || canvasHeight < 2 || (canvasWidth & 1) != 0 || (canvasHeight & 1) != 0)
        {
            throw new ArgumentException("The canvas needs even dimensions of at least 2 pixels");
        }
        Width = canvasWidth;
        Height = canvasHeight;
        frame = new byte[canvasWidth * canvasHeight * 3 / 2];
        FillBlack();
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The current NV12 frame: <see cref="Width"/> × <see cref="Height"/> luma bytes, then half as many chroma bytes (U, V pairs).</summary>
    public ReadOnlySpan<byte> Frame => frame;

    /// <summary>Where the last picture went on the canvas; empty before the first.</summary>
    public PixelRect PictureRect { get; private set; }

    /// <summary>Whether any picture has been drawn yet.</summary>
    public bool HasPicture { get; private set; }

    /// <summary>
    /// Draws a BGRA picture (rows <paramref name="stride"/> bytes apart, alpha ignored) into the
    /// canvas. When its place on the canvas changes, the old picture is cleared first.
    /// </summary>
    public void Draw(ReadOnlySpan<byte> bgra, int width, int height, int stride, bool allowUpscale)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < (long)stride * (height - 1) + width * 4)
        {
            throw new ArgumentException("The picture does not match its size");
        }
        var rect = ScreenVideoGeometry.Fit(width, height, Width, Height, allowUpscale);
        if (rect.IsEmpty)
        {
            return;
        }
        if (rect != PictureRect)
        {
            FillBlack();
            PictureRect = rect;
        }
        PrepareColumns(width, rect.Width);
        if (rowTotals.Length < width * 3)
        {
            rowTotals = new int[width * 3];
        }
        if (lineR.Length < rect.Width * 2)
        {
            lineR = new int[rect.Width * 2];
            lineG = new int[rect.Width * 2];
            lineB = new int[rect.Width * 2];
        }

        var lumaPlane = frame.AsSpan(0, Width * Height);
        var chromaPlane = frame.AsSpan(Width * Height);
        for (var outY = 0; outY < rect.Height; outY += 2)
        {
            // Two output rows at a time: the chroma below them is shared.
            for (var half = 0; half < 2; half++)
            {
                var row = outY + half;
                var sourceStart = (int)((long)row * height / rect.Height);
                var sourceEnd = Math.Max(sourceStart + 1, (int)((long)(row + 1) * height / rect.Height));
                sourceEnd = Math.Min(sourceEnd, height);
                SumRows(bgra, stride, width, sourceStart, sourceEnd);
                var rows = sourceEnd - sourceStart;
                var offset = half * rect.Width;
                var lumaRow = lumaPlane.Slice((rect.Y + row) * Width + rect.X, rect.Width);
                for (var outX = 0; outX < rect.Width; outX++)
                {
                    int b = 0, g = 0, r = 0;
                    var start = columnStart[outX];
                    var end = columnEnd[outX];
                    for (var x = start; x < end; x++)
                    {
                        b += rowTotals[x * 3];
                        g += rowTotals[x * 3 + 1];
                        r += rowTotals[x * 3 + 2];
                    }
                    var count = rows * (end - start);
                    b = (b + count / 2) / count;
                    g = (g + count / 2) / count;
                    r = (r + count / 2) / count;
                    lineR[offset + outX] = r;
                    lineG[offset + outX] = g;
                    lineB[offset + outX] = b;
                    lumaRow[outX] = Luma(r, g, b);
                }
            }
            var chromaRow = chromaPlane.Slice((rect.Y + outY) / 2 * Width + rect.X, rect.Width);
            for (var outX = 0; outX < rect.Width; outX += 2)
            {
                var second = rect.Width + outX;
                var r = (lineR[outX] + lineR[outX + 1] + lineR[second] + lineR[second + 1] + 2) / 4;
                var g = (lineG[outX] + lineG[outX + 1] + lineG[second] + lineG[second + 1] + 2) / 4;
                var b = (lineB[outX] + lineB[outX + 1] + lineB[second] + lineB[second + 1] + 2) / 4;
                chromaRow[outX] = ChromaU(r, g, b);
                chromaRow[outX + 1] = ChromaV(r, g, b);
            }
        }
        HasPicture = true;
    }

    /// <summary>BT.709 limited-range luma of 8-bit RGB (the integer form of the Windows "Recommended 8-bit YUV formats").</summary>
    public static byte Luma(int r, int g, int b) => (byte)(((47 * r + 157 * g + 16 * b + 128) >> 8) + 16);

    /// <summary>BT.709 blue-difference chroma; the green weight is rounded to −86 so the weights sum to 0 and grey stays neutral.</summary>
    public static byte ChromaU(int r, int g, int b) => (byte)(((-26 * r - 86 * g + 112 * b + 128) >> 8) + 128);

    public static byte ChromaV(int r, int g, int b) => (byte)(((112 * r - 102 * g - 10 * b + 128) >> 8) + 128);

    void SumRows(ReadOnlySpan<byte> bgra, int stride, int width, int start, int end)
    {
        var totals = rowTotals.AsSpan(0, width * 3);
        totals.Clear();
        for (var y = start; y < end; y++)
        {
            var line = bgra.Slice(y * stride, width * 4);
            for (int x = 0, t = 0; x < line.Length; x += 4, t += 3)
            {
                totals[t] += line[x];
                totals[t + 1] += line[x + 1];
                totals[t + 2] += line[x + 2];
            }
        }
    }

    void PrepareColumns(int sourceWidth, int outputWidth)
    {
        if (columnStart.Length != outputWidth)
        {
            columnStart = new int[outputWidth];
            columnEnd = new int[outputWidth];
        }
        for (var x = 0; x < outputWidth; x++)
        {
            var start = (int)((long)x * sourceWidth / outputWidth);
            var end = Math.Max(start + 1, (int)((long)(x + 1) * sourceWidth / outputWidth));
            columnStart[x] = start;
            columnEnd[x] = Math.Min(end, sourceWidth);
        }
    }

    void FillBlack()
    {
        frame.AsSpan(0, Width * Height).Fill(BlackLuma);
        frame.AsSpan(Width * Height).Fill(NeutralChroma);
    }
}
