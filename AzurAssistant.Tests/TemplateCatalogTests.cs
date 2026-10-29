using System.IO;
using System.Text.Json;
using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Tests;

/// <summary>Covers manifest validation, the resolution lock and integrity of the shipped recognition bundles.</summary>
public class TemplateCatalogTests : IDisposable
{
    private const int ReferenceWidth = 64, ReferenceHeight = 48;
    private static readonly PixelRegion Search = new(0, 0, 40, 30);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly string _directory = TestFrames.CreateTempDirectory("template-catalog");

    public void Dispose() => TestFrames.DeleteTempDirectory(_directory);

    private static string TemplateName(string path) => Path.GetFileName(path);

    private string WriteMarkTemplate(string fileName = "mark.png") => TemplateName(TestFrames.WritePng(_directory, fileName,
        TestFrames.MarkWidth, TestFrames.MarkHeight, (x, y, c) => TestFrames.EmbeddedMark(x + TestFrames.MarkLeft, y + TestFrames.MarkTop, c)));

    private string Publish(TemplateDefinition definition, int schemaVersion = 1, string id = "mark",
        int referenceWidth = ReferenceWidth, int referenceHeight = ReferenceHeight)
    {
        var path = Path.Combine(_directory, "recognition.json");
        File.WriteAllText(path, JsonSerializer.Serialize(
            new TemplateManifest(schemaVersion, referenceWidth, referenceHeight, new Dictionary<string, TemplateDefinition> { [id] = definition }), Indented));
        return path;
    }

    private string PublishMark(int schemaVersion = 1, double threshold = 0.72, string? channel = null, PixelRegion? search = null)
    {
        var fileName = WriteMarkTemplate();
        Publish(new(fileName, search ?? Search, threshold, TestFrames.Sha256Of(Path.Combine(_directory, fileName)),
            Source: "单元测试合成素材", Channel: channel), schemaVersion);
        return fileName;
    }

    [Fact]
    public void Constructor_LoadsAValidBundle()
    {
        PublishMark();

        Assert.NotNull(new TemplateCatalog(_directory));
    }

    [Fact]
    public void Match_ReturnsTheTemplateCentreAndScore()
    {
        PublishMark();
        var catalog = new TemplateCatalog(_directory);

        var match = catalog.Match(TestFrames.EmbeddedMarkFrame(), "mark", out var score);

        Assert.NotNull(match);
        Assert.True(score > 0.99, score.ToString("F4"));
        Assert.Equal(new ClientPoint(TestFrames.MarkLeft + TestFrames.MarkWidth / 2,
            TestFrames.MarkTop + TestFrames.MarkHeight / 2), match!.Center);
    }

    [Fact]
    public void Match_HonoursThePerTemplateSearchRegionOverride()
    {
        PublishMark(search: new PixelRegion(40, 2, 24, 20));
        var catalog = new TemplateCatalog(_directory);

        // The manifest region no longer covers the mark, so the explicit region is the only one that can find it.
        Assert.Null(catalog.Match(TestFrames.EmbeddedMarkFrame(), "mark"));
        Assert.NotNull(catalog.Match(TestFrames.EmbeddedMarkFrame(), "mark", search: Search));
    }

    [Fact]
    public void Match_ReturnsNullButKeepsTheScoreBelowThreshold()
    {
        PublishMark(threshold: 0.99);
        var catalog = new TemplateCatalog(_directory);
        var distractor = TestFrames.Create(ReferenceWidth, ReferenceHeight, (x, y, c) => c switch
        {
            TestFrames.Blue => (byte)((x * 5 + y * 3) % 200),
            TestFrames.Green => (byte)((x * 7 + y) % 180),
            _ => (byte)((x + y * 11) % 220)
        });

        var match = catalog.Match(distractor, "mark", out var score);

        Assert.Null(match);
        Assert.True(score < 0.99, score.ToString("F4"));
    }

    [Fact]
    public void Match_RejectsFramesOfAnotherResolution()
    {
        PublishMark();
        var catalog = new TemplateCatalog(_directory);

        var error = Assert.Throws<InvalidOperationException>(() => catalog.Match(TestFrames.Uniform(64, 64), "mark"));
        Assert.Contains("64×48", error.Message);
    }

    [Fact]
    public void ValidateFrame_RejectsFramesOfAnotherResolution()
    {
        PublishMark();
        var catalog = new TemplateCatalog(_directory);

        var error = Assert.Throws<InvalidOperationException>(() => catalog.ValidateFrame(TestFrames.Uniform(1280, 720)));
        Assert.Equal("此功能当前仅支持 64×48 客户区。", error.Message);
    }

    [Fact]
    public void Match_RejectsUnknownTemplateId()
    {
        PublishMark();
        var catalog = new TemplateCatalog(_directory);

        var error = Assert.Throws<InvalidDataException>(() => catalog.Match(TestFrames.EmbeddedMarkFrame(), "not-shipped"));
        Assert.Contains("模板清单缺少 not-shipped", error.Message);
    }

    [Fact]
    public void Match_RejectsCustomRegionOutsideTheReferenceFrame()
    {
        PublishMark();
        var catalog = new TemplateCatalog(_directory);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            catalog.Match(TestFrames.EmbeddedMarkFrame(), "mark", search: new PixelRegion(50, 0, 20, 20)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public void Constructor_RejectsUnsupportedSchemaVersions(int schemaVersion)
    {
        var fileName = WriteMarkTemplate();
        Publish(new(fileName, Search, 0.72, TestFrames.Sha256Of(Path.Combine(_directory, fileName))), schemaVersion);

        var error = Assert.Throws<InvalidDataException>(() => new TemplateCatalog(_directory));
        Assert.Contains("模板清单格式不受支持", error.Message);
    }

    [Fact]
    public void Constructor_RequiresChannelToDeclareSchemaTwo()
    {
        var fileName = WriteMarkTemplate();
        Publish(new(fileName, Search, 0.72, TestFrames.Sha256Of(Path.Combine(_directory, fileName)), Channel: "RedMinusBlue"), schemaVersion: 1);

        var error = Assert.Throws<InvalidDataException>(() => new TemplateCatalog(_directory));
        Assert.Contains("模板颜色通道要求 Schema 2", error.Message);
    }

    [Fact]
    public void Constructor_RejectsTemplatePathsOutsideTheAssetDirectory()
    {
        var fileName = WriteMarkTemplate();
        Publish(new("../" + fileName, Search, 0.72, TestFrames.Sha256Of(Path.Combine(_directory, fileName))));

        var error = Assert.Throws<InvalidDataException>(() => new TemplateCatalog(_directory));
        Assert.Contains("模板文件名无效", error.Message);
    }

    [Fact]
    public void Constructor_RejectsTamperedTemplateBytes()
    {
        var fileName = WriteMarkTemplate();
        Publish(new(fileName, Search, 0.72, new string('0', 64)));

        var error = Assert.Throws<InvalidDataException>(() => new TemplateCatalog(_directory));
        Assert.Contains("模板校验失败", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.2)]
    [InlineData(1.2)]
    public void Constructor_RejectsOutOfRangeThresholds(double threshold)
    {
        var fileName = WriteMarkTemplate();
        Publish(new(fileName, Search, threshold, TestFrames.Sha256Of(Path.Combine(_directory, fileName))));

        var error = Assert.Throws<InvalidDataException>(() => new TemplateCatalog(_directory));
        Assert.Contains("模板阈值无效", error.Message);
    }

    [Fact]
    public void Constructor_RejectsAnInfinityThresholdWrittenDirectlyIntoTheManifest()
    {
        var fileName = WriteMarkTemplate();
        File.WriteAllText(Path.Combine(_directory, "recognition.json"),
            $$"""
            { "SchemaVersion": 1, "ReferenceWidth": {{ReferenceWidth}}, "ReferenceHeight": {{ReferenceHeight}},
              "Templates": { "mark": { "File": "{{fileName}}", "Search": { "X": 0, "Y": 0, "Width": 40, "Height": 30 },
              "Threshold": 1e999, "Sha256": "{{TestFrames.Sha256Of(Path.Combine(_directory, fileName))}}" } } }
            """);

        var error = Assert.Throws<InvalidDataException>(() => new TemplateCatalog(_directory));
        Assert.Contains("模板阈值无效", error.Message);
    }

    [Fact]
    public void Constructor_RejectsSearchRegionOutsideTheReferenceFrame()
    {
        var fileName = WriteMarkTemplate();
        Publish(new(fileName, new PixelRegion(50, 0, 20, 20), 0.72, TestFrames.Sha256Of(Path.Combine(_directory, fileName))));

        Assert.Throws<ArgumentOutOfRangeException>(() => new TemplateCatalog(_directory));
    }

    [Fact]
    public void Constructor_RejectsTemplateLargerThanItsSearchRegion()
    {
        var fileName = WriteMarkTemplate();
        Publish(new(fileName, new PixelRegion(0, 0, 10, 10), 0.72, TestFrames.Sha256Of(Path.Combine(_directory, fileName))));

        var error = Assert.Throws<InvalidDataException>(() => new TemplateCatalog(_directory));
        Assert.Contains("模板大于搜索区域", error.Message);
    }

    [Fact]
    public void Constructor_RejectsUnsupportedColourChannel()
    {
        var fileName = WriteMarkTemplate();
        Publish(new(fileName, Search, 0.72, TestFrames.Sha256Of(Path.Combine(_directory, fileName)), Channel: "Hue"), schemaVersion: 2);

        var error = Assert.Throws<InvalidDataException>(() => new TemplateCatalog(_directory));
        Assert.Contains("模板颜色通道不受支持", error.Message);
    }

    [Theory]
    [InlineData(0, 48)]
    [InlineData(64, 0)]
    [InlineData(-1, 48)]
    public void Constructor_RejectsInvalidReferenceSize(int referenceWidth, int referenceHeight)
    {
        var fileName = WriteMarkTemplate();
        Publish(new(fileName, Search, 0.72, TestFrames.Sha256Of(Path.Combine(_directory, fileName))), 1, "mark", referenceWidth, referenceHeight);

        Assert.Throws<InvalidDataException>(() => new TemplateCatalog(_directory));
    }

    [Fact]
    public void Constructor_RejectsEmptyTemplateList()
    {
        File.WriteAllText(Path.Combine(_directory, "recognition.json"), JsonSerializer.Serialize(
            new TemplateManifest(1, ReferenceWidth, ReferenceHeight, []), Indented));

        Assert.Throws<InvalidDataException>(() => new TemplateCatalog(_directory));
    }

    [Fact]
    public void Constructor_RejectsEmptyManifestDocument()
    {
        File.WriteAllText(Path.Combine(_directory, "recognition.json"), "null");

        var error = Assert.Throws<InvalidDataException>(() => new TemplateCatalog(_directory));
        Assert.Contains("模板清单为空", error.Message);
    }

    [Fact]
    public void Constructor_RejectsManifestOutsideTheDirectory()
    {
        PublishMark();

        Assert.Throws<ArgumentException>(() => new TemplateCatalog(_directory, "nested/recognition.json"));
    }

    [Fact]
    public void Constructor_BuildsRedMinusBlueTemplatesOnlyFromOpponentContrast()
    {
        var fileName = TemplateName(TestFrames.WritePng(_directory, "opponent.png", TestFrames.MarkWidth, TestFrames.MarkHeight,
            (x, y, c) => TestFrames.OpponentOnlyMark(x + TestFrames.MarkLeft, y + TestFrames.MarkTop, c)));
        Publish(new(fileName, Search, 0.9, TestFrames.Sha256Of(Path.Combine(_directory, fileName)), Channel: "RedMinusBlue"), schemaVersion: 2);
        var catalog = new TemplateCatalog(_directory);

        var match = catalog.Match(TestFrames.Create(ReferenceWidth, ReferenceHeight, TestFrames.OpponentOnlyMark), "mark", out var score);

        Assert.NotNull(match);
        Assert.True(score > 0.99, score.ToString("F4"));
    }

    [Fact]
    public void Constructor_RejectsFlatTemplateImageForItsDeclaredChannel()
    {
        var fileName = TemplateName(TestFrames.WritePng(_directory, "warm-cool.png", 8, 8, TestFrames.WarmCoolConstantDifference));
        Publish(new(fileName, Search, 0.72, TestFrames.Sha256Of(Path.Combine(_directory, fileName)), Channel: "RedMinusBlue"), schemaVersion: 2);

        var error = Assert.Throws<ArgumentException>(() => new TemplateCatalog(_directory));
        Assert.Contains("模板缺少可匹配的图像特征", error.Message);
    }

    [Theory]
    [InlineData("assets/daily", "recognition.json")]
    [InlineData("assets/commissions", "recognition.json")]
    [InlineData("assets/battle-pass", "recognition.json")]
    [InlineData("assets/activity", "recognition.json")]
    [InlineData("assets/navigation", "recognition.json")]
    [InlineData("assets/start-journey", "rewards.json")]
    [InlineData("assets/start-journey", "update.json")]
    public void ShippedBundles_PassEveryIntegrityRule(string directory, string manifest)
    {
        var root = Path.Combine(AppContext.BaseDirectory, directory.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(Path.Combine(root, manifest)), $"发布内容缺少 {directory}/{manifest}");

        var catalog = new TemplateCatalog(root, manifest);

        catalog.ValidateFrame(TestFrames.Uniform(1920, 1080));
    }
}
