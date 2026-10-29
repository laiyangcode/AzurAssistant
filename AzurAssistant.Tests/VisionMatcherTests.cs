using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Tests;

/// <summary>Covers region bounds, template building and channel selection of the OpenCV matcher.</summary>
public class VisionMatcherTests
{
    private const int CentreX = TestFrames.MarkLeft + TestFrames.MarkWidth / 2;
    private const int CentreY = TestFrames.MarkTop + TestFrames.MarkHeight / 2;
    private static readonly PixelRegion Region = new(0, 0, 40, 30);

    private static ImageTemplate EmbeddedMarkTemplate(TemplateChannel channel = TemplateChannel.Gray) =>
        OpenCvTemplateMatcher.Create(TestFrames.MarkWidth, TestFrames.MarkHeight, TestFrames.EmbeddedMarkPixels(), channel);

    private static ImageTemplate OpponentMarkTemplate() =>
        OpenCvTemplateMatcher.Create(TestFrames.MarkWidth, TestFrames.MarkHeight, TestFrames.OpponentOnlyMarkPixels(),
            TemplateChannel.RedMinusBlue);

    [Theory]
    [InlineData(0, 0, 40, 30, 40, 30)]
    [InlineData(1900, 1050, 20, 30, 1920, 1080)]
    [InlineData(10, 10, 1, 1, 11, 11)]
    public void Validate_AcceptsRegionsTouchingTheEdge(int x, int y, int width, int height, int frameWidth, int frameHeight) =>
        new PixelRegion(x, y, width, height).Validate(frameWidth, frameHeight);

    [Theory]
    [InlineData(-1, 0, 10, 10)]
    [InlineData(0, -1, 10, 10)]
    [InlineData(0, 0, 0, 10)]
    [InlineData(0, 0, 10, 0)]
    [InlineData(0, 0, 41, 10)]
    [InlineData(0, 0, 40, 31)]
    [InlineData(31, 0, 10, 10)]
    [InlineData(0, 21, 10, 10)]
    public void Validate_RejectsOutOfBoundsRegions(int x, int y, int width, int height)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new PixelRegion(x, y, width, height).Validate(40, 30));
        Assert.Contains("识别区域超出画面。", error.Message);
        Assert.Equal("PixelRegion", error.ParamName);
    }

    [Fact]
    public void Validate_UsesLongArithmeticSoHugeOffsetsCannotOverflow() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelRegion(int.MaxValue, 0, 2, 1).Validate(40, 30));

    [Fact]
    public void Create_RejectsPixelBufferOfTheWrongLength()
    {
        var error = Assert.Throws<ArgumentException>(() => OpenCvTemplateMatcher.Create(4, 4, new byte[3 * 4 * 4]));
        Assert.Contains("模板像素尺寸无效", error.Message);
    }

    [Fact]
    public void Create_RejectsTemplateWithoutLuminanceFeatures()
    {
        var flat = new byte[8 * 8 * 4];
        for (var i = 0; i < flat.Length; i++) flat[i] = 120;

        var error = Assert.Throws<ArgumentException>(() => OpenCvTemplateMatcher.Create(8, 8, flat));
        Assert.Contains("模板缺少可匹配的图像特征", error.Message);
    }

    [Fact]
    public void Create_KeepsTheChannelItWasBuiltFor() => Assert.Equal(TemplateChannel.Gray, EmbeddedMarkTemplate().Channel);

    [Fact]
    public void Create_RejectsWarmCoolPatternInTheOpponentChannelBecauseItIsFlatThere()
    {
        var pixels = TestFrames.Create(8, 8, TestFrames.WarmCoolConstantDifference).PixelsOf();

        Assert.Equal(TemplateChannel.Gray, OpenCvTemplateMatcher.Create(8, 8, pixels).Channel);
        var error = Assert.Throws<ArgumentException>(() =>
            OpenCvTemplateMatcher.Create(8, 8, pixels, TemplateChannel.RedMinusBlue));
        Assert.Contains("模板缺少可匹配的图像特征", error.Message);
    }

    [Fact]
    public void Create_RejectsOpponentPatternInTheGrayChannelBecauseItIsFlatThere()
    {
        Assert.Equal(TemplateChannel.RedMinusBlue, OpponentMarkTemplate().Channel);
        var error = Assert.Throws<ArgumentException>(() =>
            OpenCvTemplateMatcher.Create(TestFrames.MarkWidth, TestFrames.MarkHeight, TestFrames.OpponentOnlyMarkPixels()));
        Assert.Contains("模板缺少可匹配的图像特征", error.Message);
    }

    [Fact]
    public void Create_RejectsUnknownChannel()
    {
        var pixels = TestFrames.Create(8, 8, TestFrames.WarmCoolConstantDifference).PixelsOf();

        Assert.Throws<ArgumentOutOfRangeException>(() => OpenCvTemplateMatcher.Create(8, 8, pixels, (TemplateChannel)99));
    }

    [Fact]
    public void Match_RejectsTemplateLargerThanTheSearchRegion()
    {
        var big = OpenCvTemplateMatcher.Create(48, 48, TestFrames.Create(48, 48, TestFrames.EmbeddedMark).PixelsOf());

        var error = Assert.Throws<ArgumentException>(() => OpenCvTemplateMatcher.Match(TestFrames.EmbeddedMarkFrame(), big, Region));
        Assert.Contains("模板超出搜索区域", error.Message);
    }

    [Fact]
    public void Match_RejectsRegionOutsideTheFrame() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OpenCvTemplateMatcher.Match(TestFrames.EmbeddedMarkFrame(), EmbeddedMarkTemplate(), new PixelRegion(30, 0, 40, 30)));

    [Fact]
    public void Match_FlatRegionScoresZeroAndReportsNoLocation()
    {
        var result = OpenCvTemplateMatcher.Match(TestFrames.Uniform(48, 40), EmbeddedMarkTemplate(), Region);

        Assert.Equal(0, result.Score);
        Assert.Equal(default, result.Center);
    }

    [Fact]
    public void Match_ReturnsTheTemplateCentre()
    {
        var result = OpenCvTemplateMatcher.Match(TestFrames.EmbeddedMarkFrame(), EmbeddedMarkTemplate(), Region);

        Assert.True(result.Score > 0.99, $"完整嵌入的模板应几乎完全匹配：{result.Score}");
        Assert.Equal(new ClientPoint(CentreX, CentreY), result.Center);
    }

    [Fact]
    public void Match_ReportsCentresInFullFrameCoordinatesWhenTheRegionIsOffset()
    {
        var result = OpenCvTemplateMatcher.Match(TestFrames.EmbeddedMarkFrame(), EmbeddedMarkTemplate(), new PixelRegion(10, 5, 30, 25));

        Assert.True(result.Score > 0.99, $"偏移搜索区仍应命中同一模板：{result.Score}");
        Assert.Equal(new ClientPoint(CentreX, CentreY), result.Center);
    }

    [Fact]
    public void Match_OpponentChannelIsBlindToConstantWarmCoolDifference()
    {
        var frame = TestFrames.Create(40, 30, TestFrames.WarmCoolConstantDifference);
        var warmCoolTemplate = OpenCvTemplateMatcher.Create(12, 8,
            TestFrames.Create(12, 8, TestFrames.WarmCoolConstantDifference).PixelsOf());

        var gray = OpenCvTemplateMatcher.Match(frame, warmCoolTemplate, new PixelRegion(0, 0, 20, 15));
        var opponent = OpenCvTemplateMatcher.Match(frame, OpponentMarkTemplate(), Region);

        Assert.True(gray.Score > 0.99, $"亮度通道应识别暖冷棋盘：{gray.Score}");
        Assert.Equal(0, opponent.Score);
    }

    [Fact]
    public void Match_OpponentChannelFindsMarkInvisibleAsLuminance()
    {
        var result = OpenCvTemplateMatcher.Match(TestFrames.Create(64, 48, TestFrames.OpponentOnlyMark),
            OpponentMarkTemplate(), Region);

        Assert.True(result.Score > 0.99, $"对手色通道应识别等亮度暖冷标记：{result.Score}");
        Assert.Equal(new ClientPoint(CentreX, CentreY), result.Center);
    }
}
