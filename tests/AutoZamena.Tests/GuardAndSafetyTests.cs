using System.Runtime.InteropServices;
using AutoZamena.App.Interop;
using AutoZamena.Core.Engine;
using Xunit;

namespace AutoZamena.Tests;

public sealed class GuardAndSafetyTests
{
    [Fact]
    public void ChatAbbreviationsAndTechSlang_AreNeverCorrupted()
    {
        var engine = new AutoCorrectEngine();
        const string slangText =
            "спс мб кст хз пж го норм щас деплой пуллреквест коммит гитхаб телеграм дискорд катка имба ";

        Assert.Equal(slangText, engine.SimulateText(slangText));
    }

    [Fact]
    public void InflectedModernLoanwordsAndHyphenatedWords_AreNeverCorrupted()
    {
        var engine = new AutoCorrectEngine();
        const string modernProse =
            "в телеграме и дискорде кто-то по-русски всё-таки обсудил тайминги, хоткеи, автозамены, фичи и деплоя пуллреквестов на гитхабе ";

        Assert.Equal(modernProse, engine.SimulateText(modernProse));
    }

    [Fact]
    public void ExpressiveStretchedChatWords_AreNeverTurnedIntoDryWords()
    {
        var engine = new AutoCorrectEngine();
        const string expressive =
            "ммм дааа нееет блииин ооочень круто спасииибо hmmm nooo sooo ";

        Assert.Equal(expressive, engine.SimulateText(expressive));
    }

    [Fact]
    public void UrlsEmailsCodeTokensFunctionCallsAndNumbers_AreNeverCorrupted()
    {
        var engine = new AutoCorrectEngine();
        const string technical =
            "https://github.com/ToshakGulina user@example.com AutoZamena.exe myfunc(привте) useState foo_bar win-x64 v2.4 ";

        Assert.Equal(technical, engine.SimulateText(technical));
    }

    [Fact]
    public void MidSentenceCapitalizedUncommonProperName_IsProtectedFromFalsePositives()
    {
        var engine = new AutoCorrectEngine();
        const string sentence = "вчера написал Гулиной сообщение ";

        Assert.Equal(sentence, engine.SimulateText(sentence));
    }

    [Fact]
    public void CustomUserSnippets_ExpandExactTriggers()
    {
        var engine = new AutoCorrectEngine();
        engine.UserDictionary.SetCustomReplacement("тг", "Telegram");

        Assert.Equal("напиши мне в Telegram ", engine.SimulateText("напиши мне в тг "));
    }

    [Fact]
    public void Win32SendInputStruct_HasExactKernelExpectedSize()
    {
        int actualSize = Marshal.SizeOf<NativeMethods.INPUT>();
        int expectedSize = Environment.Is64BitProcess ? 40 : 28;
        Assert.Equal(expectedSize, actualSize);
    }
}
