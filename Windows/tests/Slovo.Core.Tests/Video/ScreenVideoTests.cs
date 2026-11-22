using Slovo.Core.Video;

namespace Slovo.Core.Tests.Video;

/// <summary>The video's fixed canvas and the pictures drawn into it (MeetingScreenCapture.videoOutputSize on macOS).</summary>
public sealed class ScreenVideoTests
{
    [Theory]
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(2560, 1600, 1920, 1200)]
    [InlineData(2880, 1800, 1920, 1200)]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1366, 768, 1366, 768)]
    [InlineData(1280, 1024, 1280, 1024)]
    // Portrait displays keep their orientation; the longer edge is the height.
    [InlineData(1440, 2560, 1080, 1920)]
    // H.264 wants even dimensions.
    [InlineData(1365, 767, 1364, 766)]
    [InlineData(5120, 1440, 1920, 540)]
    [InlineData(1, 1, 2, 2)]
    public void TheCanvasIsTheDisplayScaledToAtMost1920WithEvenEdges(int width, int height, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), ScreenVideoGeometry.OutputSize(width, height));

    [Fact]
    public void ADisplayFillsTheCanvasAndAWindowKeepsItsOwnSize()
    {
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), ScreenVideoGeometry.Fit(3840, 2160, 1920, 1080, allowUpscale: true));
        // A smaller display after a swap is scaled up to fill the same canvas, letterboxed.
        Assert.Equal(new PixelRect(240, 0, 1440, 1080), ScreenVideoGeometry.Fit(1024, 768, 1920, 1080, allowUpscale: true));
        // An app's window is not blown up: centred at its own size, on even pixels.
        Assert.Equal(new PixelRect(560, 240, 800, 600), ScreenVideoGeometry.Fit(800, 600, 1920, 1080, allowUpscale: false));
        Assert.Equal(new PixelRect(560, 240, 800, 600), ScreenVideoGeometry.Fit(801, 601, 1920, 1080, allowUpscale: false));
        // A window larger than the canvas is scaled down to fit.
        Assert.Equal(new PixelRect(0, 60, 1920, 960), ScreenVideoGeometry.Fit(2400, 1200, 1920, 1080, allowUpscale: false));
        Assert.True(ScreenVideoGeometry.Fit(0, 600, 1920, 1080, allowUpscale: false).IsEmpty);
    }

    [Fact]
    public void ColoursBecomeBt709LimitedRange()
    {
        Assert.Equal(16, ScreenFrameConverter.Luma(0, 0, 0));
        Assert.Equal(235, ScreenFrameConverter.Luma(255, 255, 255));
        Assert.Equal(128, ScreenFrameConverter.ChromaU(255, 255, 255));
        Assert.Equal(128, ScreenFrameConverter.ChromaV(128, 128, 128));
        // Pure red, green and blue, as BT.709 puts them.
        Assert.Equal(63, ScreenFrameConverter.Luma(255, 0, 0));
        Assert.Equal(240, ScreenFrameConverter.ChromaV(255, 0, 0));
        Assert.Equal(172, ScreenFrameConverter.Luma(0, 255, 0));
        Assert.Equal(32, ScreenFrameConverter.Luma(0, 0, 255));
        Assert.Equal(240, ScreenFrameConverter.ChromaU(0, 0, 255));
    }

    [Fact]
    public void ABlankCanvasIsBlack()
    {
        var converter = new ScreenFrameConverter(8, 4);
        Assert.False(converter.HasPicture);
        Assert.All(converter.Frame[..32].ToArray(), luma => Assert.Equal(16, luma));
        Assert.All(converter.Frame[32..].ToArray(), chroma => Assert.Equal(128, chroma));
        Assert.Throws<ArgumentException>(() => new ScreenFrameConverter(7, 4));
    }

    [Fact]
    public void ScalingDownAveragesEveryPixelUnderneath()
    {
        // A 4×4 checkerboard of black and white pixels, halved: every output pixel is mid grey.
        var converter = new ScreenFrameConverter(2, 2);
        var pixels = new byte[4 * 4 * 4];
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var value = (byte)((x + y) % 2 == 0 ? 255 : 0);
                pixels.AsSpan((y * 4 + x) * 4, 3).Fill(value);
            }
        }
        converter.Draw(pixels, 4, 4, 16, allowUpscale: true);
        Assert.True(converter.HasPicture);
        var grey = ScreenFrameConverter.Luma(128, 128, 128);
        Assert.Equal(new[] { grey, grey, grey, grey }, converter.Frame[..4].ToArray());
        Assert.Equal(new byte[] { 128, 128 }, converter.Frame[4..].ToArray());
    }

    [Fact]
    public void RowsAreReadThroughTheirStrideAndTheirPaddingIgnored()
    {
        var converter = new ScreenFrameConverter(2, 2);
        // Two pixels per row of red, then eight bytes of padding that must not bleed in.
        var stride = 16;
        var pixels = new byte[stride * 2];
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 2; x++)
            {
                var at = y * stride + x * 4;
                pixels[at + 2] = 255;
                pixels[at + 3] = 255;
            }
            pixels.AsSpan(y * stride + 8, 8).Fill(0xAA);
        }
        converter.Draw(pixels, 2, 2, stride, allowUpscale: true);
        Assert.All(converter.Frame[..4].ToArray(), luma => Assert.Equal(ScreenFrameConverter.Luma(255, 0, 0), luma));
        Assert.Equal(ScreenFrameConverter.ChromaU(255, 0, 0), converter.Frame[4]);
        Assert.Equal(ScreenFrameConverter.ChromaV(255, 0, 0), converter.Frame[5]);
    }

    [Fact]
    public void AWindowSitsCentredOnBlackAndAMovedOneLeavesNothingBehind()
    {
        var converter = new ScreenFrameConverter(8, 8);
        converter.Draw(Solid(4, 4, 255), 4, 4, 16, allowUpscale: false);
        Assert.Equal(new PixelRect(2, 2, 4, 4), converter.PictureRect);
        var white = ScreenFrameConverter.Luma(255, 255, 255);
        Assert.Equal(16, Luma(converter, 0, 0));
        Assert.Equal(white, Luma(converter, 2, 2));
        Assert.Equal(white, Luma(converter, 5, 5));
        Assert.Equal(16, Luma(converter, 6, 6));

        // The window grows: the canvas is cleared before the new picture goes in.
        converter.Draw(Solid(8, 2, 0), 8, 2, 32, allowUpscale: false);
        Assert.Equal(new PixelRect(0, 2, 8, 2), converter.PictureRect);
        Assert.Equal(16, Luma(converter, 2, 4));
        Assert.Equal(16, Luma(converter, 2, 2));
    }

    [Fact]
    public void APictureThatDoesNotMatchItsSizeIsRefused()
    {
        var converter = new ScreenFrameConverter(4, 4);
        Assert.Throws<ArgumentException>(() => converter.Draw(new byte[10], 4, 4, 16, allowUpscale: true));
        Assert.Throws<ArgumentException>(() => converter.Draw(new byte[64], 4, 4, 8, allowUpscale: true));
    }

    static byte Luma(ScreenFrameConverter converter, int x, int y) => converter.Frame[y * converter.Width + x];

    static byte[] Solid(int width, int height, byte value)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels.AsSpan(i, 3).Fill(value);
            pixels[i + 3] = 255;
        }
        return pixels;
    }
}
