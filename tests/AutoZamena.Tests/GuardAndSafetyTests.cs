using System.IO;
using System.Runtime.InteropServices;
using AutoZamena.App.Config;
using AutoZamena.App.Interop;
using AutoZamena.Core.Engine;
using AutoZamena.Core.Lexicon;
using Xunit;

namespace AutoZamena.Tests;

public sealed class GuardAndSafetyTests
{
    [Fact]
    public void ChatAbbreviationsAndTechSlang_AreNeverCorrupted()
    {
        var engine = new AutoCorrectEngine();
        const string slangText =
            "спс мб кст хз пж го прив гоу бро афк вк тг лс смержи инфа скрин пруф созвон дедлайн " +
            "норм щас деплой пуллреквест коммит гитхаб телеграм дискорд катка имба " +
            "envs args chown grep diff auth pipx pls thx idk btw ";

        Assert.Equal(slangText, engine.SimulateText(slangText));

        // Meanwhile, English phrase "so far " typed in RU layout ("ыщ афк ") still switches cleanly!
        engine.ResetContext();
        Assert.Equal("so far ", engine.SimulateText("ыщ афк "));
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
    public void UrlsEmailsDomainsFilenamesFunctionCallsAndNumbers_AreNeverCorrupted()
    {
        var engine = new AutoCorrectEngine();
        const string technical =
            "https://github.com/ToshakGulina serega.ivanov@gmail.com andriod.com програма.exe привте.ru user.name AutoZamena.exe myfunc(привте) useState foo_bar win-x64 v2.4 ";

        Assert.Equal(technical, engine.SimulateText(technical));

        // Meanwhile, an actual sentence ending with a period + space fixes the typo AND capitalizes the next word.
        engine.ResetContext();
        Assert.Equal("привет. На связи ", engine.SimulateText("привте. на связи "));
    }

    [Fact]
    public void AbbreviationsBeforePeriod_DoNotTriggerFalseSentenceCapitalization()
    {
        var engine = new AutoCorrectEngine();
        const string abbrevProse =
            "и т. д. в этом случае и т. п. в 2026 г. на ул. мира около 10 тыс. человек см. выше vs. the dr. smith ";

        Assert.Equal(abbrevProse, engine.SimulateText(abbrevProse));
    }

    [Fact]
    public void PersonalDictionary_ExportAndImportRoundTripsCleanly()
    {
        var src = new UserDictionary();
        src.AddIgnoredWord("кастомслово");
        src.SetCustomReplacement("пжл", "пожалуйста");

        string tmpFile = Path.Combine(Path.GetTempPath(), $"autozamena_dict_test_{Guid.NewGuid():N}.json");
        try
        {
            AppConfig.ExportPersonalDictionary(tmpFile, src);

            var dst = new UserDictionary();
            (int addedIgn, int addedSnip) = AppConfig.ImportPersonalDictionary(tmpFile, dst);

            Assert.Equal(1, addedIgn);
            Assert.Equal(1, addedSnip);
            Assert.True(dst.IsIgnored("кастомслово"));
            Assert.True(dst.TryGetCustomReplacement("пжл", out string? repl));
            Assert.Equal("пожалуйста", repl);
        }
        finally
        {
            if (File.Exists(tmpFile))
            {
                File.Delete(tmpFile);
            }
        }
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
