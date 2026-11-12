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
    [InlineData("чот ", "что ")]
    [InlineData("пожалуста ", "пожалуйста ")]
    [InlineData("незнаю ", "не знаю ")]
    [InlineData("вобщем ", "в общем ")]
    [InlineData("програма ", "программа ")]
    [InlineData("teh ", "the ")]
    [InlineData("recieve ", "receive ")]
    public void CommonOrthographicAndTranspositionTypos_AreFixed(string input, string expected)
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal(expected, engine.SimulateText(input));
    }

    [Fact]
    public void DiffCalculation_UsesMinimalBackspaces()
    {
        var engine = new AutoCorrectEngine();

        // "наза" -> "назад": common prefix "наза" (4 chars) => ZERO backspaces!
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
}
