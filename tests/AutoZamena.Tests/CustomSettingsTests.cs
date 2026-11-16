using System.Text.Json;
using AutoZamena.Core.Engine;
using AutoZamena.Core.Models;
using AutoZamena.Core.Scoring;
using AutoZamena.Core.Lexicon;
using Xunit;

namespace AutoZamena.Tests;

public sealed class CustomSettingsTests
{
    [Theory]
    [InlineData(0, SensitivityLevel.Conservative)]
    [InlineData(50, SensitivityLevel.Balanced)]
    [InlineData(100, SensitivityLevel.Aggressive)]
    [InlineData(-100, SensitivityLevel.Conservative)]
    [InlineData(200, SensitivityLevel.Aggressive)]
    public void CustomSensitivity_EndpointsMatchExistingPresetsForBothLanguages(int percent, SensitivityLevel preset)
    {
        foreach (string word in new[] { "привте", "кмпьютер", "возврапщатся" })
        {
            var expected = NoisyChannelScorer.FindBestRuCandidate(
                LexiconDatabase.Default, word, ReadOnlySpan<KeyStrokeEvent>.Empty, null, preset, false);
            var actual = NoisyChannelScorer.FindBestRuCandidate(
                LexiconDatabase.Default, word, ReadOnlySpan<KeyStrokeEvent>.Empty, null,
                SensitivityLevel.Custom, false, percent);
            Assert.Equal(expected, actual);
        }
        foreach (string word in new[] { "hellp", "helol", "wrold" })
        {
            var expected = NoisyChannelScorer.FindBestEnCandidate(
                LexiconDatabase.Default, word, ReadOnlySpan<KeyStrokeEvent>.Empty, null, preset, false);
            var actual = NoisyChannelScorer.FindBestEnCandidate(
                LexiconDatabase.Default, word, ReadOnlySpan<KeyStrokeEvent>.Empty, null,
                SensitivityLevel.Custom, false, percent);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void CustomSensitivity_PersistsAndAffectsTypingEngine()
    {
        var settings = new EngineSettings { Sensitivity = SensitivityLevel.Custom, CustomSensitivity = 37 };
        EngineSettings? loaded = JsonSerializer.Deserialize<EngineSettings>(JsonSerializer.Serialize(settings));
        Assert.NotNull(loaded);
        Assert.Equal(37, loaded!.CustomSensitivity);
        Assert.Equal(SensitivityLevel.Custom, loaded.Sensitivity);
        Assert.Equal(37, loaded.Clone().CustomSensitivity);

        var engine = new AutoCorrectEngine(settings: loaded);
        Assert.Equal("привет ", engine.SimulateText("привте "));
    }

    [Theory]
    [InlineData(LayoutSwitchMode.Off, false, false)]
    [InlineData(LayoutSwitchMode.HotkeyOnly, false, true)]
    [InlineData(LayoutSwitchMode.AutoHighConfidenceAndHotkey, true, true)]
    [InlineData(LayoutSwitchMode.AutoOnly, true, false)]
    public void LayoutMode_OffersAllAutomaticAndManualCombinations(LayoutSwitchMode mode, bool automatic, bool manual)
    {
        Assert.Equal(automatic, mode.AllowsAutomaticCorrection());
        Assert.Equal(manual, mode.AllowsManualConversion());
        Assert.Equal(manual, mode.WithAutomaticCorrection(false).AllowsManualConversion());
        Assert.Equal(manual, mode.WithAutomaticCorrection(true).AllowsManualConversion());
        Assert.False(mode.WithAutomaticCorrection(false).AllowsAutomaticCorrection());
        Assert.True(mode.WithAutomaticCorrection(true).AllowsAutomaticCorrection());
    }

    [Fact]
    public void AutomaticOnly_StillFixesWrongLayoutButHotkeyOnlyDoesNot()
    {
        Assert.Equal("привет ", new AutoCorrectEngine(settings: new EngineSettings
        {
            LayoutMode = LayoutSwitchMode.AutoOnly
        }).SimulateText("ghbdtn "));
        Assert.Equal("ghbdtn ", new AutoCorrectEngine(settings: new EngineSettings
        {
            LayoutMode = LayoutSwitchMode.HotkeyOnly
        }).SimulateText("ghbdtn "));
    }
}
