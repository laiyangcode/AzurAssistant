using AzurAssistant.Vision;
using OpenCvSharp;

namespace AzurAssistant.Tests;

/// <summary>Covers the arrow silhouette matcher: shape fitting, its rejection rules and the north-clockwise conversion.</summary>
public class ArrowOrientationTests
{
    [Theory]
    [InlineData(-90, 0)]
    [InlineData(0, 90)]
    [InlineData(90, 180)]
    [InlineData(180, 270)]
    [InlineData(-180, 270)]
    [InlineData(-45, 45)]
    public void NorthClockwise_ConvertsImageAxesToCompassBearing(double imageDegrees, double expected) =>
        Assert.Equal(expected, ArrowOrientation.NorthClockwise(imageDegrees), 3);

    [Fact]
    public void Read_ReturnsNullWhenNothingIsDrawn()
    {
        using var empty = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));

        Assert.Null(ArrowOrientation.Read(empty));
    }

    [Fact]
    public void Read_IgnoresBlobsTooSmallToBeAnArrow()
    {
        using var mat = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mat, new Rect(28, 28, 3, 3), Scalar.All(255), -1);

        Assert.Null(ArrowOrientation.Read(mat));
    }

    [Theory]
    [InlineData(0, 26, 12, 12)]
    [InlineData(26, 0, 12, 12)]
    [InlineData(52, 26, 12, 12)]
    [InlineData(26, 52, 12, 12)]
    public void Read_IgnoresSilhouettesTouchingTheImageBorder(int x, int y, int width, int height)
    {
        using var mat = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mat, new Rect(x, y, width, height), Scalar.All(255), -1);

        Assert.Null(ArrowOrientation.Read(mat));
    }

    [Fact]
    public void Read_IgnoresRoundSilhouettesBecauseTheyHaveNoHead()
    {
        using var mat = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(mat, new Point(32, 32), 12, Scalar.All(255), -1);

        Assert.Null(ArrowOrientation.Read(mat));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(45)]
    [InlineData(315)]
    public void Read_ReportsTheDrawnArrowDirection(int generatorAngle)
    {
        using var arrow = Arrow(generatorAngle);

        var read = ArrowOrientation.Read(arrow);

        Assert.NotNull(read);
        // The template bank samples every 2 degrees, so a re-read may land on a neighbouring sample.
        Assert.True(Math.Abs(read!.Value - (generatorAngle - 90)) <= 4,
            $"绘制角度 {generatorAngle} 应读回 {generatorAngle - 90}±4，实际 {read.Value}");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(180, 180)]
    [InlineData(270, 270)]
    public void Read_CardinalArrowsMapToCardinalBearings(int generatorAngle, int expectedBearing)
    {
        using var arrow = Arrow(generatorAngle);

        var read = ArrowOrientation.Read(arrow);

        Assert.NotNull(read);
        var bearing = ArrowOrientation.NorthClockwise(read!.Value);
        var difference = Math.Min(Math.Abs(bearing - expectedBearing), 360 - Math.Abs(bearing - expectedBearing));
        Assert.True(difference <= 4, $"{generatorAngle}°绘制的箭头方位角应为 {expectedBearing}±4，实际 {bearing}");
    }

    [Fact]
    public void Read_RobustlyFindsNorthEvenWithATailNotch()
    {
        using var notched = Arrow(0, ratio: 0.95, notch: 0.30);

        var read = ArrowOrientation.Read(notched);

        Assert.NotNull(read);
        var bearing = ArrowOrientation.NorthClockwise(read!.Value);
        Assert.True(bearing <= 8 || bearing >= 352, $"带尾凹口的箭头朝向应接近正北：{bearing}");
    }

    /// <summary>Rasterizes the same parametric arrow the built-in template bank uses, on a larger canvas.</summary>
    private static Mat Arrow(double angle, double ratio = 0.75, double notch = 0, int canvas = 64)
    {
        Point2f[] polygon = [new(0, -.65f), new((float)-ratio / 2, .35f), new(0, (float)(.35 - notch)), new((float)ratio / 2, .35f)];
        var moments = Cv2.Moments(polygon);
        var scale = Math.Sqrt(220 / moments.M00);
        var radians = angle * Math.PI / 180;
        var centre = (canvas - 1) / 2d;
        var points = polygon.Select(p =>
        {
            var x = (p.X - moments.M10 / moments.M00) * scale;
            var y = (p.Y - moments.M01 / moments.M00) * scale;
            return new Point((int)Math.Round((x * Math.Cos(radians) - y * Math.Sin(radians) + centre) * 16),
                (int)Math.Round((x * Math.Sin(radians) + y * Math.Cos(radians) + centre) * 16));
        }).ToArray();
        var mask = new Mat(canvas, canvas, MatType.CV_8UC1, Scalar.All(0));
        Cv2.FillPoly(mask, [points], Scalar.All(255), shift: 4);
        return mask;
    }
}
