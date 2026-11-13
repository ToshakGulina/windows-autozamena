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
    [InlineData("непонимаю ", "не понимаю ")]
    [InlineData("недумаю ", "не думаю ")]
    [InlineData("невижу ", "не вижу ")]
    [InlineData("непомню ", "не помню ")]
    [InlineData("небудет ", "не будет ")]
    [InlineData("нехочу ", "не хочу ")]
    [InlineData("немогу ", "не могу ")]
    [InlineData("ненадо ", "не надо ")]
    [InlineData("неможет ", "не может ")]
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
    [InlineData("helloworld ", "hello world ")]
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
    [InlineData("cantfind ", "can't find ")]
    [InlineData("letme ", "let me ")]
    [InlineData("sofar ", "so far ")]
    [InlineData("idontknow ", "I don't know ")]
    public void EnglishRunTogetherWords_AreSplitCleanly(string input, string expected)
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal(expected, engine.SimulateText(input));
    }

    [Fact]
    public void SplitWords_PreserveCapitalizationPunctuationAndSupportImmediateAndDelayedBackspace()
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal(
            "Привет мир! Случайно слитые слова больше не мешают. ",
            engine.SimulateText("Приветмир! случайнослитые слова больше немешают. ")
        );

        engine.ResetContext();
        Assert.Equal("приветмир", engine.SimulateText("приветмир \b"));

        // Backspacing across trailing space into a split multi-word phrase ("не знаю ") edits only the last word cleanly
        engine.ResetContext();
        Assert.Equal("не знает ", engine.SimulateText("незнаю при\b\b\b\b\bет "));
    }

    [Theory]
    [InlineData("видеозвонок ")]
    [InlineData("фотоотчет ")]
    [InlineData("фотоотчёт ")]
    [InlineData("видеофайл ")]
    [InlineData("самовывоз ")]
    [InlineData("самозанятые ")]
    [InlineData("автозамена ")]
    [InlineData("автозапуск ")]
    [InlineData("автосохранение ")]
    [InlineData("спецпредложение ")]
    [InlineData("минимаркет ")]
    [InlineData("бизнеспроцесс ")]
    [InlineData("онлайнкурс ")]
    [InlineData("интернетмагазин ")]
    [InlineData("полстраны ")]
    [InlineData("полслова ")]
    [InlineData("полдела ")]
    [InlineData("местонахождение ")]
    [InlineData("умалишенный ")]
    [InlineData("многопоточность ")]
    [InlineData("самообучение ")]
    [InlineData("перезапустить ")]
    [InlineData("перезайти ")]
    [InlineData("переподключить ")]
    [InlineData("докинуть ")]
    [InlineData("пофиксить ")]
    [InlineData("подключить ")]
    [InlineData("мержить ")]
    [InlineData("смержить ")]
    [InlineData("замержить ")]
    [InlineData("сторис ")]
    [InlineData("копилот ")]
    [InlineData("небольшой ")]
    [InlineData("некастомный ")]
    [InlineData("Яне ")]
    [InlineData("codebase ")]
    [InlineData("filename ")]
    [InlineData("screenshot ")]
    [InlineData("screenshots ")]
    [InlineData("frontend ")]
    [InlineData("backend ")]
    [InlineData("dropdown ")]
    [InlineData("textarea ")]
    [InlineData("scrollbar ")]
    [InlineData("statusbar ")]
    [InlineData("tooltip ")]
    [InlineData("hotfix ")]
    [InlineData("bugfix ")]
    [InlineData("hotkeys ")]
    [InlineData("logout ")]
    [InlineData("signout ")]
    [InlineData("rollout ")]
    [InlineData("walkthrough ")]
    [InlineData("workaround ")]
    [InlineData("healthcheck ")]
    [InlineData("changelog ")]
    [InlineData("roadmap ")]
    [InlineData("livestream ")]
    [InlineData("allowlist ")]
    [InlineData("blocklist ")]
    [InlineData("ratelimit ")]
    [InlineData("pushback ")]
    [InlineData("goodnight ")]
    [InlineData("snowstorm ")]
    [InlineData("cloudwatch ")]
    [InlineData("tailwind ")]
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
