using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AzurAssistant.Contracts;
using OpenCvSharp;

namespace AzurAssistant.Tests;

/// <summary>Builds synthetic BGRA frames so the vision layer can be tested without a running game.</summary>
internal static class TestFrames
{
    public const uint Blue = 0, Green = 1, Red = 2;

    /// <summary>The clock a real capture uses: system stopwatch time, which the pause boundary is measured on too.</summary>
    public static TimeSpan CaptureClock() => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);

    public static FrameSnapshot Create(int width, int height, Func<int, int, uint, uint> channel,
        TimeSpan? timestamp = null, long id = 1, long viewportVersion = 1, nint windowHandle = 0x1234, int processId = 4242)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            pixels[offset + 0] = (byte)channel(x, y, Blue);
            pixels[offset + 1] = (byte)channel(x, y, Green);
            pixels[offset + 2] = (byte)channel(x, y, Red);
            pixels[offset + 3] = 255;
        }
        return new FrameSnapshot(id, timestamp ?? TimeSpan.FromSeconds(1), DateTimeOffset.UnixEpoch.Add(timestamp ?? TimeSpan.FromSeconds(1)),
            width, height, viewportVersion, windowHandle, pixels, 0, 0, processId);
    }

    public static FrameSnapshot Uniform(int width, int height, byte b = 40, byte g = 90, byte r = 140,
        TimeSpan? timestamp = null) => Create(width, height, (_, _, c) => c switch
        {
            Blue => b,
            Green => g,
            _ => r
        }, timestamp);

    public const int MarkLeft = 20, MarkTop = 15, MarkWidth = 12, MarkHeight = 8;

    /// <summary>A unique luminance mark inside a 64×48 field of mid gray, so template position is unambiguous.</summary>
    public static uint EmbeddedMark(int x, int y, uint channel)
    {
        var inside = x >= MarkLeft && x < MarkLeft + MarkWidth && y >= MarkTop && y < MarkTop + MarkHeight;
        var v = inside ? (((x - MarkLeft) + 3 * (y - MarkTop)) % 4 < 2 ? 235u : 25u) : 120u;
        return channel switch
        {
            Blue => v,
            Green => v / 2,
            _ => 255 - v
        };
    }

    public static FrameSnapshot EmbeddedMarkFrame() => Create(64, 48, EmbeddedMark);

    public static byte[] EmbeddedMarkPixels() =>
        Create(MarkWidth, MarkHeight, (x, y, c) => EmbeddedMark(x + MarkLeft, y + MarkTop, c)).PixelsOf();

    /// <summary>Warm/cool blocks with identical luminance and identical red-minus-blue difference: gray varies, opponent channel is flat.</summary>
    public static uint WarmCoolConstantDifference(int x, int y, uint channel)
    {
        var low = (x / 2 + y / 2) % 2 == 0;
        return channel switch
        {
            Blue => low ? (byte)0 : (byte)100,
            Green => 0,
            _ => low ? (byte)50 : (byte)150
        };
    }

    /// <summary>The mirror image of <see cref="WarmCoolConstantDifference"/>: flat gray, strong opponent contrast on a neutral field.</summary>
    public static uint OpponentOnlyMark(int x, int y, uint channel)
    {
        var inside = x >= MarkLeft && x < MarkLeft + MarkWidth && y >= MarkTop && y < MarkTop + MarkHeight;
        if (!inside) return 100;
        var warm = ((x - MarkLeft) / 2 + (y - MarkTop) / 2) % 2 == 0;
        return channel switch
        {
            Blue => warm ? (byte)0 : (byte)255,
            Green => warm ? (byte)88 : (byte)168,
            _ => warm ? (byte)255 : (byte)0
        };
    }

    public static byte[] OpponentOnlyMarkPixels() =>
        Create(MarkWidth, MarkHeight, (x, y, c) => OpponentOnlyMark(x + MarkLeft, y + MarkTop, c)).PixelsOf();

    public static byte[] PixelsOf(this FrameSnapshot frame) => frame.Pixels.ToArray();

    public static string WritePng(string directory, string fileName, int width, int height, Func<int, int, uint, uint> channel)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        using var mat = new Mat(height, width, MatType.CV_8UC4, Scalar.All(0));
        var buffer = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            buffer[offset + 0] = (byte)channel(x, y, Blue);
            buffer[offset + 1] = (byte)channel(x, y, Green);
            buffer[offset + 2] = (byte)channel(x, y, Red);
            buffer[offset + 3] = 255;
        }
        Marshal.Copy(buffer, 0, mat.Data, buffer.Length);
        Cv2.ImWrite(path, mat);
        return path;
    }

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string CreateTempDirectory(string purpose)
    {
        var path = Path.Combine(Path.GetTempPath(), "azurassistant-tests", purpose, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void DeleteTempDirectory(string path)
    {
        try { Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
