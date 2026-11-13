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
    [InlineData("пРИВЕТ ", "Привет ")]
    [InlineData("чТО ", "Что ")]
    [InlineData("hELLO ", "Hello ")]
    [InlineData("пРИВТЕ ", "Привет ")]
    [InlineData("iOS ", "iOS ")]
    [InlineData("mRNA ", "mRNA ")]
    [InlineData("eSIM ", "eSIM ")]
    [InlineData("кВт ", "кВт ")]
    public void InvertedCapsLock_FixedAndRequestsCapsShutoffWithoutBreakingTechUnits(string input, string expected)
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal(expected, engine.SimulateText(input));

        engine.ResetContext();
        foreach (char c in "пРИВЕТ")
        {
            engine.OnCharacter(new KeyStrokeEvent(c));
        }
        CorrectionResult res = engine.OnWhitespace(' ');
        Assert.Equal(CorrectionKind.InvertedCaps, res.Kind);
        Assert.True(res.TurnOffCapsLock);
        Assert.Equal("Привет", res.ReplacementText);
    }

    [Theory]
    [InlineData("ghbdtn ", "привет ")]
    [InlineData("k.,k. ", "люблю ")]
    [InlineData(";bpym ", "жизнь ")]
    [InlineData("[kt, ", "хлеб ")]
    [InlineData("\"njn ", "Этот ")]
    [InlineData("'nj ", "это ")]
    [InlineData("e;t ", "уже ")]
    [InlineData(",tp ", "без ")]
    [InlineData("ghbdnt ", "привет ")]
    [InlineData("gj;fkecnf ", "пожалуйста ")]
    [InlineData("xnjnj ", "что-то ")]
    [InlineData("djpdhfgofncz ", "возвращаться ")]
    [InlineData("руддщ ", "hello ")]
    [InlineData("иусгфыу ", "because ")]
    [InlineData("hellp ", "help ")]
    [InlineData("helol ", "hello ")]
    public void WrongKeyboardLayout_AutoFixesExactAndTypoWordsAnd3CharPunctuationTokens(string input, string expected)
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal(expected, engine.SimulateText(input));
    }

    [Theory]
    [InlineData("z yt pyf. ", "я не знаю ")]
    [InlineData("d ntktuhfvt ", "в телеграме ")]
    [InlineData("rfr ltkf ", "как дела ")]
    [InlineData("ok ghbdtn ", "ok привет ")]
    public void WrongKeyboardLayout_RetroactivelyConvertsShortPrecedingPrepositionsAndPronouns(string input, string expected)
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal(expected, engine.SimulateText(input));
    }

    [Fact]
    public void RetroactivePhraseWrongLayout_SupportsSingleKeyBackspaceUndo()
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal("z yt pyf.", engine.SimulateText("z yt pyf. \b"));
    }

    [Fact]
    public void PhraseHotkeyLayoutConversion_ConvertsWholeLineWithoutClipboardAndTogglesBack()
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal("ye lf ", engine.SimulateText("ye lf "));

        CorrectionResult toRu = engine.ConvertPhraseLayoutByHotkey();
        Assert.True(toRu.HasChange);
        Assert.Equal("ну да ", toRu.ReplacementText);
        Assert.Equal(TargetLanguage.Russian, toRu.SwitchLayoutTo);

        CorrectionResult backToEn = engine.ConvertPhraseLayoutByHotkey();
        Assert.True(backToEn.HasChange);
        Assert.Equal("ye lf ", backToEn.ReplacementText);
        Assert.Equal(TargetLanguage.English, backToEn.SwitchLayoutTo);
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
    public void HotkeyLayoutConversion_WorksAfterSpaceOnKnownWordsAndTogglesBack()
    {
        var engine = new AutoCorrectEngine();

        // "tot " is a valid English word, so auto-layout leaves it untouched until explicit layout hotkey
        Assert.Equal("tot ", engine.SimulateText("tot "));

        CorrectionResult toRu = engine.ConvertLastWordLayoutByHotkey();
        Assert.True(toRu.HasChange);
        Assert.Equal("еще ", toRu.ReplacementText);
        Assert.Equal(TargetLanguage.Russian, toRu.SwitchLayoutTo);

        // Repeating the hotkey toggles back to "tot "
        CorrectionResult backToEn = engine.ConvertLastWordLayoutByHotkey();
        Assert.True(backToEn.HasChange);
        Assert.Equal("tot ", backToEn.ReplacementText);
        Assert.Equal(TargetLanguage.English, backToEn.SwitchLayoutTo);
    }

    [Fact]
    public void UndoOnSentenceAutoCapitalize_SuppressesCapOnceWithoutPollutingIgnoredDictionary()
    {
        var engine = new AutoCorrectEngine();

        // Type "привет. на " -> auto-capitalizes to "привет. На ", then Backspace reverts to "привет. на"
        string afterUndo = engine.SimulateText("привет. на \b");
        Assert.Equal("привет. на", afterUndo);

        // Pressing Space again keeps lowercase "на " once AND must NOT add common word "на" to IgnoredWords.
        CorrectionResult confirmSpace = engine.OnWhitespace(' ');
        Assert.False(confirmSpace.HasChange);
        Assert.False(engine.UserDictionary.IsIgnored("на"));

        //Space-before-period chat style ("ок . на ") also capitalizes cleanly
        engine.ResetContext();
        Assert.Equal("ок . На ", engine.SimulateText("ок . на "));
    }

    [Fact]
    public void ImmediateBackspaceUndo_RevertsCorrectionAndLearnsWordWhenConfirmedWithSpace()
    {
        var engine = new AutoCorrectEngine();

        // 1. Type "привте " -> becomes "привет ", then press Backspace (\b) -> reverts to "привте"
        string afterUndo = engine.SimulateText("привте \b");
        Assert.Equal("привте", afterUndo);

        // 2. Now press Space to confirm we really wanted "привте" -> stays "привте " and learns into UserDictionary.
        CorrectionResult confirmSpace = engine.OnWhitespace(' ');
        Assert.False(confirmSpace.HasChange);
        Assert.True(engine.UserDictionary.IsIgnored("привте"));

        // 3. Typing "привте " again in a new sentence must NOT touch it anymore
        engine.ResetContext();
        Assert.Equal("привте ", engine.SimulateText("привте "));
    }
}
