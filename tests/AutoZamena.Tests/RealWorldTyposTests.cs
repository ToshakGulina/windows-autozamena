using AutoZamena.Core.Engine;
using AutoZamena.Core.Models;
using AutoZamena.Core.Scoring;
using Xunit;

namespace AutoZamena.Tests;

public sealed class RealWorldTyposTests
{
    [Fact]
    public void UserPromptFullSentence_FixesAllTyposAndPreservesEveryValidWord()
    {
        var engine = new AutoCorrectEngine();
        const string input =
            "жутко бесят постоянные опечатки на ПК. на телефоне печатаю быстро без задержек, " +
            "а здесь приходится постоянно возврапщатся наза и поправлять. ";

        const string expected =
            "жутко бесят постоянные опечатки на ПК. На телефоне печатаю быстро без задержек, " +
            "а здесь приходится постоянно возвращаться назад и поправлять. ";

        string actual = engine.SimulateText(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ContextualDisambiguation_NazaResolvesByLeftContext()
    {
        var engine = new AutoCorrectEngine();

        Assert.Equal("возвращаться назад ", engine.SimulateText("возвращаться наза "));
        engine.ResetContext();

        Assert.Equal("пять минут назад ", engine.SimulateText("пять минут наза "));
        engine.ResetContext();

        Assert.Equal("запах газа ", engine.SimulateText("запах наза "));
    }

    [Fact]
    public void ReflexiveVerbGrammar_TsyaVsTsya_ResolvedByContext()
    {
        var engine = new AutoCorrectEngine();

        Assert.Equal("надо учиться ", engine.SimulateText("надо учится "));
        engine.ResetContext();

        Assert.Equal("должно получиться ", engine.SimulateText("должно получится "));
        engine.ResetContext();

        Assert.Equal("он учится ", engine.SimulateText("он учиться "));
    }

    [Theory]
    [InlineData("привте ", "привет ")]
    [InlineData("приввет ", "привет ")]
    [InlineData("чот ", "что ")]
    [InlineData("пожалуста ", "пожалуйста ")]
    [InlineData("незнаю ", "не знаю ")]
    [InlineData("вобщем ", "в общем ")]
    [InlineData("програма ", "программа ")]
    [InlineData("чтото ", "что-то ")]
    [InlineData("порусски ", "по-русски ")]
    [InlineData("вопервых ", "во-первых ")]
    [InlineData("по-руски ", "по-русски ")]
    [InlineData("кто-нибуть ", "кто-нибудь ")]
    [InlineData("интернет-магозин ", "интернет-магазин ")]
    [InlineData("teh ", "the ")]
    [InlineData("recieve ", "receive ")]
    [InlineData("dont ", "don't ")]
    [InlineData("doesnt ", "doesn't ")]
    public void CommonOrthographicAndTranspositionTypos_AreFixed(string input, string expected)
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal(expected, engine.SimulateText(input));
    }

    [Fact]
    public void QuotesAndParenthesesAroundWords_AllowCleanCorrectionInsideProse()
    {
        var engine = new AutoCorrectEngine();
        const string input = "он написал (привте) и «возврапщатся» и \"пожалуста\" в чат ";
        const string expected = "он написал (привет) и «возвращаться» и \"пожалуйста\" в чат ";

        Assert.Equal(expected, engine.SimulateText(input));
    }

    [Fact]
    public void FollowUpSentenceWithImperativesAndCases_PreservesAllValidWordsAndCapitalizesSentence()
    {
        var engine = new AutoCorrectEngine();
        const string input = "проверь ещё раз, чтоб не было багов и ошибок. и улучши, если необходимо ";
        const string expected = "проверь ещё раз, чтоб не было багов и ошибок. И улучши, если необходимо ";

        Assert.Equal(expected, engine.SimulateText(input));
    }

    [Fact]
    public void DiffCalculation_UsesMinimalBackspaces()
    {
        var engine = new AutoCorrectEngine();

        // "наза" -> "назад": common prefix "наза" (4 chars) => zero backspaces.
        foreach (char c in "наза")
        {
            engine.OnCharacter(new KeyStrokeEvent(c));
        }
        CorrectionResult nazaRes = engine.OnWhitespace(' ');
        Assert.True(nazaRes.HasChange);
        Assert.Equal("назад", nazaRes.ReplacementText);
        Assert.Equal(0, nazaRes.BackspacesCount);
        Assert.Equal("д", nazaRes.AppendText);

        // "возврапщатся" -> "возвращаться": common prefix "возвра" (6 chars) => 6 backspaces ("пщатся") + "щаться"
        engine.ResetContext();
        foreach (char c in "приходится ")
        {
            engine.OnCharacter(new KeyStrokeEvent(c));
        }
        foreach (char c in "возврапщатся")
        {
            engine.OnCharacter(new KeyStrokeEvent(c));
        }
        CorrectionResult vozvRes = engine.OnWhitespace(' ');
        Assert.True(vozvRes.HasChange);
        Assert.Equal("возвращаться", vozvRes.ReplacementText);
        Assert.Equal(6, vozvRes.BackspacesCount);
        Assert.Equal("щаться", vozvRes.AppendText);
    }

    [Fact]
    public void FastChordTiming_ReducesFatFingerInsertionCost()
    {
        // Normal typing interval (~120 ms)
        var slowEvents = new KeyStrokeEvent[]
        {
            new('в', 120), new('о', 120), new('з', 120), new('в', 120), new('р', 120),
            new('а', 120), new('п', 120), new('щ', 120), new('а', 120), new('т', 120),
            new('ь', 120), new('с', 120), new('я', 120)
        };

        // Fast accidental chord on 'п' right after 'а' (12 ms overlap!)
        var chordEvents = new KeyStrokeEvent[]
        {
            new('в', 120), new('о', 120), new('з', 120), new('в', 120), new('р', 120),
            new('а', 120), new('п', 12, OverlapWithPrev: true), new('щ', 110), new('а', 120),
            new('т', 120), new('ь', 120), new('с', 120), new('я', 120)
        };

        float slowCost = NoisyChannelScorer.ComputePhysicalEditCost("возврапщаться", "возвращаться", slowEvents);
        float chordCost = NoisyChannelScorer.ComputePhysicalEditCost("возврапщаться", "возвращаться", chordEvents);

        Assert.True(chordCost < slowCost, $"Expected chordCost ({chordCost}) < slowCost ({slowCost})");
    }

    [Theory]
    [InlineData("тчо ", "что ")]
    [InlineData("чтщ ", "что ")]
    [InlineData("шде ", "где ")]
    [InlineData("кще ", "еще ")]
    [InlineData("евсь ", "весь ")]
    [InlineData("кмпьютер ", "компьютер ")]
    [InlineData("комьютер ", "компьютер ")]
    [InlineData("докумнт ", "документ ")]
    [InlineData("тестировшик ", "тестировщик ")]
    [InlineData("инжинер ", "инженер ")]
    [InlineData("извени ", "извини ")]
    [InlineData("извените ", "извините ")]
    [InlineData("малако ", "молоко ")]
    [InlineData("харашо ", "хорошо ")]
    [InlineData("сичас ", "сейчас ")]
    [InlineData("настройкех ", "настройках ")]
    [InlineData("исправляит ", "исправляет ")]
    [InlineData("переключатиль ", "переключатель ")]
    [InlineData("пользоватиль ", "пользователь ")]
    [InlineData("документацея ", "документация ")]
    [InlineData("конфигурацея ", "конфигурация ")]
    public void ExtendedFirstCharTranspositionsModernWordsAndLongInflections_AreFixed(string input, string expected)
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal(expected, engine.SimulateText(input));
    }

    [Theory]
    [InlineData("сво ")]
    [InlineData("кпд ")]
    [InlineData("мгу ")]
    [InlineData("ивановскийх ")]
    public void ShortAbbreviationsAndLowFrequencyTwoEditNoise_StayUntouched(string validOrAmbiguous)
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal(validOrAmbiguous, engine.SimulateText(validOrAmbiguous));
    }
}
