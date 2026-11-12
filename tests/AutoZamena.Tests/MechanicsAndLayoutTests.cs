using AutoZamena.Core.Engine;
using AutoZamena.Core.Models;
using Xunit;

namespace AutoZamena.Tests;

public sealed class MechanicsAndLayoutTests
{
    [Theory]
    [InlineData("ПРивет ", "Привет ")]
    [InlineData("ЧТо ", "Что ")]
    [InlineData("HEllo ", "Hello ")]
    [InlineData("МГц ", "МГц ")]
    [InlineData("ПК ", "ПК ")]
    [InlineData("API ", "API ")]
    public void StickyShiftDoubleCaps_FixedWithoutBreakingAcronymsOrUnits(string input, string expected)
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal(expected, engine.SimulateText(input));
    }

    [Theory]
    [InlineData("сво й ", "свой ")]
    [InlineData("приве т ", "привет ")]
    [InlineData("делае т ", "делает ")]
    [InlineData("работае т ", "работает ")]
    public void SpacebarRollover_MergesBrokenStubWithStrayTrailingConsonant(string input, string expected)
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal(expected, engine.SimulateText(input));
    }

    [Fact]
    public void SpacebarRollover_DropsDelayedTailCharAfterAutoCompletedWord()
    {
        var engine = new AutoCorrectEngine();
        long t = 1000;
        foreach (char c in "наза")
        {
            t += 90;
            engine.OnCharacter(new KeyStrokeEvent(c, 90, false, t));
        }

        t += 50;
        CorrectionResult firstSpace = engine.OnWhitespace(' ', t);
        Assert.Equal("назад", firstSpace.ReplacementText);

        // User's ring finger hits 'д' 25ms AFTER spacebar rollover, followed by Space
        t += 25;
        engine.OnCharacter(new KeyStrokeEvent('д', 25, true, t));
        t += 80;
        CorrectionResult secondSpace = engine.OnWhitespace(' ', t);

        Assert.Equal(CorrectionKind.SpacebarRolloverDropLateTail, secondSpace.Kind);
        Assert.Equal(1, secondSpace.BackspacesCount);
        Assert.Equal(string.Empty, secondSpace.AppendText);
        Assert.Null(secondSpace.TrailingDelimiter);
    }

    [Theory]
    [InlineData("ghbdtn ", "привет ")]
    [InlineData("k.,k. ", "люблю ")]
    [InlineData(";bpym ", "жизнь ")]
    [InlineData("[kt, ", "хлеб ")]
    [InlineData("руддщ ", "hello ")]
    public void WrongKeyboardLayout_AutoFixesHighConfidenceWordsIncludingPunctuationKeys(string input, string expected)
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal(expected, engine.SimulateText(input));
    }

    [Fact]
    public void HotkeyLayoutConversion_ConvertsEvenShortTokensAndUpdatesDirection()
    {
        var engine = new AutoCorrectEngine();

        foreach (char c in "jr")
        {
            engine.OnCharacter(new KeyStrokeEvent(c));
        }

        CorrectionResult hk = engine.ConvertLastWordLayoutByHotkey();
        Assert.Equal(CorrectionKind.HotkeyLayoutSwitch, hk.Kind);
        Assert.Equal("ок", hk.ReplacementText);
        Assert.Equal(TargetLanguage.Russian, hk.SwitchLayoutTo);
    }

    [Fact]
    public void ImmediateBackspaceUndo_RevertsCorrectionAndLearnsWordWhenConfirmedWithSpace()
    {
        var engine = new AutoCorrectEngine();

        // 1. Type "привте " -> becomes "привет ", then press Backspace (\b) -> reverts to "привте"
        string afterUndo = engine.SimulateText("привте \b");
        Assert.Equal("привте", afterUndo);

        // 2. Now press Space to confirm we really wanted "привте" -> stays "привте " and learns into UserDictionary!
        CorrectionResult confirmSpace = engine.OnWhitespace(' ');
        Assert.False(confirmSpace.HasChange);
        Assert.True(engine.UserDictionary.IsIgnored("привте"));

        // 3. Typing "привте " again in a new sentence must NOT touch it anymore
        engine.ResetContext();
        Assert.Equal("привте ", engine.SimulateText("привте "));
    }
}
