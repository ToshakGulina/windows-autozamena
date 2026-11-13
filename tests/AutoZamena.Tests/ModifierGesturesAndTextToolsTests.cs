using System;
using AutoZamena.App.Config;
using AutoZamena.App.Interop;
using AutoZamena.Core.Engine;
using AutoZamena.Core.Layout;
using AutoZamena.Core.Lexicon;
using AutoZamena.Core.Models;
using Xunit;

namespace AutoZamena.Tests;

public sealed class ModifierGesturesAndTextToolsTests
{
    [Fact]
    public void ModifierGestureTracker_DetectsDoubleShiftBothShiftsSingleShiftAndDoubleCtrl_WithoutFalsePositives()
    {
        var tracker = new ModifierGestureTracker();

        // Double Shift within threshold -> ModifierGesture.DoubleShift
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, true, 1000, false, true, true, false, true));
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, false, 1080, false, true, true, false, true));
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, true, 1210, false, true, true, false, true));
        Assert.Equal(ModifierGesture.DoubleShift, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, false, 1290, false, true, true, false, true));

        // Left Shift + Right Shift simultaneously -> BothShiftsTogglePause on first release
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, true, 2000, false, true, true, true, true));
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_RSHIFT, true, 2040, false, true, true, true, true));
        Assert.Equal(ModifierGesture.BothShiftsTogglePause, tracker.ProcessModifierEvent(NativeMethods.VK_RSHIFT, false, 2110, false, true, true, true, true));
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, false, 2130, false, true, true, true, true));

        // Typing a capital letter (Shift + character) does not trigger SingleShift or DoubleShift
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, true, 3000, false, true, true, true, true));
        tracker.OnNonModifierKeyDown();
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, false, 3120, false, true, true, true, true));

        // Alt + Shift OS layout shortcut does not trigger SingleShift
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LMENU, true, 4000, true, true, true, true, true));
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, true, 4050, true, true, true, true, true));
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, false, 4110, true, true, true, true, true));
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LMENU, false, 4140, false, true, true, true, true));

        // Shift + mouse click selection does not trigger SingleShift
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, true, 5000, false, true, true, true, true));
        tracker.OnMouseClick();
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, false, 5120, false, true, true, true, true));

        // Clean Single Shift tap when enabled -> ModifierGesture.SingleShift
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_RSHIFT, true, 6000, false, true, true, true, true));
        Assert.Equal(ModifierGesture.SingleShift, tracker.ProcessModifierEvent(NativeMethods.VK_RSHIFT, false, 6085, false, true, true, true, true));

        // Double Ctrl -> ModifierGesture.DoubleCtrl, whereas Ctrl+C -> None
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LCONTROL, true, 7000, false, true, true, false, true));
        tracker.OnNonModifierKeyDown();
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LCONTROL, false, 7110, false, true, true, false, true));

        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LCONTROL, true, 8000, false, true, true, false, true));
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LCONTROL, false, 8070, false, true, true, false, true));
        Assert.Equal(ModifierGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LCONTROL, true, 8180, false, true, true, false, true));
        Assert.Equal(ModifierGesture.DoubleCtrl, tracker.ProcessModifierEvent(NativeMethods.VK_LCONTROL, false, 8250, false, true, true, false, true));
    }

    [Fact]
    public void AppConfig_DefaultsUseModifierGesturesWithoutPauseBreak_AndMigratesLegacyV1Config()
    {
        var fresh = new AppConfig();
        Assert.True(fresh.UseDoubleShiftConvert);
        Assert.True(fresh.UseBothShiftsPause);
        Assert.False(fresh.UseSingleShiftLayoutSwitch);
        Assert.True(fresh.UseDoubleCtrlConvertLine);
        Assert.True(fresh.ConvertLayoutHotkey.IsEmpty);

        const string legacyV1Json = """
        {
          "ConvertLayoutHotkey": {
            "Ctrl": false,
            "Alt": false,
            "Shift": false,
            "VirtualKey": 19
          }
        }
        """;

        AppConfig? migrated = AppConfig.ParseAndMigrateJson(legacyV1Json, out bool didMigrate);
        Assert.NotNull(migrated);
        Assert.True(didMigrate);
        Assert.Equal(2, migrated!.ConfigSchemaVersion);
        Assert.True(migrated.UseDoubleShiftConvert);
        Assert.True(migrated.UseBothShiftsPause);
        Assert.True(migrated.ConvertLayoutHotkey.IsEmpty);
    }

    [Fact]
    public void DoubleShift_CancelsAutoSwitchedWordAndLearnsException_AndCancelsMultiWordPhrase()
    {
        var engine = new AutoCorrectEngine();

        Assert.Equal("привет ", engine.SimulateText("ghbdtn "));
        CorrectionResult undo = engine.ConvertLastWordLayoutByHotkey();
        Assert.True(undo.HasChange);
        Assert.Equal("ghbdtn ", undo.ReplacementText);
        Assert.Equal(TargetLanguage.English, undo.SwitchLayoutTo);
        Assert.True(engine.UserDictionary.IsIgnored("ghbdtn"));

        engine.ResetContext();
        Assert.Equal("ghbdtn ", engine.SimulateText("ghbdtn "));

        var engine2 = new AutoCorrectEngine();
        Assert.Equal("я не знаю ", engine2.SimulateText("z yt pyf. "));
        CorrectionResult phraseUndo = engine2.ConvertLastWordLayoutByHotkey();
        Assert.True(phraseUndo.HasChange);
        Assert.Equal("z yt pyf. ", phraseUndo.ReplacementText);
        Assert.Equal(TargetLanguage.English, phraseUndo.SwitchLayoutTo);
    }

    [Theory]
    [InlineData("ghbdtn? vbh ", "привет, мир ")]
    [InlineData("ghbdtn/ да ", "привет. Да ")]
    [InlineData("rfr ltkf& хорошо ", "как дела? Хорошо ")]
    [InlineData("руддщб цщкдв ", "hello, world ")]
    [InlineData("пщщпдуюсщь ", "google.com ")]
    [InlineData("цццюнфюкг ", "www.ya.ru ")]
    [InlineData("пшергиюсщь ", "github.com ")]
    [InlineData("hellp? ", "help? ")]
    public void WrongLayoutPunctuationAndDomains_FixedCleanlyWithoutBreakingEnglishQuestionTypos(string input, string expected)
    {
        var engine = new AutoCorrectEngine();
        Assert.Equal(expected, engine.SimulateText(input));
    }

    [Fact]
    public void MouseClickMidWordEditGuard_SuppressesFragmentCorrectionUntilNextDelimiter()
    {
        var engine = new AutoCorrectEngine();

        engine.NotifyPointerOrFocusChanged(true);
        Assert.Equal("ghbdtn ", engine.SimulateText("ghbdtn "));

        Assert.Equal("привет ", engine.SimulateText("ghbdtn "));
    }

    [Fact]
    public void SmartSelectionConverter_WholeWordLayoutCaseCyclingAndTransliterationWorkCleanly()
    {
        (string convertedMixed, TargetLanguage domLang) = SmartTextConverter.ConvertSelectionLayout(
            "Ghbdtn jn GitHub https://github.com 2026!",
            LexiconDatabase.Default
        );
        Assert.Equal("Привет от GitHub https://github.com 2026!", convertedMixed);
        Assert.Equal(TargetLanguage.Russian, domLang);

        (string convertedEn, TargetLanguage domLangEn) = SmartTextConverter.ConvertSelectionLayout(
            "руддщ цщкдв",
            LexiconDatabase.Default
        );
        Assert.Equal("hello world", convertedEn);
        Assert.Equal(TargetLanguage.English, domLangEn);

        string step1 = SmartTextConverter.CycleCaseThreeWay("ПРИВЕТ МИР");
        Assert.Equal("привет мир", step1);
        string step2 = SmartTextConverter.CycleCaseThreeWay(step1);
        Assert.Equal("Привет мир", step2);
        string step3 = SmartTextConverter.CycleCaseThreeWay(step2);
        Assert.Equal("ПРИВЕТ МИР", step3);

        string translitLat = SmartTextConverter.Transliterate("Привет, Юлия и Щука!");
        Assert.Equal("Privet, Yuliya i Shchuka!", translitLat);
        string translitCyr = SmartTextConverter.Transliterate("Privet, shchuka!");
        Assert.Equal("Привет, щука!", translitCyr);
    }

    [Fact]
    public void DoubleSpacePeriod_SmartEmDash_AndDynamicCursorSnippetsWorkCleanly()
    {
        var engine = new AutoCorrectEngine
        {
            ClipboardTextProvider = () => "AutoZamena"
        };
        engine.UserDictionary.SetCustomReplacement("дт", "{{iso}}");
        engine.UserDictionary.SetCustomReplacement("буф", "[{{clipboard}}]");
        engine.UserDictionary.SetCustomReplacement("цит", "«|»");

        Assert.Equal("привет. Мир ", engine.SimulateText("привет  мир "));

        engine.ResetContext();
        Assert.Equal("это — тест ", engine.SimulateText("это -- тест "));

        engine.ResetContext();
        string todayIso = DateTime.Now.ToString("yyyy-MM-dd");
        Assert.Equal($"{todayIso} [AutoZamena] ", engine.SimulateText("дт буф "));

        engine.ResetContext();
        Assert.Equal("«привет»", engine.SimulateText("цит привет"));
    }
}
