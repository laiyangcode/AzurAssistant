using System.Globalization;
using System.IO;
using System.Text.Json;
using AzurAssistant.Composition;
using AzurAssistant.Features.Commissions;
using AzurAssistant.Runtime;
using AzurAssistant.UI;

namespace AzurAssistant.Tests;

/// <summary>Covers the clock, hotkey, ordering, startup-argument and display-conversion rules that need no game.</summary>
public class RuntimeLogicTests : IDisposable
{
    private static readonly DateTimeOffset WeekMonday04 = new(2026, 1, 5, 4, 0, 0, TimeSpan.FromHours(8));
    private readonly string _directory = TestFrames.CreateTempDirectory("runtime-logic");

    public void Dispose() => TestFrames.DeleteTempDirectory(_directory);

    private string NextPath(string name) => Path.Combine(_directory, name);

    [Fact]
    public void ResponseTiming_NormalModeIsUnscaled()
    {
        var timing = ResponseTiming.FromLowPerformanceMode(false);

        Assert.Equal(1d, timing.Factor);
        Assert.Equal(TimeSpan.FromMilliseconds(200), timing.Scale(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(TimeSpan.Zero, timing.Scale(TimeSpan.Zero));
    }

    [Fact]
    public void ResponseTiming_LowPerformanceModeScalesWaitingOnly()
    {
        var timing = ResponseTiming.FromLowPerformanceMode(true);

        Assert.Equal(1.5d, timing.Factor);
        Assert.Equal(TimeSpan.FromMilliseconds(300), timing.Scale(TimeSpan.FromMilliseconds(200)));
        Assert.Throws<ArgumentOutOfRangeException>(() => timing.Scale(TimeSpan.FromMilliseconds(-1)));
    }

    [Theory]
    [InlineData("Ctrl+Alt+F5", "Ctrl+Alt+F5", 1u | 2u, 0x74u)]
    [InlineData("shift+ctrl+a", "Ctrl+Shift+A", 2u | 4u, 0x41u)]
    [InlineData("F1", "F1", 0u, 0x70u)]
    [InlineData("ctrl+home", "Ctrl+Home", 2u, 0x24u)]
    [InlineData("CONTROL+PageUp", "Ctrl+PageUp", 2u, 0x21u)]
    [InlineData("alt+3", "Alt+3", 1u, 0x33u)]
    public void HotkeyBinding_AcceptsAndNormalisesValidShortcuts(string input, string expectedText, uint modifiers, uint virtualKey)
    {
        Assert.True(HotkeyBinding.TryParse(input, out var binding, out var error), error);
        Assert.Equal(expectedText, binding.Text);
        Assert.Equal(modifiers, binding.Modifiers);
        Assert.Equal(virtualKey, binding.VirtualKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+Win+A")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("F12")]
    [InlineData("A")]
    [InlineData("Shift+A")]
    [InlineData("Ctrl+Esc")]
    [InlineData("Alt+Tab")]
    [InlineData("Alt+F4")]
    [InlineData("Alt+Space")]
    [InlineData("Ctrl+Alt+Delete")]
    [InlineData("Ctrl+Meta+Alt+Shift+F25")]
    public void HotkeyBinding_RejectsUnusableShortcuts(string? input) =>
        Assert.False(HotkeyBinding.TryParse(input, out _, out _));

    [Fact]
    public void HotkeyBinding_ExplainsWhyF12IsReserved()
    {
        Assert.False(HotkeyBinding.TryParse("Ctrl+F12", out _, out var error));
        Assert.Contains("F12", error);
    }

    [Fact]
    public void HotkeyBinding_ExplainsThatPlainKeysNeedCtrlOrAlt()
    {
        Assert.False(HotkeyBinding.TryParse("Shift+K", out _, out var error));
        Assert.Contains("Ctrl 或 Alt", error);
    }

    [Fact]
    public void HotkeyBinding_PairRejectsDuplicatesAndNamesTheOffendingField()
    {
        Assert.True(HotkeyBinding.TryParsePair("Ctrl+F8", "Ctrl+F9", out var pause, out var stop, out var error));
        Assert.Equal("Ctrl+F8", pause.Text);
        Assert.Equal("Ctrl+F9", stop.Text);

        Assert.False(HotkeyBinding.TryParsePair("Ctrl+F8", "Ctrl+F8", out _, out _, out error));
        Assert.Contains("不能使用相同的快捷键", error);

        Assert.False(HotkeyBinding.TryParsePair("F12", "Ctrl+F9", out _, out _, out error));
        Assert.StartsWith("暂停 / 继续：", error);

        Assert.False(HotkeyBinding.TryParsePair("Ctrl+F8", "F12", out _, out _, out error));
        Assert.StartsWith("停止所有任务：", error);
    }

    [Fact]
    public void HotkeyBinding_RejectsModifierBitsOutsideCtrlAltShift()
    {
        Assert.False(HotkeyBinding.TryFromVirtualKey(8, 0x41, out _, out var error));
        Assert.Contains("Windows 键", error);
    }

    [Fact]
    public void HotkeyBinding_RejectsKeysWithoutAName()
    {
        Assert.False(HotkeyBinding.TryFromVirtualKey(0, 0x60, out _, out var error));
        Assert.Contains("不能单独绑定", error);
    }

    [Fact]
    public void DailyFeatureOrder_KnowsTheEightDailyFeatures()
    {
        string[] expected = ["claim-mail", "home-center", "claim-activity", "claim-battle-pass", "weekly-echoes",
            "daily-commissions", "crisis-raids", "story-commissions"];

        Assert.Equal(expected, DailyFeatureOrder.Choices.Select(c => c.Id).ToArray());
    }

    [Fact]
    public void DailyFeatureOrder_EmptyOrderKeepsTheBuiltInSequence() =>
        Assert.Equal(DailyFeatureOrder.Choices.Select(c => c.Id).ToArray(), DailyFeatureOrder.Resolve("").Select(c => c.Id).ToArray());

    [Fact]
    public void DailyFeatureOrder_PutsChosenItemsFirstAndAppendsTheRest()
    {
        string[] expected = ["crisis-raids", "claim-mail", "home-center", "claim-activity", "claim-battle-pass",
            "weekly-echoes", "daily-commissions", "story-commissions"];

        Assert.Equal(expected, DailyFeatureOrder.Resolve("crisis-raids,claim-mail").Select(c => c.Id).ToArray());
    }

    [Theory]
    [InlineData("claim-mail,claim-mail")]
    [InlineData("not-a-feature")]
    [InlineData("claim-mail,")]
    [InlineData(",")]
    public void DailyFeatureOrder_RejectsDuplicateOrUnknownIds(string order)
    {
        var error = Assert.Throws<InvalidDataException>(() => DailyFeatureOrder.Resolve(order));
        Assert.Equal("一条龙顺序包含重复或未支持的任务ID。", error.Message);
    }

    [Fact]
    public void StartupOptions_DefaultsToManualStart()
    {
        var options = StartupOptions.Parse([]);

        Assert.False(options.RunDaily);
        Assert.Null(options.WaitForProcess);
        Assert.False(options.ExitAfterDaily);
        Assert.False(options.SkipStartupActions);
    }

    [Fact]
    public void StartupOptions_ReadsEverySupportedArgument()
    {
        var options = StartupOptions.Parse(["--run-daily", "--exit-after-daily", "--wait-for-process", "4242"]);

        Assert.True(options.RunDaily);
        Assert.True(options.ExitAfterDaily);
        Assert.Equal(4242, options.WaitForProcess);
    }

    [Fact]
    public void StartupOptions_AllowsSkippingStartupActionsAfterAnUpdate()
    {
        var options = StartupOptions.Parse(["--skip-startup-actions", "--wait-for-process", "7"]);

        Assert.True(options.SkipStartupActions);
        Assert.Equal(7, options.WaitForProcess);
    }

    [Theory]
    [InlineData("--exit-after-daily")]
    [InlineData("--skip-startup-actions --run-daily")]
    [InlineData("--wait-for-process")]
    [InlineData("--wait-for-process 0")]
    [InlineData("--wait-for-process abc")]
    [InlineData("--run-dail")]
    public void StartupOptions_RejectsInvalidArgumentCombinations(string args) =>
        Assert.Throws<ArgumentException>(() => StartupOptions.Parse(args.Split(' ')));

    [Theory]
    [InlineData(2026, 1, 5, 3, 59, "2025-12-29")]
    [InlineData(2026, 1, 5, 4, 0, "2026-01-05")]
    [InlineData(2026, 1, 5, 23, 30, "2026-01-05")]
    [InlineData(2026, 1, 11, 4, 0, "2026-01-05")]
    [InlineData(2026, 1, 12, 3, 59, "2026-01-05")]
    [InlineData(2026, 1, 12, 4, 0, "2026-01-12")]
    public void WeeklyProgress_WeekKeyRollsOverOnMondayMorningBeijing(int year, int month, int day, int hour, int minute, string expected) =>
        Assert.Equal(expected, WeeklyProgressStore.WeekKey(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(8))));

    [Fact]
    public void WeeklyProgress_WeekKeyConvertsOtherOffsetsFirst() =>
        Assert.Equal("2026-01-05", WeeklyProgressStore.WeekKey(new DateTimeOffset(2026, 1, 4, 21, 30, 0, TimeSpan.Zero)));

    [Fact]
    public void WeeklyProgress_RoundTripsRemainingCountWithinTheSameWeek()
    {
        var path = NextPath("weekly.json");
        var store = new WeeklyProgressStore(path, () => WeekMonday04);

        store.ObserveRemaining(2);
        Assert.Equal(1, store.Claimed);

        store.ObserveRemaining(0);
        Assert.Equal(3, store.Claimed);

        store.Reset();
        Assert.Equal(0, store.Claimed);
    }

    [Fact]
    public void WeeklyProgress_RejectsRemainingCountsTheGameCannotReport()
    {
        var store = new WeeklyProgressStore(NextPath("invalid.json"), () => WeekMonday04);

        Assert.Throws<ArgumentOutOfRangeException>(() => store.ObserveRemaining(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ObserveRemaining(4));
    }

    [Fact]
    public void WeeklyProgress_IgnoresAFileFromAnotherWeek()
    {
        var path = NextPath("stale.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new WeeklyProgress(1, "2020-01-06", 3)));

        Assert.Equal(0, new WeeklyProgressStore(path, () => WeekMonday04).Claimed);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    public void WeeklyProgress_IgnoresACorruptedFile(string content)
    {
        var path = NextPath("corrupt.json");
        File.WriteAllText(path, content);

        Assert.Equal(0, new WeeklyProgressStore(path, () => WeekMonday04).Claimed);
    }

    [Fact]
    public void WeeklyProgress_IgnoresUnsupportedSchemaAndOutOfRangeCounts()
    {
        var week = WeeklyProgressStore.WeekKey(WeekMonday04);
        File.WriteAllText(NextPath("schema.json"), JsonSerializer.Serialize(new WeeklyProgress(2, week, 1)));
        File.WriteAllText(NextPath("range.json"), JsonSerializer.Serialize(new WeeklyProgress(1, week, 9)));

        Assert.Equal(0, new WeeklyProgressStore(NextPath("schema.json"), () => WeekMonday04).Claimed);
        Assert.Equal(0, new WeeklyProgressStore(NextPath("range.json"), () => WeekMonday04).Claimed);
    }

    [Fact]
    public void WeeklyProgress_TreatsAMissingFileAsNothingClaimed() =>
        Assert.Equal(0, new WeeklyProgressStore(NextPath("absent.json"), () => WeekMonday04).Claimed);

    [Theory]
    [InlineData(1, "Ⅰ")]
    [InlineData(2, "Ⅱ")]
    [InlineData(3, "Ⅲ")]
    [InlineData(4, "Ⅳ")]
    [InlineData(5, "Ⅴ")]
    [InlineData(6, "Ⅵ")]
    [InlineData(0, "—")]
    [InlineData(7, "—")]
    [InlineData(null, "—")]
    public void DifficultyLabel_ShowsTheGamesRomanTiers(object? value, string expected) =>
        Assert.Equal(expected, new DifficultyLabelConverter().Convert(value!, typeof(string), null!, CultureInfo.CurrentCulture));

    [Fact]
    public void DifficultyLabel_ConvertBackLeavesTheStoredNumberAlone() =>
        Assert.Same(System.Windows.Data.Binding.DoNothing,
            new DifficultyLabelConverter().ConvertBack("Ⅰ", typeof(int), null!, CultureInfo.CurrentCulture));
}
