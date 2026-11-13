using AutoZamena.Core.Engine;
using Xunit;

namespace AutoZamena.Tests;

public sealed class RunTogetherSplittingTests
{
    [Theory]
    [InlineData("приветмир ", "привет мир ")]
    [InlineData("какдела ", "как дела ")]
    [InlineData("случайнослитые ", "случайно слитые ")]
    [InlineData("слитыеслова ", "слитые слова ")]
    [InlineData("неразделяет ", "не разделяет ")]
    [InlineData("неисправляет ", "не исправляет ")]
    [InlineData("неработает ", "не работает ")]
    [InlineData("небудет ", "не будет ")]
    [InlineData("нехочу ", "не хочу ")]
    [InlineData("немогу ", "не могу ")]
    [InlineData("ненадо ", "не надо ")]
    [InlineData("ненужно ", "не нужно ")]
    [InlineData("неочень ", "не очень ")]
    [InlineData("уменя ", "у меня ")]
    [InlineData("утебя ", "у тебя ")]
    [InlineData("сомной ", "со мной ")]
    [InlineData("комне ", "ко мне ")]
    [InlineData("вэтом ", "в этом ")]
    [InlineData("наэтом ", "на этом ")]
    [InlineData("всеравно ", "все равно ")]
    [InlineData("досвидания ", "до свидания ")]
    [InlineData("врядли ", "вряд ли ")]
    [InlineData("тоесть ", "то есть ")]
    [InlineData("какбудто ", "как будто ")]
    [InlineData("добрыйдень ", "добрый день ")]
    [InlineData("можетбыть ", "может быть ")]
    [InlineData("большоеспасибо ", "большое спасибо ")]
    [InlineData("янезнаю ", "я не знаю ")]
    [InlineData("насамомделе ", "на самом деле ")]
    [InlineData("чтоэтотакое ", "что это такое ")]
    [InlineData("откуданибудь ", "откуда-нибудь ")]
    [InlineData("какойлибо ", "какой-либо ")]
    [InlineData("зачемто ", "зачем-то ")]
    [InlineData("коекто ", "кое-кто ")]
    public void RussianRunTogetherWordsAndParticles_AreSplitCleanly(string input, string expected)
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal(expected, engine.SimulateText(input));
    }

    [Theory]
    [InlineData("goodmorning ", "good morning ")]
    [InlineData("thankyou ", "thank you ")]
    [InlineData("ofcourse ", "of course ")]
    [InlineData("alot ", "a lot ")]
    [InlineData("onthe ", "on the ")]
    [InlineData("inthe ", "in the ")]
    [InlineData("tothe ", "to the ")]
    [InlineData("forthe ", "for the ")]
    [InlineData("isnot ", "is not ")]
    [InlineData("willbe ", "will be ")]
    [InlineData("havebeen ", "have been ")]
    [InlineData("dontknow ", "don't know ")]
    [InlineData("idontknow ", "I don't know ")]
    public void EnglishRunTogetherWords_AreSplitCleanly(string input, string expected)
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal(expected, engine.SimulateText(input));
    }

    [Fact]
    public void SplitWords_PreserveCapitalizationPunctuationAndSupportImmediateBackspaceUndo()
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal(
            "Привет мир! Случайно слитые слова больше не мешают. ",
            engine.SimulateText("Приветмир! случайнослитые слова больше немешают. ")
        );

        engine.ResetContext();
        Assert.Equal("приветмир", engine.SimulateText("приветмир \b"));
    }

    [Theory]
    [InlineData("видеозвонок ")]
    [InlineData("автозамена ")]
    [InlineData("многопоточность ")]
    [InlineData("самообучение ")]
    [InlineData("перезапустить ")]
    [InlineData("пофиксить ")]
    [InlineData("подключить ")]
    [InlineData("небольшой ")]
    [InlineData("некастомный ")]
    [InlineData("Яне ")]
    [InlineData("codebase ")]
    [InlineData("filename ")]
    [InlineData("workplace ")]
    [InlineData("workflow ")]
    [InlineData("username ")]
    [InlineData("lockscreen ")]
    public void CompoundWordsVerbPrefixesAndNames_AreNeverFalselySplit(string validWord)
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal(validWord, engine.SimulateText(validWord));
    }
}
