using System;
using AutoZamena.App.Config;
using AutoZamena.App.Interop;
using AutoZamena.Core.Engine;
using AutoZamena.Core.Layout;
using AutoZamena.Core.Lexicon;
using AutoZamena.Core.Models;
using Xunit;

namespace AutoZamena.Tests;

public sealed class CarambaAndSmartFeaturesTests
{
    [Fact]
    public void CarambaGestureTracker_DetectsDoubleShiftBothShiftsSingleShiftAndDoubleCtrl_WithoutFalsePositives()
    {
        var tracker = new CarambaGestureTracker();

        // 1. Double Shift within 420ms -> CarambaGesture.DoubleShift
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, true, 1000, false, true, true, false, true));
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, false, 1080, false, true, true, false, true));
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, true, 1210, false, true, true, false, true));
        Assert.Equal(CarambaGesture.DoubleShift, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, false, 1290, false, true, true, false, true));

        // 2. Left Shift + Right Shift simultaneously -> CarambaGesture.BothShiftsTogglePause on first release, None on second
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, true, 2000, false, true, true, true, true));
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_RSHIFT, true, 2040, false, true, true, true, true));
        Assert.Equal(CarambaGesture.BothShiftsTogglePause, tracker.ProcessModifierEvent(NativeMethods.VK_RSHIFT, false, 2110, false, true, true, true, true));
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, false, 2130, false, true, true, true, true));

        // 3. Typing a capital letter (Shift + 'A') NEVER triggers SingleShift or DoubleShift!
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, true, 3000, false, true, true, true, true));
        tracker.OnNonModifierKeyDown();
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, false, 3120, false, true, true, true, true));

        // 4. Alt + Shift OS shortcut NEVER triggers SingleShift!
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LMENU, true, 4000, true, true, true, true, true));
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, true, 4050, true, true, true, true, true));
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, false, 4110, true, true, true, true, true));
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LMENU, false, 4140, false, true, true, true, true));

        // 5. Shift + Mouse Click selection NEVER triggers SingleShift!
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, true, 5000, false, true, true, true, true));
        tracker.OnMouseClick();
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LSHIFT, false, 5120, false, true, true, true, true));

        // 6. Clean Single Shift tap when enabled -> CarambaGesture.SingleShift
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_RSHIFT, true, 6000, false, true, true, true, true));
        Assert.Equal(CarambaGesture.SingleShift, tracker.ProcessModifierEvent(NativeMethods.VK_RSHIFT, false, 6085, false, true, true, true, true));

        // 7. Double Ctrl -> CarambaGesture.DoubleCtrl, whereas Ctrl+C -> None
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LCONTROL, true, 7000, false, true, true, false, true));
        tracker.OnNonModifierKeyDown();
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LCONTROL, false, 7110, false, true, true, false, true));

        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LCONTROL, true, 8000, false, true, true, false, true));
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LCONTROL, false, 8070, false, true, true, false, true));
        Assert.Equal(CarambaGesture.None, tracker.ProcessModifierEvent(NativeMethods.VK_LCONTROL, true, 8180, false, true, true, false, true));
        Assert.Equal(CarambaGesture.DoubleCtrl, tracker.ProcessModifierEvent(NativeMethods.VK_LCONTROL, false, 8250, false, true, true, false, true));
    }

    [Fact]
    public void AppConfig_DefaultsUseCarambaGesturesWithoutPauseBreak_AndMigratesLegacyV1Config()
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
    public void CarambaDoubleShift_CancelsAutoSwitchedWordAndLearnsException_AndCancelsMultiWordPhrase()
    {
        var engine = new AutoCorrectEngine();

        // 1. Single-word auto-switch ("ghbdtn " -> "привет ") cancelled via Double Shift -> reverts & learns!
        Assert.Equal("привет ", engine.SimulateText("ghbdtn "));
        CorrectionResult undo = engine.ConvertLastWordLayoutByHotkey();
        Assert.True(undo.HasChange);
        Assert.Equal("ghbdtn ", undo.ReplacementText);
        Assert.Equal(TargetLanguage.English, undo.SwitchLayoutTo);
        Assert.True(engine.UserDictionary.IsIgnored("ghbdtn"));

        // Subsequent typing of "ghbdtn " is never auto-switched again
        engine.ResetContext();
        Assert.Equal("ghbdtn ", engine.SimulateText("ghbdtn "));

        // 2. Multi-word retroactive phrase ("z yt pyf. " -> "я не знаю ") cancelled via Double Shift -> reverts entire phrase!
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
    [InlineData("пшехаиюсщь ", "github.com ")]
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

        // Simulate user clicking with the mouse into the middle of an existing word and typing a fragment
        engine.NotifyPointerOrFocusChanged(suppressCurrentFragment: true);
        Assert.Equal("ghbdtn ", engine.SimulateText("ghbdtn "));

        // Immediately on the NEXT fresh word after the space, normal wrong-layout auto-switching resumes!
        Assert.Equal("привет ", engine.SimulateText("ghbdtn "));
    }

    [Fact]
    public void SmartSelectionConverter_WholeWordLayoutCaseCyclingAndTransliterationWorkCleanly()
    {
        // Mahou / Caramba Whole-Word Layout: converts wrong-layout tokens while keeping valid URLs & CamelCase brands intact!
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

        // 3-way Case Cycling: ALL CAPS -> lower -> Sentence case -> ALL CAPS
        string step1 = SmartTextConverter.CycleCaseThreeWay("ПРИВЕТ МИР");
        Assert.Equal("привет мир", step1);
        string step2 = SmartTextConverter.CycleCaseThreeWay(step1);
        Assert.Equal("Привет мир", step2);
        string step3 = SmartTextConverter.CycleCaseThreeWay(step2);
        Assert.Equal("ПРИВЕТ МИР", step3);

        // Bidirectional RU <-> EN Transliteration
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

        // 1. Double-space period + automatic next-sentence capitalization
        Assert.Equal("привет. Мир ", engine.SimulateText("привет  мир "));

        // 2. Smart typography: space + "--" + space -> em-dash "— "
        engine.ResetContext();
        Assert.Equal("это — тест ", engine.SimulateText("это -- тест "));

        // 3. Dynamic {{iso}} and {{clipboard}} macros
        engine.ResetContext();
        string todayIso = DateTime.Now.ToString("yyyy-MM-dd");
        Assert.Equal($"{todayIso} [AutoZamena] ", engine.SimulateText("дт буф "));

        // 4. Espanso-style '|' cursor placement inside «|»: typing "цит привет" places "привет" INSIDE «»!
        engine.ResetContext();
        Assert.Equal("«привет»", engine.SimulateText("цит привет"));
    }
}
