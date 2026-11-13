using AutoZamena.Core.Layout;
using AutoZamena.Core.Lexicon;
using AutoZamena.Core.Models;
using AutoZamena.Core.Scoring;

namespace AutoZamena.Core.Engine;

/// <summary>
/// Stateful real-time typing buffer and autocorrect decision engine.
/// Thread-confined (or externally synchronized) per typing stream; executes in &lt; 1 ms per word.
/// </summary>
public sealed class AutoCorrectEngine
{
    private static readonly HashSet<char> RuNonStandaloneConsonants = new()
    {
        'г', 'д', 'з', 'й', 'л', 'м', 'н', 'п', 'р', 'т', 'ф', 'х', 'ц', 'ч', 'ш', 'щ', 'ъ', 'ы', 'ь', 'э', 'ю'
    };

    private static readonly HashSet<char> EnNonStandaloneLetters = new()
    {
        'b', 'c', 'd', 'e', 'f', 'g', 'h', 'j', 'k', 'l', 'm', 'n', 'p', 'q', 'r', 's', 't', 'v', 'w', 'x', 'y', 'z'
    };

    private static readonly HashSet<string> RuValidHyphenAffixes = new(StringComparer.Ordinal)
    {
        "то", "либо", "нибудь", "таки", "ка", "де", "с",
        "по", "кое", "кой", "во", "в", "из", "под", "над",
        "пол", "полу", "экс", "вице", "мини", "макси", "микро", "макро", "мульти", "супер", "топ",
        "веб", "онлайн", "офлайн", "оффлайн", "телеграм", "айти", "санкт", "нью", "лос"
    };

    private static readonly Dictionary<string, string> ExplicitHyphenFixes = new(StringComparer.Ordinal)
    {
        ["по-руски"] = "по-русски",
        ["по-англиски"] = "по-английски",
        ["по-мойму"] = "по-моему",
        ["кто-нибуть"] = "кто-нибудь",
        ["что-нибуть"] = "что-нибудь",
        ["где-нибуть"] = "где-нибудь",
        ["куда-нибуть"] = "куда-нибудь",
        ["когда-нибуть"] = "когда-нибудь",
        ["как-нибуть"] = "как-нибудь",
        ["какой-нибуть"] = "какой-нибудь",
        ["все-тки"] = "всё-таки",
        ["всё-тки"] = "всё-таки"
    };

    private readonly LexiconDatabase _lexicon;
    private readonly UserDictionary _userDictionary;

    private readonly List<KeyStrokeEvent> _wordEvents = new(32);
    private readonly List<KeyStrokeEvent> _rawSinceWhitespace = new(32);
    private readonly List<RecentSpaceToken> _recentSpaceTokens = new(16);
    private readonly HashSet<string> _tempUndoSuppressed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingLearnAfterUndo = new(StringComparer.OrdinalIgnoreCase);

    private RuWordEntry? _prevRuWord;
    private EnWordEntry? _prevEnWord;

    private PendingBrokenStub? _prevUnfixedStub;
    private CompletedTailAddition? _lastTailAddition;
    private CompletedHotkeyToken? _lastCompletedForHotkey;
    private UndoSnapshot? _undoSnapshot;

    private bool _afterSentenceEndPunct;
    private bool _suppressAutoCapOnce;
    private bool _sawSentencePunctWithoutSpace;
    private bool _inCompoundToken;
    private bool _hasTypedAnyWordInContext;
    private bool _suppressFragmentCorrectionUntilDelimiter;
    private bool _canDoubleSpacePeriod;
    private long _lastSpaceWordTimestampMs;

    public EngineSettings Settings { get; set; }

    /// <summary>
    /// Optional provider returning current plain-text clipboard content for dynamic {{clipboard}} user snippets.
    /// </summary>
    public Func<string?>? ClipboardTextProvider { get; set; }

    public AutoCorrectEngine(
        LexiconDatabase? lexicon = null,
        UserDictionary? userDictionary = null,
        EngineSettings? settings = null)
    {
        _lexicon = lexicon ?? LexiconDatabase.Default;
        _userDictionary = userDictionary ?? new UserDictionary();
        Settings = settings ?? new EngineSettings();
    }

    public UserDictionary UserDictionary => _userDictionary;

    /// <summary>
    /// True when the engine currently tracks a just-typed word/token on screen that can be converted
    /// immediately without inspecting clipboard selection.
    /// </summary>
    public bool HasLiveConvertibleToken => _rawSinceWhitespace.Count > 0 || _lastCompletedForHotkey.HasValue;

    /// <summary>
    /// Called when the user clicks the mouse or jumps the caret into existing text.
    /// Clears stale word state and suppresses auto-correction on a partial mid-word edit until the next delimiter.
    /// </summary>
    public void NotifyPointerOrFocusChanged(bool suppressFragmentUntilDelimiter = true)
    {
        ResetContext();
        _suppressFragmentCorrectionUntilDelimiter = suppressFragmentUntilDelimiter;
    }

    private readonly record struct PendingBrokenStub(
        string RawToken,
        string CommittedOnScreen,
        CasingKind Casing,
        char DelimiterUsed,
        long TimestampMs
    );

    private readonly record struct CompletedTailAddition(
        string OriginalRaw,
        string CompletedWord,
        char AddedTailChar,
        long TimestampMs
    );

    private readonly record struct CompletedHotkeyToken(
        string OnScreenWord,
        string TrailingDelimiter
    );

    private readonly record struct RecentSpaceToken(
        string RawTyped,
        string CurrentOnScreen,
        bool WasAutoLayoutSwitched,
        bool StartedAtSentenceCap
    );

    private readonly record struct UndoSnapshot(
        CorrectionKind Kind,
        string OriginalTokenForLearn,
        string OriginalOnScreenBeforeDelimiter,
        string CurrentOnScreenWithDelimiter,
        TargetLanguage RevertLayoutTo,
        RuWordEntry? PreviousRuBeforeCorrection,
        EnWordEntry? PreviousEnBeforeCorrection,
        bool LearnIgnoreOnConfirm,
        bool RestoreSentenceCapSuppression
    );

    public void ResetContext()
    {
        _wordEvents.Clear();
        _rawSinceWhitespace.Clear();
        _recentSpaceTokens.Clear();
        _prevRuWord = null;
        _prevEnWord = null;
        _prevUnfixedStub = null;
        _lastTailAddition = null;
        _lastCompletedForHotkey = null;
        _undoSnapshot = null;
        _afterSentenceEndPunct = false;
        _suppressAutoCapOnce = false;
        _sawSentencePunctWithoutSpace = false;
        _inCompoundToken = false;
        _hasTypedAnyWordInContext = false;
        _suppressFragmentCorrectionUntilDelimiter = false;
        _canDoubleSpacePeriod = false;
        _lastSpaceWordTimestampMs = 0;
    }

    /// <summary>
    /// Feeds a character typed by the user.
    /// Returns a <see cref="CorrectionResult"/> only when a delimiter triggers an immediate fix;
    /// otherwise returns <see cref="CorrectionResult.NoChange"/>.
    /// </summary>
    public CorrectionResult OnCharacter(KeyStrokeEvent ev)
    {
        char c = ev.Character;

        if (c == ' ' || c == '\t')
        {
            return OnWhitespace(c, ev.TimestampMs);
        }

        _undoSnapshot = null;
        _canDoubleSpacePeriod = false;

        if (c == '\r' || c == '\n')
        {
            CorrectionResult res = EvaluateCurrentBufferOnDelimiter(c, ev.TimestampMs, allowWrongLayoutRaw: true);
            ResetContext();
            return res;
        }

        // Opening bracket/typographic quote at the very start of a token: "(привте)", "«привте»", "“hello”"
        // Note: ASCII '"' and '\'' and '[' stay in _rawSinceWhitespace because on US QWERTY they map to 'Э'/'э'/'х'.
        if (_rawSinceWhitespace.Count == 0 && c is '(' or '«' or '„' or '“' or '—' or '–')
        {
            _sawSentencePunctWithoutSpace = false;
            _recentSpaceTokens.Clear();
            return CorrectionResult.NoChange;
        }

        // Closing bracket / typographic quote OR closing ASCII '"' after a Cyrillic/Latin word: "(привте)", "\"привте\""
        if (IsClosingBracketOrQuoteDelimiter(c))
        {
            CorrectionResult closeRes = EvaluateCurrentBufferOnDelimiter(c, ev.TimestampMs, allowWrongLayoutRaw: true);
            _wordEvents.Clear();
            _rawSinceWhitespace.Clear();
            _recentSpaceTokens.Clear();
            _prevUnfixedStub = null;
            _lastTailAddition = null;
            _inCompoundToken = false;
            return closeRes;
        }

        // Defer '.' and ':' inside _rawSinceWhitespace so domains, filenames, emails, URLs & code
        // (andriod.com, serega.ivanov@gmail.com, програма.exe, https://...) are never prematurely altered on '.'.
        if (c is '.' or ':')
        {
            _sawSentencePunctWithoutSpace = false;
            _rawSinceWhitespace.Add(ev);
            return CorrectionResult.NoChange;
        }

        // Immediate clause/sentence delimiters: ',', '!', '?', ';'
        if (IsImmediatePunctuationDelimiter(c))
        {
            // Could ',' or ';' be a Russian letter typed on an EN keyboard (e.g. 'k.,k.' = 'люблю' or ';bpym' = 'жизнь')?
            if (ShouldDeferEnPunctuationForLayout(c))
            {
                _rawSinceWhitespace.Add(ev);
                _wordEvents.Clear();
                return CorrectionResult.NoChange;
            }

            CorrectionResult punctRes = EvaluateCurrentBufferOnDelimiter(c, ev.TimestampMs, allowWrongLayoutRaw: false);

            _wordEvents.Clear();
            _rawSinceWhitespace.Clear();
            _recentSpaceTokens.Clear();
            _prevUnfixedStub = null;
            _lastTailAddition = null;

            if (c is '!' or '?')
            {
                _sawSentencePunctWithoutSpace = true;
                _prevRuWord = null;
                _prevEnWord = null;
            }
            else
            {
                _sawSentencePunctWithoutSpace = false;
            }

            return punctRes;
        }

        _sawSentencePunctWithoutSpace = false;
        _rawSinceWhitespace.Add(ev);

        // If a previous char in this token was a deferred '/', '&', or '?' ("path/sub", "a&b"), mark compound.
        if (HasInternalLayoutPunctBeforeLast(_rawSinceWhitespace))
        {
            _wordEvents.Clear();
            _inCompoundToken = true;
            return CorrectionResult.NoChange;
        }

        if (char.IsLetterOrDigit(c) || c == '-')
        {
            // Only append to _wordEvents if we haven't already seen an internal '.' or ':' in this token.
            // If we HAVE seen '.' or ':' before this letter/digit (e.g. "andriod.c" or "програма.e"),
            // clear _wordEvents so only TryAutoFixWrongLayout ("k.lb" -> "люди") can inspect rawSpan.
            if (HasInternalDotOrColonBeforeLast(_rawSinceWhitespace))
            {
                _wordEvents.Clear();
            }
            else
            {
                _wordEvents.Add(ev);
            }
        }
        else
        {
            // Allow trailing '/' (RU '.') or '&' (RU '?') at the end of an EN-typed Russian word ("ghbdtn/" -> "привет.", "ltkf&" -> "дела?")
            bool isLeadingDoubleQuote = _rawSinceWhitespace.Count == 1 && c == '"';
            bool isDeferredTrailingRuPunct = c is '/' or '&' && ShouldDeferEnTrailingSlashOrAmpersand();

            if (!isLeadingDoubleQuote && !KeyboardGeometry.IsEnKeyForRuLetter(c) && !isDeferredTrailingRuPunct)
            {
                _wordEvents.Clear();
                _inCompoundToken = true;
            }
            else if (!isLeadingDoubleQuote && !isDeferredTrailingRuPunct)
            {
                _wordEvents.Clear();
            }
        }

        return CorrectionResult.NoChange;
    }

    /// <summary>
    /// Handles Space or Tab delimiter.
    /// </summary>
    public CorrectionResult OnWhitespace(char whitespaceChar = ' ', long timestampMs = 0)
    {
        // Double-space -> ". " with next-word capitalization
        if (whitespaceChar == ' ' &&
            _rawSinceWhitespace.Count == 0 &&
            _canDoubleSpacePeriod &&
            Settings.Enabled &&
            Settings.DoubleSpacePeriod)
        {
            bool rapidEnough = timestampMs <= 0 ||
                               _lastSpaceWordTimestampMs <= 0 ||
                               (timestampMs - _lastSpaceWordTimestampMs is >= 8 and <= 380);
            _canDoubleSpacePeriod = false;

            if (rapidEnough)
            {
                RuWordEntry? savedRu = _prevRuWord;
                EnWordEntry? savedEn = _prevEnWord;

                _afterSentenceEndPunct = true;
                _sawSentencePunctWithoutSpace = false;
                _inCompoundToken = false;
                _prevUnfixedStub = null;
                _lastTailAddition = null;
                _lastCompletedForHotkey = null;
                _recentSpaceTokens.Clear();
                _prevRuWord = null;
                _prevEnWord = null;

                _undoSnapshot = new UndoSnapshot(
                    CorrectionKind.SmartPunctuation,
                    string.Empty,
                    "  ",
                    ". ",
                    TargetLanguage.None,
                    savedRu,
                    savedEn,
                    LearnIgnoreOnConfirm: false,
                    RestoreSentenceCapSuppression: true
                );

                return new CorrectionResult(
                    CorrectionKind.SmartPunctuation,
                    "  ",
                    ". ",
                    BackspacesCount: 1,
                    AppendText: ".",
                    TrailingDelimiter: ' ',
                    ConfidenceScore: 0.99,
                    Reason: "Точка по двойному пробелу"
                );
            }
        }

        _undoSnapshot = null;
        _canDoubleSpacePeriod = false;

        if (_sawSentencePunctWithoutSpace && _rawSinceWhitespace.Count == 0)
        {
            _afterSentenceEndPunct = true;
            _sawSentencePunctWithoutSpace = false;
            _inCompoundToken = false;
            _recentSpaceTokens.Clear();
            return CorrectionResult.NoChange;
        }

        // Handle standalone '.' or '...' after space ("ок . на" or "ок ... на")
        if (_rawSinceWhitespace.Count > 0 && IsAllPeriods(_rawSinceWhitespace))
        {
            _afterSentenceEndPunct = true;
            _sawSentencePunctWithoutSpace = false;
            _inCompoundToken = false;
            _wordEvents.Clear();
            _rawSinceWhitespace.Clear();
            _recentSpaceTokens.Clear();
            return CorrectionResult.NoChange;
        }

        _sawSentencePunctWithoutSpace = false;
        bool hadEligibleWordBeforeSpace =
            whitespaceChar == ' ' &&
            !_inCompoundToken &&
            _rawSinceWhitespace.Count > 0 &&
            char.IsLetterOrDigit(_rawSinceWhitespace[^1].Character);

        CorrectionResult res = EvaluateCurrentBufferOnDelimiter(
            whitespaceChar,
            timestampMs,
            allowWrongLayoutRaw: whitespaceChar == ' '
        );

        if (whitespaceChar != ' ')
        {
            _recentSpaceTokens.Clear();
        }
        else if (hadEligibleWordBeforeSpace && !_afterSentenceEndPunct)
        {
            _canDoubleSpacePeriod = true;
            _lastSpaceWordTimestampMs = timestampMs;
        }

        _inCompoundToken = false;
        _wordEvents.Clear();
        _rawSinceWhitespace.Clear();
        return res;
    }

    /// <summary>
    /// Handles physical Backspace key. If the immediately preceding keystroke triggered an auto-correction,
    /// returns an <see cref="CorrectionKind.UndoRestore"/> diff restoring the user's exact typed characters.
    /// </summary>
    public CorrectionResult OnBackspace()
    {
        _canDoubleSpacePeriod = false;

        if (_undoSnapshot.HasValue && Settings.Enabled && Settings.UndoOnImmediateBackspace)
        {
            UndoSnapshot snap = _undoSnapshot.Value;
            _undoSnapshot = null;

            if (snap.LearnIgnoreOnConfirm && !string.IsNullOrEmpty(snap.OriginalTokenForLearn))
            {
                _tempUndoSuppressed.Add(snap.OriginalTokenForLearn);
                if (Settings.AutoLearnOnUndo)
                {
                    _pendingLearnAfterUndo.Add(snap.OriginalTokenForLearn);
                }
            }

            if (snap.RestoreSentenceCapSuppression)
            {
                _suppressAutoCapOnce = true;
            }

            _prevRuWord = snap.PreviousRuBeforeCorrection;
            _prevEnWord = snap.PreviousEnBeforeCorrection;
            _prevUnfixedStub = null;
            _lastTailAddition = null;
            _lastCompletedForHotkey = null;
            _recentSpaceTokens.Clear();

            // Rebuild buffer for the last token of the restored text so user can press Space or edit further
            _wordEvents.Clear();
            _rawSinceWhitespace.Clear();
            int lastSpace = snap.OriginalOnScreenBeforeDelimiter.LastIndexOf(' ');
            ReadOnlySpan<char> tailToken = lastSpace >= 0
                ? snap.OriginalOnScreenBeforeDelimiter.AsSpan(lastSpace + 1)
                : snap.OriginalOnScreenBeforeDelimiter.AsSpan();

            foreach (char ch in tailToken)
            {
                var kev = new KeyStrokeEvent(ch);
                _rawSinceWhitespace.Add(kev);
                if (char.IsLetterOrDigit(ch) || ch == '-')
                {
                    _wordEvents.Add(kev);
                }
            }

            return CorrectionResult.FromDiff(
                CorrectionKind.UndoRestore,
                snap.CurrentOnScreenWithDelimiter,
                snap.CurrentOnScreenWithDelimiter,
                snap.OriginalOnScreenBeforeDelimiter,
                trailingDelimiter: null,
                switchLayoutTo: snap.RevertLayoutTo,
                confidenceScore: 1.0,
                reason: "Откат автозамены по Backspace"
            );
        }

        _undoSnapshot = null;
        _prevUnfixedStub = null;
        _lastTailAddition = null;
        _lastCompletedForHotkey = null;
        _recentSpaceTokens.Clear();

        if (_rawSinceWhitespace.Count > 0)
        {
            char removed = _rawSinceWhitespace[^1].Character;
            _rawSinceWhitespace.RemoveAt(_rawSinceWhitespace.Count - 1);
            if (_wordEvents.Count > 0 && _wordEvents[^1].Character == removed)
            {
                _wordEvents.RemoveAt(_wordEvents.Count - 1);
            }
            return CorrectionResult.NoChange;
        }

        // Backspacing past whitespace into an earlier word we no longer own -> reset cleanly
        ResetContext();
        return CorrectionResult.NoChange;
    }

    /// <summary>
    /// Converts the layout (RU &lt;-&gt; EN) of the currently buffered word or the just-finished word,
    /// or reverts a just-applied automatic layout switch and records the word in the ignore list.
    /// </summary>
    public CorrectionResult ConvertLastWordLayoutByHotkey()
    {
        _canDoubleSpacePeriod = false;

        // If an automatic WrongLayout switch fired on the preceding delimiter, revert it and learn the word
        if (_undoSnapshot.HasValue && _undoSnapshot.Value.Kind == CorrectionKind.WrongLayout)
        {
            UndoSnapshot snap = _undoSnapshot.Value;
            _undoSnapshot = null;

            if (snap.LearnIgnoreOnConfirm && !string.IsNullOrEmpty(snap.OriginalTokenForLearn))
            {
                _tempUndoSuppressed.Add(snap.OriginalTokenForLearn);
                if (Settings.AutoLearnOnUndo && !snap.OriginalTokenForLearn.Contains(' '))
                {
                    _userDictionary.AddIgnoredWord(snap.OriginalTokenForLearn);
                }
            }

            char delimChar = snap.CurrentOnScreenWithDelimiter.Length > 0
                ? snap.CurrentOnScreenWithDelimiter[^1]
                : ' ';
            string delimStr = char.IsWhiteSpace(delimChar) || IsImmediatePunctuationDelimiter(delimChar)
                ? delimChar.ToString()
                : string.Empty;
            string desiredWithDelim = snap.OriginalOnScreenBeforeDelimiter + delimStr;

            int lastSp = snap.OriginalOnScreenBeforeDelimiter.LastIndexOf(' ');
            string lastRestoredWord = lastSp >= 0
                ? snap.OriginalOnScreenBeforeDelimiter[(lastSp + 1)..]
                : snap.OriginalOnScreenBeforeDelimiter;

            _lastCompletedForHotkey = new CompletedHotkeyToken(lastRestoredWord, delimStr);
            _recentSpaceTokens.Clear();
            _prevRuWord = snap.PreviousRuBeforeCorrection;
            _prevEnWord = snap.PreviousEnBeforeCorrection;
            _prevUnfixedStub = null;

            return CorrectionResult.FromDiff(
                CorrectionKind.HotkeyLayoutSwitch,
                snap.CurrentOnScreenWithDelimiter,
                snap.CurrentOnScreenWithDelimiter,
                desiredWithDelim,
                trailingDelimiter: null,
                switchLayoutTo: snap.RevertLayoutTo,
                confidenceScore: 1.0,
                reason: "Отмена автопереключения раскладки (Double Shift)"
            );
        }

        // Case 1: User is currently at the end of a typed token (before pressing Space)
        if (_rawSinceWhitespace.Count > 0)
        {
            string raw = ExtractString(_rawSinceWhitespace);
            (string converted, TargetLanguage targetLang) = ConvertTokenOppositeLayout(raw);
            if (converted != raw && targetLang != TargetLanguage.None)
            {
                RebuildSingleTokenBuffer(converted);
                UpdatePreviousLanguageContext(converted);
                return CorrectionResult.FromDiff(
                    CorrectionKind.HotkeyLayoutSwitch,
                    raw,
                    raw,
                    converted,
                    trailingDelimiter: null,
                    switchLayoutTo: targetLang,
                    confidenceScore: 1.0,
                    reason: targetLang == TargetLanguage.Russian ? "Двойной Shift: EN→RU" : "Двойной Shift: RU→EN"
                );
            }
        }

        // Case 2: User already pressed Space/delimiter after a token (including valid words like "tot " -> "еще ")
        if (_lastCompletedForHotkey.HasValue)
        {
            string currentWord = _lastCompletedForHotkey.Value.OnScreenWord;
            string delim = _lastCompletedForHotkey.Value.TrailingDelimiter;
            (string converted, TargetLanguage targetLang) = ConvertTokenOppositeLayout(currentWord);
            if (converted != currentWord && targetLang != TargetLanguage.None)
            {
                string onScreen = currentWord + delim;
                string desired = converted + delim;
                _lastCompletedForHotkey = new CompletedHotkeyToken(converted, delim);
                if (_recentSpaceTokens.Count > 0 && delim == " ")
                {
                    RecentSpaceToken last = _recentSpaceTokens[^1];
                    _recentSpaceTokens[^1] = last with { CurrentOnScreen = converted, WasAutoLayoutSwitched = true };
                }
                _prevUnfixedStub = null;
                UpdatePreviousLanguageContext(converted);

                return CorrectionResult.FromDiff(
                    CorrectionKind.HotkeyLayoutSwitch,
                    currentWord,
                    onScreen,
                    desired,
                    trailingDelimiter: null,
                    switchLayoutTo: targetLang,
                    confidenceScore: 1.0,
                    reason: targetLang == TargetLanguage.Russian ? "Двойной Shift: EN→RU" : "Двойной Shift: RU→EN"
                );
            }
        }

        return CorrectionResult.NoChange;
    }

    /// <summary>
    /// Converts the uninterrupted sequence of space-separated words typed on the current line
    /// without touching the system clipboard.
    /// </summary>
    public CorrectionResult ConvertPhraseLayoutByHotkey()
    {
        if (_recentSpaceTokens.Count == 0)
        {
            return ConvertLastWordLayoutByHotkey();
        }

        string currentTail = _rawSinceWhitespace.Count > 0 ? ExtractString(_rawSinceWhitespace) : string.Empty;

        var onScreenBuilder = new System.Text.StringBuilder(64);
        for (int i = 0; i < _recentSpaceTokens.Count; i++)
        {
            onScreenBuilder.Append(_recentSpaceTokens[i].CurrentOnScreen).Append(' ');
        }
        onScreenBuilder.Append(currentTail);

        string fullOnScreen = onScreenBuilder.ToString();
        (string convertedFull, TargetLanguage targetLang) = ConvertPhraseOppositeLayout(fullOnScreen);
        if (convertedFull == fullOnScreen || targetLang == TargetLanguage.None)
        {
            return CorrectionResult.NoChange;
        }

        // Update _recentSpaceTokens so repeating the hotkey toggles the phrase back
        for (int i = 0; i < _recentSpaceTokens.Count; i++)
        {
            RecentSpaceToken tok = _recentSpaceTokens[i];
            string convTok = targetLang == TargetLanguage.Russian
                ? KeyboardGeometry.ConvertEnToRu(tok.CurrentOnScreen)
                : KeyboardGeometry.ConvertRuToEn(tok.CurrentOnScreen);
            _recentSpaceTokens[i] = tok with { CurrentOnScreen = convTok, WasAutoLayoutSwitched = true };
        }

        if (currentTail.Length > 0)
        {
            string convTail = targetLang == TargetLanguage.Russian
                ? KeyboardGeometry.ConvertEnToRu(currentTail)
                : KeyboardGeometry.ConvertRuToEn(currentTail);
            RebuildSingleTokenBuffer(convTail);
            UpdatePreviousLanguageContext(convTail);
        }
        else if (_recentSpaceTokens.Count > 0)
        {
            string lastWord = _recentSpaceTokens[^1].CurrentOnScreen;
            _lastCompletedForHotkey = new CompletedHotkeyToken(lastWord, " ");
            UpdatePreviousLanguageContext(lastWord);
        }

        _prevUnfixedStub = null;
        return CorrectionResult.FromDiff(
            CorrectionKind.HotkeyLayoutSwitch,
            fullOnScreen,
            fullOnScreen,
            convertedFull,
            trailingDelimiter: null,
            switchLayoutTo: targetLang,
            confidenceScore: 1.0,
            reason: targetLang == TargetLanguage.Russian ? "Хоткей фразы EN→RU" : "Хоткей фразы RU→EN"
        );
    }

    /// <summary>
    /// Convenience simulator for unit testing and interactive sandbox preview.
    /// </summary>
    public string SimulateText(string typedSequence)
    {
        var screen = new System.Text.StringBuilder(typedSequence.Length + 16);
        int cursor = 0;
        long t = 1000;

        foreach (char c in typedSequence)
        {
            t += 95;
            if (c == '\b')
            {
                CorrectionResult undo = OnBackspace();
                if (undo.HasChange)
                {
                    ApplyToBuffer(screen, ref cursor, undo);
                }
                else if (cursor > 0)
                {
                    cursor--;
                    screen.Remove(cursor, 1);
                }
                continue;
            }

            CorrectionResult res = OnCharacter(new KeyStrokeEvent(c, DeltaPrevMs: 95, OverlapWithPrev: false, TimestampMs: t));
            if (res.HasChange)
            {
                ApplyToBuffer(screen, ref cursor, res);
            }
            else
            {
                screen.Insert(cursor, c);
                cursor++;
            }
        }

        return screen.ToString();
    }

    private static void ApplyToBuffer(System.Text.StringBuilder screen, ref int cursor, CorrectionResult res)
    {
        int remove = Math.Min(cursor, res.BackspacesCount);
        if (remove > 0)
        {
            screen.Remove(cursor - remove, remove);
            cursor -= remove;
        }
        if (!string.IsNullOrEmpty(res.AppendText))
        {
            screen.Insert(cursor, res.AppendText);
            cursor += res.AppendText.Length;
        }
        if (res.TrailingDelimiter.HasValue)
        {
            screen.Insert(cursor, res.TrailingDelimiter.Value);
            cursor++;
        }
        if (res.MoveCursorLeftCount > 0)
        {
            cursor = Math.Max(0, cursor - res.MoveCursorLeftCount);
        }
    }

    private CorrectionResult EvaluateCurrentBufferOnDelimiter(
        char delimiter,
        long timestampMs,
        bool allowWrongLayoutRaw)
    {
        if (!Settings.Enabled)
        {
            _prevUnfixedStub = null;
            _lastTailAddition = null;
            _recentSpaceTokens.Clear();
            return CorrectionResult.NoChange;
        }

        string rawSpan = ExtractString(_rawSinceWhitespace);
        string wordToken = ExtractString(_wordEvents);

        if (string.IsNullOrEmpty(rawSpan))
        {
            _suppressFragmentCorrectionUntilDelimiter = false;
            return CorrectionResult.NoChange;
        }

        // Mid-word edit guard: skip auto-correction on the partial fragment typed immediately after a mouse click
        if (_suppressFragmentCorrectionUntilDelimiter)
        {
            _suppressFragmentCorrectionUntilDelimiter = false;
            _hasTypedAnyWordInContext = true;
            _prevUnfixedStub = null;
            _lastTailAddition = null;
            _recentSpaceTokens.Clear();
            return CorrectionResult.NoChange;
        }

        bool shouldCapSentence =
            !_suppressAutoCapOnce &&
            ((_afterSentenceEndPunct && Settings.AutoCapitalizeAfterSentence) ||
             (!_hasTypedAnyWordInContext && Settings.AutoCapitalizeFirstWord));
        _suppressAutoCapOnce = false;

        // Save previous context snapshot for potential Undo
        RuWordEntry? savedPrevRu = _prevRuWord;
        EnWordEntry? savedPrevEn = _prevEnWord;

        // 0. If the user previously undid a word and now confirms that exact word with a delimiter -> learn it.
        if (_pendingLearnAfterUndo.Remove(rawSpan))
        {
            _userDictionary.AddIgnoredWord(rawSpan);
        }
        else if (!string.IsNullOrEmpty(wordToken) && _pendingLearnAfterUndo.Remove(wordToken))
        {
            _userDictionary.AddIgnoredWord(wordToken);
        }

        // 1. User custom replacement / snippet (highest priority, explicit user intent; supports {{date}}, {{time}}, {{iso}}, {{clipboard}}, |)
        if (_userDictionary.TryGetCustomReplacement(rawSpan, out string? customRepl))
        {
            string expanded = SmartTextConverter.ExpandSnippetTemplate(
                customRepl,
                ClipboardTextProvider?.Invoke(),
                DateTime.Now,
                out int moveLeft
            );

            if (moveLeft > 0 && delimiter == ' ')
            {
                _afterSentenceEndPunct = false;
                _hasTypedAnyWordInContext = true;
                _prevUnfixedStub = null;
                _lastCompletedForHotkey = null;
                _recentSpaceTokens.Clear();
                _undoSnapshot = null;

                return CorrectionResult.FromDiff(
                    CorrectionKind.UserSnippet,
                    rawSpan,
                    rawSpan,
                    expanded,
                    trailingDelimiter: null,
                    switchLayoutTo: TargetLanguage.None,
                    confidenceScore: 1.0,
                    reason: "Личная автозамена (курсор внутри шаблона)",
                    turnOffCapsLock: false,
                    moveCursorLeftCount: moveLeft
                );
            }

            return CommitCorrection(
                CorrectionKind.UserSnippet,
                rawSpan,
                rawSpan,
                expanded,
                delimiter,
                TargetLanguage.None,
                savedPrevRu,
                savedPrevEn,
                1.0,
                "Личная автозамена",
                learnIgnoreOnConfirm: false,
                moveCursorLeftCount: moveLeft > 0 ? moveLeft + 1 : 0
            );
        }

        // 1b. Smart Typography: standalone "--" between spaces -> em-dash "—" (never touches "--help" or "i--")
        if (Settings.SmartTypography && rawSpan == "--" && delimiter == ' ' && _hasTypedAnyWordInContext)
        {
            return CommitCorrection(
                CorrectionKind.SmartPunctuation,
                "--",
                "--",
                "—",
                delimiter,
                TargetLanguage.None,
                savedPrevRu,
                savedPrevEn,
                0.99,
                "Типографика: длинное тире",
                learnIgnoreOnConfirm: false
            );
        }

        // Check URL / code / numbers / symbols guard on rawSpan BEFORE anything else
        // (Allow a single trailing '/' or '?' or '&' at the end of an EN-typed Russian word like "ghbdtn/" -> "привет.")
        bool hasTrailingEnRuPunct = rawSpan.Length >= 3 && rawSpan[^1] is '/' or '?' or '&';
        ReadOnlySpan<char> symbolCheckSpan = hasTrailingEnRuPunct
            ? rawSpan.AsSpan(0, rawSpan.Length - 1)
            : rawSpan.AsSpan();

        if (_inCompoundToken ||
            TokenGuards.ContainsDisqualifyingSymbolOrDigit(symbolCheckSpan) ||
            TokenGuards.LooksLikeUrlOrDomain(rawSpan))
        {
            _afterSentenceEndPunct = false;
            _hasTypedAnyWordInContext = true;
            _prevRuWord = null;
            _prevEnWord = null;
            _prevUnfixedStub = null;
            _lastTailAddition = null;
            _lastCompletedForHotkey = null;
            _recentSpaceTokens.Clear();
            return CorrectionResult.NoChange;
        }

        // 2. Wrong keyboard layout auto-detection (e.g. "ghbdtn" -> "привет", "ghbdtn?" -> "привет,", "пщщпдуюсщь" -> "google.com")
        if (Settings.LayoutMode == LayoutSwitchMode.AutoHighConfidenceAndHotkey &&
            (!TokenGuards.HasExpressiveLetterRepeat(rawSpan) || rawSpan.StartsWith("цццю", StringComparison.OrdinalIgnoreCase)) &&
            (allowWrongLayoutRaw || rawSpan == wordToken))
        {
            CorrectionResult? layoutFix = TryAutoFixWrongLayout(
                rawSpan,
                delimiter,
                shouldCapSentence,
                savedPrevRu,
                savedPrevEn
            );
            if (layoutFix is not null)
            {
                return layoutFix;
            }
        }

        // If rawSpan ended with a deferred '?' that did NOT auto-convert as EN->RU layout, honor '?' as sentence end.
        if (rawSpan[^1] == '?')
        {
            _afterSentenceEndPunct = delimiter is ' ' or '\t';
            _sawSentencePunctWithoutSpace = delimiter is not (' ' or '\t');
            _hasTypedAnyWordInContext = true;
            _prevRuWord = null;
            _prevEnWord = null;
            _prevUnfixedStub = null;
            _lastTailAddition = null;
            _recentSpaceTokens.Clear();
            return CorrectionResult.NoChange;
        }

        // Extract optional leading '"' and optional trailing deferred '.' / ':' (e.g. "\"привте\"", "наза.", "пожалуста...")
        string leadingQuote = string.Empty;
        string trailingDeferredPunct = string.Empty;
        ReadOnlySpan<char> workingSpan = rawSpan.AsSpan();

        if (workingSpan.Length >= 2 && workingSpan[0] == '"')
        {
            leadingQuote = "\"";
            workingSpan = workingSpan[1..];
        }

        int punctTailStart = workingSpan.Length;
        while (punctTailStart > 0 && workingSpan[punctTailStart - 1] is '.' or ':')
        {
            punctTailStart--;
        }

        if (punctTailStart < workingSpan.Length)
        {
            trailingDeferredPunct = workingSpan[punctTailStart..].ToString();
            workingSpan = workingSpan[..punctTailStart];
        }

        // If workingSpan doesn't match clean wordToken (e.g. internal dot "andriod.local" or "user.name"), leave 100% untouched.
        if (string.IsNullOrEmpty(wordToken) || !workingSpan.SequenceEqual(wordToken.AsSpan()))
        {
            _afterSentenceEndPunct = false;
            _hasTypedAnyWordInContext = true;
            _prevRuWord = null;
            _prevEnWord = null;
            _prevUnfixedStub = null;
            _lastTailAddition = null;
            if (trailingDeferredPunct.Length == 0 && leadingQuote.Length == 0)
            {
                _prevUnfixedStub = new PendingBrokenStub(rawSpan, rawSpan, CasingKind.Lower, delimiter, timestampMs);
                _lastCompletedForHotkey = new CompletedHotkeyToken(rawSpan, delimiter.ToString());
                if (delimiter == ' ')
                {
                    _recentSpaceTokens.Add(new RecentSpaceToken(rawSpan, rawSpan, false, shouldCapSentence));
                }
            }
            else
            {
                _recentSpaceTokens.Clear();
            }
            return CorrectionResult.NoChange;
        }

        // Check whether trailing deferred '.' marks the end of a sentence (unless preceded by an abbreviation like "т.", "д.", "г.", "ул.")
        bool endsSentenceViaDeferredDot =
            trailingDeferredPunct.EndsWith('.') &&
            (trailingDeferredPunct.Length >= 2 || !TokenGuards.IsAbbreviationBeforePeriod(wordToken));

        // Also check user snippet on unwrapped wordToken when wrapped by quote or trailing dot/colon
        if ((leadingQuote.Length > 0 || trailingDeferredPunct.Length > 0) &&
            _userDictionary.TryGetCustomReplacement(wordToken, out string? wrappedCustom))
        {
            string expandedWrapped = SmartTextConverter.ExpandSnippetTemplate(
                wrappedCustom,
                ClipboardTextProvider?.Invoke(),
                DateTime.Now,
                out _
            );
            CorrectionResult snipRes = CommitCorrection(
                CorrectionKind.UserSnippet,
                wordToken,
                leadingQuote + wordToken + trailingDeferredPunct,
                leadingQuote + expandedWrapped + trailingDeferredPunct,
                delimiter,
                TargetLanguage.None,
                savedPrevRu,
                savedPrevEn,
                1.0,
                "Личная автозамена",
                learnIgnoreOnConfirm: false
            );
            ApplyDeferredSentenceEndState(endsSentenceViaDeferredDot, trailingDeferredPunct.Length > 0, delimiter);
            return snipRes;
        }

        // 3. Spacebar Rollover checks ("приве т " -> "привет " and delayed tail "назад д " -> "назад ")
        if (leadingQuote.Length == 0 && trailingDeferredPunct.Length == 0 &&
            Settings.FixSpacebarRollover && wordToken.Length == 1 && delimiter == ' ')
        {
            char singleLower = char.ToLowerInvariant(wordToken[0]);

            // 3a. Delayed rollover key right after we already auto-completed that exact tail char
            if (_lastTailAddition.HasValue &&
                _lastTailAddition.Value.AddedTailChar == singleLower &&
                _wordEvents.Count == 1)
            {
                long firstKeyTime = _wordEvents[0].TimestampMs;
                long prevTime = _lastTailAddition.Value.TimestampMs;
                if (prevTime > 0 && firstKeyTime > 0 && (firstKeyTime - prevTime) <= 220 &&
                    (RuNonStandaloneConsonants.Contains(singleLower) || EnNonStandaloneLetters.Contains(singleLower)))
                {
                    _lastTailAddition = null;
                    _prevUnfixedStub = null;
                    return new CorrectionResult(
                        CorrectionKind.SpacebarRolloverDropLateTail,
                        wordToken,
                        string.Empty,
                        BackspacesCount: 1,
                        AppendText: string.Empty,
                        TrailingDelimiter: null,
                        ConfidenceScore: 0.98,
                        Reason: "Поздний дожим клавиши после пробела"
                    );
                }
            }

            // 3b. Merge broken stub + stray single trailing consonant ("приве т" -> "привет", "делае т" -> "делает")
            if (_prevUnfixedStub.HasValue && _prevUnfixedStub.Value.DelimiterUsed == ' ')
            {
                PendingBrokenStub stub = _prevUnfixedStub.Value;
                bool isNonStandalone = RuNonStandaloneConsonants.Contains(singleLower) ||
                                       EnNonStandaloneLetters.Contains(singleLower);

                if (isNonStandalone && stub.RawToken.Length >= 2)
                {
                    string mergedLower = (stub.RawToken + wordToken).ToLowerInvariant();
                    bool validMerged = _lexicon.TryGetRuWord(mergedLower, out RuWordEntry ruMerged) && ruMerged.Zipf >= 3.1f
                                    || _lexicon.TryGetEnWord(mergedLower, out EnWordEntry enMerged) && enMerged.Zipf >= 3.2f;

                    if (validMerged)
                    {
                        string desiredMerged = TokenGuards.ApplyCasing(mergedLower, stub.Casing, forceCapitalize: false);
                        string onScreenTwoTokens = stub.CommittedOnScreen + " " + wordToken;
                        string rawTypedTwoTokens = stub.RawToken + " " + wordToken;
                        _prevUnfixedStub = null;
                        _lastTailAddition = null;
                        _lastCompletedForHotkey = new CompletedHotkeyToken(desiredMerged, delimiter.ToString());
                        if (_recentSpaceTokens.Count > 0)
                        {
                            _recentSpaceTokens[^1] = new RecentSpaceToken(rawTypedTwoTokens, desiredMerged, false, false);
                        }
                        UpdatePreviousLanguageContext(desiredMerged);

                        var mergeRes = CorrectionResult.FromDiff(
                            CorrectionKind.SpacebarRolloverMerge,
                            rawTypedTwoTokens,
                            onScreenTwoTokens,
                            desiredMerged,
                            delimiter,
                            TargetLanguage.None,
                            0.97,
                            "Склейка сбитого пробела"
                        );

                        _undoSnapshot = new UndoSnapshot(
                            CorrectionKind.SpacebarRolloverMerge,
                            string.Empty,
                            rawTypedTwoTokens,
                            desiredMerged + delimiter,
                            TargetLanguage.None,
                            savedPrevRu,
                            savedPrevEn,
                            LearnIgnoreOnConfirm: false,
                            RestoreSentenceCapSuppression: false
                        );
                        return mergeRes;
                    }
                }
            }
        }

        _lastTailAddition = null;

        // Check suppression / user dictionary / protected words
        string lower = wordToken.ToLowerInvariant();
        CasingKind casing = TokenGuards.ClassifyCasing(wordToken);

        // Ignore ALL_CAPS (ПК, API) and MixedCamel (useState, AutoZamena)
        if (casing is CasingKind.AllCaps or CasingKind.MixedCamel ||
            (casing == CasingKind.InvertedCapsLock && !Settings.FixInvertedCaps))
        {
            RecordUntouchedWord(wordToken, rawSpan, leadingQuote, trailingDeferredPunct, endsSentenceViaDeferredDot, shouldCapSentence, delimiter);
            UpdatePreviousLanguageContext(wordToken);
            if (endsSentenceViaDeferredDot)
            {
                _prevRuWord = null;
                _prevEnWord = null;
            }
            return CorrectionResult.NoChange;
        }

        if (_tempUndoSuppressed.Contains(wordToken) || _userDictionary.IsIgnored(wordToken))
        {
            RecordUntouchedWord(wordToken, rawSpan, leadingQuote, trailingDeferredPunct, endsSentenceViaDeferredDot, shouldCapSentence, delimiter);
            UpdatePreviousLanguageContext(wordToken);
            if (endsSentenceViaDeferredDot)
            {
                _prevRuWord = null;
                _prevEnWord = null;
            }
            return CorrectionResult.NoChange;
        }

        bool invertedCaps = Settings.FixInvertedCaps && casing == CasingKind.InvertedCapsLock;

        // Expressive stretched words ("ммм", "дааа", "нееет", "ооочень", "nooo", "hmmm"): never "fix" letters.
        if (TokenGuards.HasExpressiveLetterRepeat(wordToken))
        {
            if ((invertedCaps || (shouldCapSentence && casing == CasingKind.Lower)) && leadingQuote.Length == 0)
            {
                string capExpr = TokenGuards.ApplyCasing(lower, CasingKind.Capitalized, forceCapitalize: true);
                CorrectionResult exprCapRes = CommitCorrection(
                    invertedCaps ? CorrectionKind.InvertedCaps : CorrectionKind.AutoCapitalize,
                    wordToken,
                    wordToken + trailingDeferredPunct,
                    capExpr + trailingDeferredPunct,
                    delimiter,
                    TargetLanguage.None,
                    savedPrevRu,
                    savedPrevEn,
                    0.99,
                    invertedCaps ? "Инвертированный Caps Lock" : "Заглавная буква в начале предложения",
                    learnIgnoreOnConfirm: false,
                    restoreSentenceCapSuppression: !invertedCaps,
                    turnOffCapsLock: invertedCaps
                );
                ApplyDeferredSentenceEndState(endsSentenceViaDeferredDot, trailingDeferredPunct.Length > 0, delimiter);
                return exprCapRes;
            }

            RecordUntouchedWord(wordToken, rawSpan, leadingQuote, trailingDeferredPunct, endsSentenceViaDeferredDot, shouldCapSentence, delimiter);
            return CorrectionResult.NoChange;
        }

        // Handle hyphenated words ("кто-то", "по-русски", "по-руски" -> "по-русски", "интернет-магозин" -> "интернет-магазин")
        if (TokenGuards.IsWellFormedHyphenatedWord(lower, out bool hyphRu, out bool hyphEn))
        {
            CorrectionResult hyphRes = EvaluateHyphenatedWord(
                wordToken,
                lower,
                leadingQuote,
                trailingDeferredPunct,
                casing,
                hyphRu,
                shouldCapSentence,
                endsSentenceViaDeferredDot,
                delimiter,
                savedPrevRu,
                savedPrevEn
            );
            ApplyDeferredSentenceEndState(endsSentenceViaDeferredDot, trailingDeferredPunct.Length > 0, delimiter);
            return hyphRes;
        }

        // 4. Sticky Shift Double-Caps (ПРивет -> Привет) & Inverted Caps Lock (пРИВЕТ -> Привет)
        bool stickyCaps = Settings.FixDoubleCaps && casing == CasingKind.StickyDoubleCaps;
        bool isProtected = _lexicon.IsProtectedWord(lower);
        bool isPureRu = TokenGuards.IsPureCyrillicWord(lower);
        bool isPureEn = !isPureRu && TokenGuards.IsPureLatinWord(lower);

        // 5. Explicit unambiguous fixes ("незнаю" -> "не знаю", "вобщем" -> "в общем", "чтото" -> "что-то", "dont" -> "don't")
        if (Settings.FixTypos && !isProtected && _lexicon.TryGetExplicitFix(lower, out string? explicitTarget))
        {
            string casedTarget = TokenGuards.ApplyCasing(explicitTarget, casing, shouldCapSentence && leadingQuote.Length == 0);
            CorrectionResult expRes = CommitCorrection(
                CorrectionKind.ExplicitFix,
                wordToken,
                leadingQuote + wordToken + trailingDeferredPunct,
                leadingQuote + casedTarget + trailingDeferredPunct,
                delimiter,
                TargetLanguage.None,
                savedPrevRu,
                savedPrevEn,
                0.99,
                "Частая орфографическая опечатка",
                turnOffCapsLock: invertedCaps
            );
            ApplyDeferredSentenceEndState(endsSentenceViaDeferredDot, trailingDeferredPunct.Length > 0, delimiter);
            return expRes;
        }

        // 6. Check if the typed word is ALREADY a valid dictionary word (or protected word)
        if (isPureRu && (_lexicon.TryGetRuWord(lower, out RuWordEntry knownRu) || isProtected))
        {
            // Check contextual -тся / -ться grammar slip on valid reflexive verbs (e.g. "надо учится" -> "надо учиться")
            if (Settings.FixTypos && !isProtected && _prevRuWord.HasValue)
            {
                string? reflexiveFix = TryFixValidReflexiveVerbGrammar(lower, knownRu, _prevRuWord.Value);
                if (reflexiveFix is not null)
                {
                    string casedReflexive = TokenGuards.ApplyCasing(reflexiveFix, casing, shouldCapSentence && leadingQuote.Length == 0);
                    CorrectionResult reflRes = CommitCorrection(
                        CorrectionKind.Typo,
                        wordToken,
                        leadingQuote + wordToken + trailingDeferredPunct,
                        leadingQuote + casedReflexive + trailingDeferredPunct,
                        delimiter,
                        TargetLanguage.None,
                        savedPrevRu,
                        savedPrevEn,
                        0.96,
                        "Грамматика -тся/-ться по контексту",
                        turnOffCapsLock: invertedCaps
                    );
                    ApplyDeferredSentenceEndState(endsSentenceViaDeferredDot, trailingDeferredPunct.Length > 0, delimiter);
                    return reflRes;
                }
            }

            if (invertedCaps)
            {
                string fixedInv = TokenGuards.ApplyCasing(lower, CasingKind.Capitalized, forceCapitalize: true);
                CorrectionResult invRes = CommitCorrection(
                    CorrectionKind.InvertedCaps,
                    wordToken,
                    leadingQuote + wordToken + trailingDeferredPunct,
                    leadingQuote + fixedInv + trailingDeferredPunct,
                    delimiter,
                    TargetLanguage.None,
                    savedPrevRu,
                    savedPrevEn,
                    0.99,
                    "Исправление случайного Caps Lock",
                    learnIgnoreOnConfirm: false,
                    restoreSentenceCapSuppression: false,
                    turnOffCapsLock: true
                );
                ApplyDeferredSentenceEndState(endsSentenceViaDeferredDot, trailingDeferredPunct.Length > 0, delimiter);
                return invRes;
            }

            if (stickyCaps && (wordToken.Length >= 4 || (!isProtected && knownRu.Zipf >= 5.0f)))
            {
                string fixedCaps = TokenGuards.ApplyCasing(lower, CasingKind.Capitalized, forceCapitalize: true);
                CorrectionResult dcRes = CommitCorrection(
                    CorrectionKind.DoubleCaps,
                    wordToken,
                    leadingQuote + wordToken + trailingDeferredPunct,
                    leadingQuote + fixedCaps + trailingDeferredPunct,
                    delimiter,
                    TargetLanguage.None,
                    savedPrevRu,
                    savedPrevEn,
                    0.99,
                    "Исправление залипшего Shift",
                    learnIgnoreOnConfirm: false,
                    restoreSentenceCapSuppression: false
                );
                ApplyDeferredSentenceEndState(endsSentenceViaDeferredDot, trailingDeferredPunct.Length > 0, delimiter);
                return dcRes;
            }

            if (shouldCapSentence && casing == CasingKind.Lower && leadingQuote.Length == 0)
            {
                string capWord = TokenGuards.ApplyCasing(lower, CasingKind.Capitalized, forceCapitalize: true);
                CorrectionResult capRes = CommitCorrection(
                    CorrectionKind.AutoCapitalize,
                    wordToken,
                    wordToken + trailingDeferredPunct,
                    capWord + trailingDeferredPunct,
                    delimiter,
                    TargetLanguage.None,
                    savedPrevRu,
                    savedPrevEn,
                    0.99,
                    "Заглавная буква в начале предложения",
                    learnIgnoreOnConfirm: false,
                    restoreSentenceCapSuppression: true
                );
                ApplyDeferredSentenceEndState(endsSentenceViaDeferredDot, trailingDeferredPunct.Length > 0, delimiter);
                return capRes;
            }

            RecordUntouchedWord(wordToken, rawSpan, leadingQuote, trailingDeferredPunct, endsSentenceViaDeferredDot, shouldCapSentence, delimiter);
            _prevRuWord = (!isProtected && !endsSentenceViaDeferredDot) ? knownRu : null;
            _prevEnWord = null;
            return CorrectionResult.NoChange;
        }

        if (isPureEn && (_lexicon.TryGetEnWord(lower, out EnWordEntry knownEn) || isProtected))
        {
            if (invertedCaps)
            {
                string fixedInv = TokenGuards.ApplyCasing(lower, CasingKind.Capitalized, forceCapitalize: true);
                CorrectionResult invRes = CommitCorrection(
                    CorrectionKind.InvertedCaps,
                    wordToken,
                    leadingQuote + wordToken + trailingDeferredPunct,
                    leadingQuote + fixedInv + trailingDeferredPunct,
                    delimiter,
                    TargetLanguage.None,
                    savedPrevRu,
                    savedPrevEn,
                    0.99,
                    "Исправление случайного Caps Lock",
                    learnIgnoreOnConfirm: false,
                    restoreSentenceCapSuppression: false,
                    turnOffCapsLock: true
                );
                ApplyDeferredSentenceEndState(endsSentenceViaDeferredDot, trailingDeferredPunct.Length > 0, delimiter);
                return invRes;
            }

            if (stickyCaps && (wordToken.Length >= 4 || (!isProtected && knownEn.Zipf >= 5.0f)))
            {
                string fixedCaps = TokenGuards.ApplyCasing(lower, CasingKind.Capitalized, forceCapitalize: true);
                CorrectionResult dcRes = CommitCorrection(
                    CorrectionKind.DoubleCaps,
                    wordToken,
                    leadingQuote + wordToken + trailingDeferredPunct,
                    leadingQuote + fixedCaps + trailingDeferredPunct,
                    delimiter,
                    TargetLanguage.None,
                    savedPrevRu,
                    savedPrevEn,
                    0.99,
                    "Исправление залипшего Shift",
                    learnIgnoreOnConfirm: false,
                    restoreSentenceCapSuppression: false
                );
                ApplyDeferredSentenceEndState(endsSentenceViaDeferredDot, trailingDeferredPunct.Length > 0, delimiter);
                return dcRes;
            }

            if (shouldCapSentence && casing == CasingKind.Lower && leadingQuote.Length == 0)
            {
                string capWord = TokenGuards.ApplyCasing(lower, CasingKind.Capitalized, forceCapitalize: true);
                CorrectionResult capRes = CommitCorrection(
                    CorrectionKind.AutoCapitalize,
                    wordToken,
                    wordToken + trailingDeferredPunct,
                    capWord + trailingDeferredPunct,
                    delimiter,
                    TargetLanguage.None,
                    savedPrevRu,
                    savedPrevEn,
                    0.99,
                    "Заглавная буква в начале предложения",
                    learnIgnoreOnConfirm: false,
                    restoreSentenceCapSuppression: true
                );
                ApplyDeferredSentenceEndState(endsSentenceViaDeferredDot, trailingDeferredPunct.Length > 0, delimiter);
                return capRes;
            }

            RecordUntouchedWord(wordToken, rawSpan, leadingQuote, trailingDeferredPunct, endsSentenceViaDeferredDot, shouldCapSentence, delimiter);
            _prevEnWord = (!isProtected && !endsSentenceViaDeferredDot) ? knownEn : null;
            _prevRuWord = null;
            return CorrectionResult.NoChange;
        }

        // 7. Unknown word: run Noisy-Channel Physical + Timing + Context Typo Scorer.
        if (Settings.FixTypos && wordToken.Length >= 3)
        {
            bool isMidSentenceCap = casing == CasingKind.Capitalized &&
                                    !shouldCapSentence &&
                                    _hasTypedAnyWordInContext &&
                                    leadingQuote.Length == 0;

            ReadOnlySpan<KeyStrokeEvent> eventsSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_wordEvents);

            if (isPureRu)
            {
                ScoredCandidate? bestRu = NoisyChannelScorer.FindBestRuCandidate(
                    _lexicon,
                    lower,
                    eventsSpan,
                    _prevRuWord,
                    Settings.Sensitivity,
                    isMidSentenceCap
                );

                if (bestRu.HasValue)
                {
                    string finalWord = TokenGuards.ApplyCasing(bestRu.Value.Word, casing, shouldCapSentence && leadingQuote.Length == 0);
                    string reason = bestRu.Value.ContextBoost >= 0.8f
                        ? "Опечатка + контекст соседнего слова"
                        : "Геометрия клавиш и частотность";

                    if (leadingQuote.Length == 0 && trailingDeferredPunct.Length == 0 &&
                        bestRu.Value.Word.Length == lower.Length + 1 &&
                        bestRu.Value.Word.StartsWith(lower, StringComparison.Ordinal))
                    {
                        _lastTailAddition = new CompletedTailAddition(
                            wordToken,
                            finalWord,
                            bestRu.Value.Word[^1],
                            timestampMs
                        );
                    }

                    CorrectionResult ruFix = CommitCorrection(
                        CorrectionKind.Typo,
                        wordToken,
                        leadingQuote + wordToken + trailingDeferredPunct,
                        leadingQuote + finalWord + trailingDeferredPunct,
                        delimiter,
                        TargetLanguage.None,
                        savedPrevRu,
                        savedPrevEn,
                        bestRu.Value.Score,
                        reason,
                        turnOffCapsLock: invertedCaps
                    );
                    if (leadingQuote.Length == 0 && trailingDeferredPunct.Length == 0)
                    {
                        _prevUnfixedStub = new PendingBrokenStub(wordToken, finalWord, casing, delimiter, timestampMs);
                    }
                    ApplyDeferredSentenceEndState(endsSentenceViaDeferredDot, trailingDeferredPunct.Length > 0, delimiter);
                    return ruFix;
                }
            }
            else if (isPureEn)
            {
                ScoredCandidate? bestEn = NoisyChannelScorer.FindBestEnCandidate(
                    _lexicon,
                    lower,
                    eventsSpan,
                    _prevEnWord,
                    Settings.Sensitivity,
                    isMidSentenceCap
                );

                if (bestEn.HasValue)
                {
                    string finalWord = TokenGuards.ApplyCasing(bestEn.Value.Word, casing, shouldCapSentence && leadingQuote.Length == 0);
                    string reason = bestEn.Value.ContextBoost >= 0.8f
                        ? "Typo + bigram context"
                        : "Геометрия клавиш (EN)";

                    if (leadingQuote.Length == 0 && trailingDeferredPunct.Length == 0 &&
                        bestEn.Value.Word.Length == lower.Length + 1 &&
                        bestEn.Value.Word.StartsWith(lower, StringComparison.Ordinal))
                    {
                        _lastTailAddition = new CompletedTailAddition(
                            wordToken,
                            finalWord,
                            bestEn.Value.Word[^1],
                            timestampMs
                        );
                    }

                    CorrectionResult enFix = CommitCorrection(
                        CorrectionKind.Typo,
                        wordToken,
                        leadingQuote + wordToken + trailingDeferredPunct,
                        leadingQuote + finalWord + trailingDeferredPunct,
                        delimiter,
                        TargetLanguage.None,
                        savedPrevRu,
                        savedPrevEn,
                        bestEn.Value.Score,
                        reason,
                        turnOffCapsLock: invertedCaps
                    );
                    if (leadingQuote.Length == 0 && trailingDeferredPunct.Length == 0)
                    {
                        _prevUnfixedStub = new PendingBrokenStub(wordToken, finalWord, casing, delimiter, timestampMs);
                    }
                    ApplyDeferredSentenceEndState(endsSentenceViaDeferredDot, trailingDeferredPunct.Length > 0, delimiter);
                    return enFix;
                }
            }
        }

        // Word was left untouched (e.g. unknown slang, name, or incomplete broken stub like "приве")
        RecordUntouchedWord(wordToken, rawSpan, leadingQuote, trailingDeferredPunct, endsSentenceViaDeferredDot, shouldCapSentence, delimiter);
        if (leadingQuote.Length == 0 && trailingDeferredPunct.Length == 0)
        {
            _prevUnfixedStub = new PendingBrokenStub(wordToken, wordToken, casing, delimiter, timestampMs);
        }
        _prevRuWord = null;
        _prevEnWord = null;
        return CorrectionResult.NoChange;
    }

    private void RecordUntouchedWord(
        string wordToken,
        string rawSpan,
        string leadingQuote,
        string trailingDeferredPunct,
        bool endsSentenceViaDeferredDot,
        bool shouldCapSentence,
        char delimiter)
    {
        _hasTypedAnyWordInContext = true;
        _prevUnfixedStub = null;

        if (leadingQuote.Length == 0 && trailingDeferredPunct.Length == 0)
        {
            _lastCompletedForHotkey = new CompletedHotkeyToken(wordToken, delimiter.ToString());
            if (delimiter == ' ')
            {
                _recentSpaceTokens.Add(new RecentSpaceToken(rawSpan, wordToken, false, shouldCapSentence));
                TrimRecentSpaceTokens();
            }
        }
        else
        {
            _recentSpaceTokens.Clear();
        }

        ApplyDeferredSentenceEndState(endsSentenceViaDeferredDot, trailingDeferredPunct.Length > 0, delimiter);
    }

    private void ApplyDeferredSentenceEndState(bool endsSentenceViaDeferredDot, bool hadTrailingPunct, char delimiter)
    {
        if (hadTrailingPunct)
        {
            _recentSpaceTokens.Clear();
            _prevUnfixedStub = null;
            if (endsSentenceViaDeferredDot)
            {
                _prevRuWord = null;
                _prevEnWord = null;
                if (delimiter is ' ' or '\t')
                {
                    _afterSentenceEndPunct = true;
                    _sawSentencePunctWithoutSpace = false;
                }
                else
                {
                    _afterSentenceEndPunct = false;
                    _sawSentencePunctWithoutSpace = true;
                }
                return;
            }
        }

        _afterSentenceEndPunct = false;
    }

    private CorrectionResult EvaluateHyphenatedWord(
        string wordToken,
        string lower,
        string leadingQuote,
        string trailingDeferredPunct,
        CasingKind casing,
        bool isRu,
        bool shouldCapSentence,
        bool endsSentenceViaDeferredDot,
        char delimiter,
        RuWordEntry? savedPrevRu,
        EnWordEntry? savedPrevEn)
    {
        bool invertedCaps = Settings.FixInvertedCaps && casing == CasingKind.InvertedCapsLock;

        if (Settings.FixTypos && ExplicitHyphenFixes.TryGetValue(lower, out string? explicitHyph))
        {
            string casedTarget = TokenGuards.ApplyCasing(explicitHyph, casing, shouldCapSentence && leadingQuote.Length == 0);
            return CommitCorrection(
                CorrectionKind.ExplicitFix,
                wordToken,
                leadingQuote + wordToken + trailingDeferredPunct,
                leadingQuote + casedTarget + trailingDeferredPunct,
                delimiter,
                TargetLanguage.None,
                savedPrevRu,
                savedPrevEn,
                0.99,
                "Дефисная орфография",
                turnOffCapsLock: invertedCaps
            );
        }

        string[] parts = lower.Split('-');
        bool allPartsValid = true;
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i];
            bool validPart = isRu
                ? (_lexicon.IsKnownRuWord(p) || _lexicon.IsProtectedWord(p) || RuValidHyphenAffixes.Contains(p))
                : (_lexicon.IsKnownEnWord(p) || _lexicon.IsProtectedWord(p));
            if (!validPart)
            {
                allPartsValid = false;
                break;
            }
        }

        if (allPartsValid)
        {
            if (invertedCaps)
            {
                string fixedInv = TokenGuards.ApplyCasing(lower, CasingKind.Capitalized, forceCapitalize: true);
                return CommitCorrection(
                    CorrectionKind.InvertedCaps,
                    wordToken,
                    leadingQuote + wordToken + trailingDeferredPunct,
                    leadingQuote + fixedInv + trailingDeferredPunct,
                    delimiter,
                    TargetLanguage.None,
                    savedPrevRu,
                    savedPrevEn,
                    0.99,
                    "Исправление случайного Caps Lock",
                    learnIgnoreOnConfirm: false,
                    restoreSentenceCapSuppression: false,
                    turnOffCapsLock: true
                );
            }

            if (Settings.FixDoubleCaps && casing == CasingKind.StickyDoubleCaps && wordToken.Length >= 4)
            {
                string fixedCaps = TokenGuards.ApplyCasing(lower, CasingKind.Capitalized, forceCapitalize: true);
                return CommitCorrection(
                    CorrectionKind.DoubleCaps,
                    wordToken,
                    leadingQuote + wordToken + trailingDeferredPunct,
                    leadingQuote + fixedCaps + trailingDeferredPunct,
                    delimiter,
                    TargetLanguage.None,
                    savedPrevRu,
                    savedPrevEn,
                    0.99,
                    "Исправление залипшего Shift",
                    learnIgnoreOnConfirm: false,
                    restoreSentenceCapSuppression: false
                );
            }

            if (shouldCapSentence && casing == CasingKind.Lower && leadingQuote.Length == 0)
            {
                string capWord = TokenGuards.ApplyCasing(lower, CasingKind.Capitalized, forceCapitalize: true);
                return CommitCorrection(
                    CorrectionKind.AutoCapitalize,
                    wordToken,
                    wordToken + trailingDeferredPunct,
                    capWord + trailingDeferredPunct,
                    delimiter,
                    TargetLanguage.None,
                    savedPrevRu,
                    savedPrevEn,
                    0.99,
                    "Заглавная буква в начале предложения",
                    learnIgnoreOnConfirm: false,
                    restoreSentenceCapSuppression: true
                );
            }

            RecordUntouchedWord(wordToken, wordToken, leadingQuote, trailingDeferredPunct, endsSentenceViaDeferredDot, shouldCapSentence, delimiter);
            if (!endsSentenceViaDeferredDot)
            {
                UpdatePreviousLanguageContext(parts[^1]);
            }
            return CorrectionResult.NoChange;
        }

        // Conservative single-segment typo correction for 2-part Russian hyphenated words (e.g. "интернет-магозин" -> "интернет-магазин")
        if (Settings.FixTypos && isRu && parts.Length == 2)
        {
            bool p0Valid = _lexicon.IsKnownRuWord(parts[0]) || _lexicon.IsProtectedWord(parts[0]) || RuValidHyphenAffixes.Contains(parts[0]);
            bool p1Valid = _lexicon.IsKnownRuWord(parts[1]) || _lexicon.IsProtectedWord(parts[1]) || RuValidHyphenAffixes.Contains(parts[1]);

            if (p0Valid && !p1Valid && parts[1].Length >= 5)
            {
                ScoredCandidate? cand1 = NoisyChannelScorer.FindBestRuCandidate(
                    _lexicon,
                    parts[1],
                    ReadOnlySpan<KeyStrokeEvent>.Empty,
                    null,
                    Settings.Sensitivity,
                    isMidSentenceCapitalized: false
                );
                if (cand1.HasValue && cand1.Value.EditCost <= 1.05f && cand1.Value.Score >= 1.70f)
                {
                    string rebuilt = parts[0] + "-" + cand1.Value.Word;
                    string casedRebuilt = TokenGuards.ApplyCasing(rebuilt, casing, shouldCapSentence && leadingQuote.Length == 0);
                    return CommitCorrection(
                        CorrectionKind.Typo,
                        wordToken,
                        leadingQuote + wordToken + trailingDeferredPunct,
                        leadingQuote + casedRebuilt + trailingDeferredPunct,
                        delimiter,
                        TargetLanguage.None,
                        savedPrevRu,
                        savedPrevEn,
                        cand1.Value.Score,
                        "Опечатка в дефисном слове",
                        turnOffCapsLock: invertedCaps
                    );
                }
            }
            else if (!p0Valid && p1Valid && parts[0].Length >= 5)
            {
                ScoredCandidate? cand0 = NoisyChannelScorer.FindBestRuCandidate(
                    _lexicon,
                    parts[0],
                    ReadOnlySpan<KeyStrokeEvent>.Empty,
                    null,
                    Settings.Sensitivity,
                    isMidSentenceCapitalized: false
                );
                if (cand0.HasValue && cand0.Value.EditCost <= 1.05f && cand0.Value.Score >= 1.70f)
                {
                    string rebuilt = cand0.Value.Word + "-" + parts[1];
                    string casedRebuilt = TokenGuards.ApplyCasing(rebuilt, casing, shouldCapSentence && leadingQuote.Length == 0);
                    return CommitCorrection(
                        CorrectionKind.Typo,
                        wordToken,
                        leadingQuote + wordToken + trailingDeferredPunct,
                        leadingQuote + casedRebuilt + trailingDeferredPunct,
                        delimiter,
                        TargetLanguage.None,
                        savedPrevRu,
                        savedPrevEn,
                        cand0.Value.Score,
                        "Опечатка в дефисном слове",
                        turnOffCapsLock: invertedCaps
                    );
                }
            }
        }

        RecordUntouchedWord(wordToken, wordToken, leadingQuote, trailingDeferredPunct, endsSentenceViaDeferredDot, shouldCapSentence, delimiter);
        _prevRuWord = null;
        _prevEnWord = null;
        return CorrectionResult.NoChange;
    }

    private CorrectionResult? TryAutoFixWrongLayout(
        string rawSpan,
        char delimiter,
        bool shouldCapSentence,
        RuWordEntry? savedPrevRu,
        EnWordEntry? savedPrevEn)
    {
        if (_tempUndoSuppressed.Contains(rawSpan) || _userDictionary.IsIgnored(rawSpan))
        {
            return null;
        }

        // Peel optional trailing EN-key Russian punctuation mark:
        // '?' -> ',' ("ghbdtn?" -> "привет,"), '/' -> '.' ("ghbdtn/" -> "привет."), '&' -> '?' ("ltkf&" -> "дела?")
        char? enMappedTrailPunct = rawSpan.Length >= 4
            ? rawSpan[^1] switch
            {
                '?' => ',',
                '/' => '.',
                '&' => '?',
                _ => null
            }
            : null;

        string coreEnSpan = enMappedTrailPunct.HasValue ? rawSpan[..^1] : rawSpan;
        bool hasNonLetterRuKey = ContainsNonLetterEnKeyForRu(coreEnSpan);

        if (coreEnSpan.Length >= 3 && !(coreEnSpan.Length == 3 && !hasNonLetterRuKey && savedPrevRu is null))
        {
            string coreEnLower = coreEnSpan.ToLowerInvariant();
            if (!_lexicon.IsProtectedWord(coreEnLower) && !_userDictionary.IsIgnored(coreEnSpan))
            {
                bool allEnKeysForRu = true;
                for (int i = 0; i < coreEnSpan.Length; i++)
                {
                    if (!KeyboardGeometry.IsEnKeyForRuLetter(coreEnSpan[i]))
                    {
                        allEnKeysForRu = false;
                        break;
                    }
                }

                if (allEnKeysForRu && !_lexicon.IsKnownEnWord(coreEnLower))
                {
                    bool hasAnyCloseEn = false;
                    if (TokenGuards.IsPureLatinWord(coreEnLower) && savedPrevRu is null)
                    {
                        ScoredCandidate? closeEn = NoisyChannelScorer.FindBestEnCandidate(
                            _lexicon,
                            coreEnLower,
                            ReadOnlySpan<KeyStrokeEvent>.Empty,
                            _prevEnWord,
                            SensitivityLevel.Balanced,
                            isMidSentenceCapitalized: false
                        );
                        if (closeEn.HasValue && closeEn.Value.EditCost <= 0.95f && closeEn.Value.Score >= 2.10f)
                        {
                            hasAnyCloseEn = true;
                        }
                    }

                    if (!hasAnyCloseEn)
                    {
                        string mappedRu = KeyboardGeometry.ConvertEnToRu(coreEnSpan);
                        string mappedRuLower = mappedRu.ToLowerInvariant();
                        CasingKind mappedCasing = TokenGuards.ClassifyCasing(mappedRu);
                        bool validCasing = mappedCasing is not CasingKind.AllCaps and not CasingKind.MixedCamel;
                        bool turnOffCaps = Settings.FixInvertedCaps && mappedCasing == CasingKind.InvertedCapsLock;

                        string? resolvedRuLower = null;
                        double resolvedConfidence = 4.5;
                        string reason = "Забытая раскладка EN→RU";

                        float minRuZipf = coreEnSpan.Length == 3 ? (savedPrevRu is not null ? 4.20f : 5.0f) : 3.10f;
                        if (validCasing &&
                            ((_lexicon.TryGetRuWord(mappedRuLower, out RuWordEntry ruTarget) && ruTarget.Zipf >= minRuZipf) ||
                             (coreEnSpan.Length >= 4 && TokenGuards.IsPureCyrillicWord(mappedRuLower) && _lexicon.IsProtectedWord(mappedRuLower))))
                        {
                            resolvedRuLower = mappedRuLower;
                            resolvedConfidence = ruTarget.Zipf > 0 ? ruTarget.Zipf : 4.5;
                        }
                        else if (validCasing && Settings.FixTypos && coreEnSpan.Length >= 4 &&
                                 _lexicon.TryGetExplicitFix(mappedRuLower, out string? explicitRu))
                        {
                            resolvedRuLower = explicitRu;
                            resolvedConfidence = 4.8;
                            reason = "Забытая раскладка EN→RU + опечатка";
                        }
                        else if (validCasing && Settings.FixTypos && coreEnSpan.Length >= 5 &&
                                 TokenGuards.IsPureCyrillicWord(mappedRuLower))
                        {
                            int strokeTake = enMappedTrailPunct.HasValue ? _rawSinceWhitespace.Count - 1 : _rawSinceWhitespace.Count;
                            KeyStrokeEvent[] mappedEvents = MapStrokesToRu(_rawSinceWhitespace, strokeTake);
                            ScoredCandidate? ruTypoCand = NoisyChannelScorer.FindBestRuCandidate(
                                _lexicon,
                                mappedRuLower,
                                mappedEvents,
                                _prevRuWord,
                                Settings.Sensitivity,
                                isMidSentenceCapitalized: false
                            );
                            if (ruTypoCand.HasValue && ruTypoCand.Value.EditCost <= 0.95f && ruTypoCand.Value.Score >= 1.15f)
                            {
                                resolvedRuLower = ruTypoCand.Value.Word;
                                resolvedConfidence = ruTypoCand.Value.Score;
                                reason = "Забытая раскладка EN→RU + опечатка";
                            }
                        }

                        if (resolvedRuLower is not null)
                        {
                            return CommitEnToRuLayoutWithRetroactiveChain(
                                rawSpan,
                                resolvedRuLower,
                                enMappedTrailPunct,
                                mappedCasing,
                                shouldCapSentence,
                                delimiter,
                                savedPrevRu,
                                savedPrevEn,
                                resolvedConfidence,
                                reason,
                                turnOffCaps
                            );
                        }
                    }
                }
            }
        }

        string rawLower = rawSpan.ToLowerInvariant();
        if (_lexicon.IsProtectedWord(rawLower))
        {
            return null;
        }

        // Domain/URL typed in RU layout ("пщщпдуюсщь" -> "google.com", "цццюнфюкг" -> "www.ya.ru")
        if (rawSpan.Length >= 5 && !_lexicon.IsKnownRuWord(rawLower) &&
            SmartTextConverter.TryConvertRuTypedDomainToEn(rawSpan, out string domainEn))
        {
            TargetLanguage switchTo = Settings.SwitchSystemLayoutOnAutoFix
                ? TargetLanguage.English
                : TargetLanguage.None;

            CorrectionResult domRes = CommitCorrection(
                CorrectionKind.WrongLayout,
                rawSpan,
                rawSpan,
                domainEn,
                delimiter,
                switchTo,
                savedPrevRu,
                savedPrevEn,
                4.9,
                "Забытая раскладка адреса RU→EN"
            );
            _recentSpaceTokens.Clear();
            return domRes;
        }

        // Direction 2b: Typed in RU layout -> intended EN word ("руддщ" -> "hello", "руддщб" -> "hello,", "руддщю" -> "hello.")
        if (rawSpan.Length >= 4 && TokenGuards.IsPureCyrillicWord(rawSpan) && !_lexicon.IsKnownRuWord(rawLower))
        {
            ScoredCandidate? closeRu = NoisyChannelScorer.FindBestRuCandidate(
                _lexicon,
                rawLower,
                ReadOnlySpan<KeyStrokeEvent>.Empty,
                _prevRuWord,
                SensitivityLevel.Balanced,
                isMidSentenceCapitalized: false
            );
            if (!closeRu.HasValue || closeRu.Value.EditCost > 0.90f || closeRu.Value.Score < 2.20f)
            {
                string? resolvedEnWord = TryResolveRuTypedEnglishWord(
                    rawSpan,
                    shouldCapSentence,
                    out char? ruMappedEnTrailPunct,
                    out bool turnOffCaps
                );

                if (resolvedEnWord is not null)
                {
                    string desiredEn = ruMappedEnTrailPunct.HasValue
                        ? resolvedEnWord + ruMappedEnTrailPunct.Value
                        : resolvedEnWord;

                    TargetLanguage switchTo = Settings.SwitchSystemLayoutOnAutoFix
                        ? TargetLanguage.English
                        : TargetLanguage.None;

                    CorrectionResult enRes = CommitCorrection(
                        CorrectionKind.WrongLayout,
                        rawSpan,
                        rawSpan,
                        desiredEn,
                        delimiter,
                        switchTo,
                        savedPrevRu,
                        savedPrevEn,
                        4.5,
                        "Забытая раскладка RU→EN",
                        turnOffCapsLock: turnOffCaps
                    );

                    if (ruMappedEnTrailPunct.HasValue)
                    {
                        _recentSpaceTokens.Clear();
                        if (ruMappedEnTrailPunct.Value == '.' && !TokenGuards.IsAbbreviationBeforePeriod(resolvedEnWord))
                        {
                            _prevRuWord = null;
                            _prevEnWord = null;
                            _afterSentenceEndPunct = delimiter is ' ' or '\t';
                            _sawSentencePunctWithoutSpace = delimiter is not (' ' or '\t');
                        }
                    }

                    return enRes;
                }
            }
        }

        return null;
    }

    private string? TryResolveRuTypedEnglishWord(
        string rawSpan,
        bool shouldCapSentence,
        out char? trailingPunct,
        out bool turnOffCaps)
    {
        trailingPunct = null;
        turnOffCaps = false;

        string? direct = MatchMappedEnCandidate(rawSpan, shouldCapSentence, out turnOffCaps);
        if (direct is not null)
        {
            return direct;
        }

        // Check trailing 'б' (physical ',') or 'ю' (physical '.') attached to an EN word typed in RU layout ("руддщб" -> "hello,")
        if (rawSpan.Length >= 5)
        {
            char lastLower = char.ToLowerInvariant(rawSpan[^1]);
            if (lastLower is 'б' or 'ю')
            {
                string? coreMatched = MatchMappedEnCandidate(rawSpan[..^1], shouldCapSentence, out turnOffCaps);
                if (coreMatched is not null)
                {
                    trailingPunct = lastLower == 'б' ? ',' : '.';
                    return coreMatched;
                }
            }
        }

        return null;
    }

    private string? MatchMappedEnCandidate(string cyrSpan, bool shouldCapSentence, out bool turnOffCaps)
    {
        string mappedEn = KeyboardGeometry.ConvertRuToEn(cyrSpan);
        string mappedEnLower = mappedEn.ToLowerInvariant();
        CasingKind mappedCasing = TokenGuards.ClassifyCasing(mappedEn);
        bool validCasing = mappedCasing is not CasingKind.AllCaps and not CasingKind.MixedCamel;
        turnOffCaps = Settings.FixInvertedCaps && mappedCasing == CasingKind.InvertedCapsLock;

        if (!validCasing)
        {
            return null;
        }

        if ((_lexicon.TryGetEnWord(mappedEnLower, out EnWordEntry enTarget) && enTarget.Zipf >= 3.30f) ||
            _lexicon.IsProtectedWord(mappedEnLower))
        {
            return TokenGuards.ApplyCasing(mappedEnLower, mappedCasing, shouldCapSentence);
        }

        if (Settings.FixTypos && _lexicon.TryGetExplicitFix(mappedEnLower, out string? explicitEn))
        {
            return TokenGuards.ApplyCasing(explicitEn, mappedCasing, shouldCapSentence);
        }

        return null;
    }

    private CorrectionResult CommitEnToRuLayoutWithRetroactiveChain(
        string currentRaw,
        string resolvedRuLower,
        char? trailingRuPunct,
        CasingKind currentMappedCasing,
        bool shouldCapCurrent,
        char delimiter,
        RuWordEntry? savedPrevRu,
        EnWordEntry? savedPrevEn,
        double confidence,
        string reason,
        bool turnOffCapsLock)
    {
        TargetLanguage switchTo = Settings.SwitchSystemLayoutOnAutoFix
            ? TargetLanguage.Russian
            : TargetLanguage.None;

        string punctSuffix = trailingRuPunct.HasValue ? trailingRuPunct.Value.ToString() : string.Empty;
        bool endsSentence = trailingRuPunct.HasValue &&
                            (trailingRuPunct.Value == '?' ||
                             (trailingRuPunct.Value == '.' && !TokenGuards.IsAbbreviationBeforePeriod(resolvedRuLower)));

        // Check up to 3 immediately preceding short (1..3 char) EN-typed tokens on the same line
        // e.g. "z yt " before "pyf." -> "я не знаю", "rfr " before "ltkf&" -> "как дела?"
        int chainCount = 0;
        if (delimiter == ' ' && _recentSpaceTokens.Count > 0)
        {
            int idx = _recentSpaceTokens.Count - 1;
            while (idx >= 0 && chainCount < 3)
            {
                RecentSpaceToken prevTok = _recentSpaceTokens[idx];
                if (!CanChainShortEnTokenToRu(prevTok, out _))
                {
                    break;
                }
                chainCount++;
                idx--;
            }
        }

        if (chainCount == 0)
        {
            string finalSingle = TokenGuards.ApplyCasing(resolvedRuLower, currentMappedCasing, shouldCapCurrent) + punctSuffix;
            CorrectionResult singleRes = CommitCorrection(
                CorrectionKind.WrongLayout,
                currentRaw,
                currentRaw,
                finalSingle,
                delimiter,
                switchTo,
                savedPrevRu,
                savedPrevEn,
                confidence,
                reason,
                turnOffCapsLock: turnOffCapsLock
            );

            if (trailingRuPunct.HasValue)
            {
                _recentSpaceTokens.Clear();
                if (endsSentence)
                {
                    _prevRuWord = null;
                    _prevEnWord = null;
                    _afterSentenceEndPunct = delimiter is ' ' or '\t';
                    _sawSentencePunctWithoutSpace = delimiter is not (' ' or '\t');
                }
            }

            return singleRes;
        }

        int startIdx = _recentSpaceTokens.Count - chainCount;
        var onScreenBuilder = new System.Text.StringBuilder(32);
        var desiredBuilder = new System.Text.StringBuilder(32);

        for (int i = startIdx; i < _recentSpaceTokens.Count; i++)
        {
            RecentSpaceToken tok = _recentSpaceTokens[i];
            string mappedPrev = KeyboardGeometry.ConvertEnToRu(tok.RawTyped);
            CasingKind prevCasing = TokenGuards.ClassifyCasing(mappedPrev);
            string casedPrev = TokenGuards.ApplyCasing(
                mappedPrev.ToLowerInvariant(),
                prevCasing,
                tok.StartedAtSentenceCap
            );

            onScreenBuilder.Append(tok.CurrentOnScreen).Append(' ');
            desiredBuilder.Append(casedPrev).Append(' ');

            _recentSpaceTokens[i] = tok with { CurrentOnScreen = casedPrev, WasAutoLayoutSwitched = true };
        }

        string finalCurrent = TokenGuards.ApplyCasing(resolvedRuLower, currentMappedCasing, forceCapitalize: false) + punctSuffix;
        onScreenBuilder.Append(currentRaw);
        desiredBuilder.Append(finalCurrent);

        string fullOriginalOnScreen = onScreenBuilder.ToString();
        string fullDesired = desiredBuilder.ToString();

        _hasTypedAnyWordInContext = true;
        _prevUnfixedStub = null;
        _lastCompletedForHotkey = new CompletedHotkeyToken(finalCurrent, delimiter.ToString());

        if (trailingRuPunct.HasValue)
        {
            _recentSpaceTokens.Clear();
        }
        else
        {
            _recentSpaceTokens.Add(new RecentSpaceToken(currentRaw, finalCurrent, true, false));
            TrimRecentSpaceTokens();
        }

        UpdatePreviousLanguageContext(resolvedRuLower);

        if (endsSentence)
        {
            _prevRuWord = null;
            _prevEnWord = null;
            _afterSentenceEndPunct = delimiter is ' ' or '\t';
            _sawSentencePunctWithoutSpace = delimiter is not (' ' or '\t');
        }
        else
        {
            _afterSentenceEndPunct = false;
        }

        TargetLanguage revertLayout = switchTo == TargetLanguage.Russian
            ? TargetLanguage.English
            : TargetLanguage.None;

        _undoSnapshot = new UndoSnapshot(
            CorrectionKind.WrongLayout,
            fullOriginalOnScreen,
            fullOriginalOnScreen,
            fullDesired + delimiter,
            revertLayout,
            savedPrevRu,
            savedPrevEn,
            LearnIgnoreOnConfirm: false,
            RestoreSentenceCapSuppression: false
        );

        return CorrectionResult.FromDiff(
            CorrectionKind.WrongLayout,
            fullOriginalOnScreen,
            fullOriginalOnScreen,
            fullDesired,
            delimiter,
            switchTo,
            confidence,
            "Забытая раскладка фразы EN→RU",
            turnOffCapsLock
        );
    }

    private bool CanChainShortEnTokenToRu(in RecentSpaceToken tok, out string mappedLower)
    {
        mappedLower = string.Empty;
        if (tok.WasAutoLayoutSwitched || tok.RawTyped != tok.CurrentOnScreen)
        {
            return false;
        }

        if (tok.RawTyped.Length is < 1 or > 3)
        {
            return false;
        }

        for (int i = 0; i < tok.RawTyped.Length; i++)
        {
            if (!KeyboardGeometry.IsEnKeyForRuLetter(tok.RawTyped[i]))
            {
                return false;
            }
        }

        string rawLower = tok.RawTyped.ToLowerInvariant();
        if (_lexicon.IsProtectedWord(rawLower) || _userDictionary.IsIgnored(tok.RawTyped))
        {
            return false;
        }

        // Do not chain genuine common English words ("in", "on", "to", "ok", "the", "and")
        if (_lexicon.TryGetEnWord(rawLower, out EnWordEntry enEntry) && enEntry.Zipf >= 4.4f)
        {
            return false;
        }

        mappedLower = KeyboardGeometry.ConvertEnToRu(tok.RawTyped).ToLowerInvariant();
        return _lexicon.TryGetRuWord(mappedLower, out RuWordEntry ruEntry) && ruEntry.Zipf >= 4.2f;
    }

    private static KeyStrokeEvent[] MapStrokesToRu(List<KeyStrokeEvent> events, int takeCount)
    {
        int n = Math.Min(events.Count, Math.Max(0, takeCount));
        var arr = new KeyStrokeEvent[n];
        for (int i = 0; i < n; i++)
        {
            KeyStrokeEvent ev = events[i];
            string mapped = KeyboardGeometry.ConvertEnToRu(ev.Character.ToString());
            char ruCh = mapped.Length > 0 ? mapped[0] : ev.Character;
            arr[i] = ev with { Character = ruCh };
        }
        return arr;
    }

    private static bool ContainsNonLetterEnKeyForRu(string span)
    {
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (!KeyboardGeometry.IsLatinLetter(c) && KeyboardGeometry.IsEnKeyForRuLetter(c))
            {
                return true;
            }
        }
        return false;
    }

    private string? TryFixValidReflexiveVerbGrammar(string lowerWord, in RuWordEntry current, in RuWordEntry prev)
    {
        if ((prev.Flags & RuGrammarFlags.TakesInfinitiveAfter) != 0 &&
            lowerWord.EndsWith("тся", StringComparison.Ordinal) &&
            (current.Flags & RuGrammarFlags.Infinitive) == 0)
        {
            string infForm = string.Concat(lowerWord.AsSpan(0, lowerWord.Length - 3), "ться");
            if (_lexicon.TryGetRuWord(infForm, out RuWordEntry infEntry) &&
                (infEntry.Flags & RuGrammarFlags.Infinitive) != 0)
            {
                return infForm;
            }
        }

        if ((prev.Flags & RuGrammarFlags.TakesFinite3PerAfter) != 0 &&
            lowerWord.EndsWith("ться", StringComparison.Ordinal) &&
            (current.Flags & RuGrammarFlags.Finite3Per) == 0)
        {
            string finForm = string.Concat(lowerWord.AsSpan(0, lowerWord.Length - 4), "тся");
            if (_lexicon.TryGetRuWord(finForm, out RuWordEntry finEntry) &&
                (finEntry.Flags & RuGrammarFlags.Finite3Per) != 0)
            {
                return finForm;
            }
        }

        return null;
    }

    private CorrectionResult CommitCorrection(
        CorrectionKind kind,
        string originalWordForLearn,
        string onScreenBeforeDelimiter,
        string desiredWord,
        char delimiter,
        TargetLanguage switchLayoutTo,
        RuWordEntry? savedPrevRu,
        EnWordEntry? savedPrevEn,
        double confidence,
        string reason,
        bool learnIgnoreOnConfirm = true,
        bool restoreSentenceCapSuppression = false,
        bool turnOffCapsLock = false,
        int moveCursorLeftCount = 0)
    {
        bool endsWithSentencePunct = desiredWord.Length >= 2 &&
                                     desiredWord[^1] is '.' or '!' or '?' &&
                                     (desiredWord[^1] != '.' || !TokenGuards.IsAbbreviationBeforePeriod(desiredWord.AsSpan(0, desiredWord.Length - 1)));
        _afterSentenceEndPunct = endsWithSentencePunct && delimiter is ' ' or '\t';
        if (endsWithSentencePunct && delimiter is not (' ' or '\t'))
        {
            _sawSentencePunctWithoutSpace = true;
        }

        _hasTypedAnyWordInContext = true;
        _prevUnfixedStub = null;
        _lastCompletedForHotkey = new CompletedHotkeyToken(desiredWord, delimiter.ToString());

        if (delimiter == ' ')
        {
            _recentSpaceTokens.Add(new RecentSpaceToken(
                originalWordForLearn,
                desiredWord,
                kind == CorrectionKind.WrongLayout,
                false
            ));
            TrimRecentSpaceTokens();
        }
        else
        {
            _recentSpaceTokens.Clear();
        }

        UpdatePreviousLanguageContext(desiredWord);

        if (desiredWord == onScreenBeforeDelimiter)
        {
            return CorrectionResult.NoChange;
        }

        TargetLanguage revertLayout = switchLayoutTo switch
        {
            TargetLanguage.Russian => TargetLanguage.English,
            TargetLanguage.English => TargetLanguage.Russian,
            _ => TargetLanguage.None
        };

        _undoSnapshot = new UndoSnapshot(
            kind,
            originalWordForLearn,
            onScreenBeforeDelimiter,
            desiredWord + delimiter,
            revertLayout,
            savedPrevRu,
            savedPrevEn,
            learnIgnoreOnConfirm,
            restoreSentenceCapSuppression
        );

        return CorrectionResult.FromDiff(
            kind,
            originalWordForLearn,
            onScreenBeforeDelimiter,
            desiredWord,
            delimiter,
            switchLayoutTo,
            confidence,
            reason,
            turnOffCapsLock,
            moveCursorLeftCount
        );
    }

    private void TrimRecentSpaceTokens()
    {
        if (_recentSpaceTokens.Count > 16)
        {
            _recentSpaceTokens.RemoveAt(0);
        }
    }

    private void RebuildSingleTokenBuffer(string token)
    {
        _rawSinceWhitespace.Clear();
        _wordEvents.Clear();
        foreach (char ch in token)
        {
            var kev = new KeyStrokeEvent(ch);
            _rawSinceWhitespace.Add(kev);
            if (char.IsLetterOrDigit(ch) || ch == '-')
            {
                _wordEvents.Add(kev);
            }
        }
    }

    private void UpdatePreviousLanguageContext(string committedText)
    {
        string clean = committedText.Trim('"', '.', ':', ',', ';');
        int spaceIdx = clean.LastIndexOf(' ');
        string lastToken = (spaceIdx >= 0 ? clean[(spaceIdx + 1)..] : clean).ToLowerInvariant();
        int hyphIdx = lastToken.LastIndexOf('-');
        if (hyphIdx >= 0 && hyphIdx < lastToken.Length - 1)
        {
            lastToken = lastToken[(hyphIdx + 1)..];
        }

        if (_lexicon.TryGetRuWord(lastToken, out RuWordEntry ru))
        {
            _prevRuWord = ru;
            _prevEnWord = null;
        }
        else if (_lexicon.TryGetEnWord(lastToken, out EnWordEntry en))
        {
            _prevEnWord = en;
            _prevRuWord = null;
        }
        else
        {
            _prevRuWord = null;
            _prevEnWord = null;
        }
    }

    private static (string Converted, TargetLanguage TargetLang) ConvertTokenOppositeLayout(string raw)
    {
        int cyr = 0;
        int enForRu = 0;
        foreach (char c in raw)
        {
            if (KeyboardGeometry.IsCyrillicLetter(c)) cyr++;
            else if (KeyboardGeometry.IsEnKeyForRuLetter(c)) enForRu++;
        }

        if (enForRu > cyr && enForRu > 0)
        {
            return (KeyboardGeometry.ConvertEnToRu(raw), TargetLanguage.Russian);
        }

        if (cyr > 0)
        {
            return (KeyboardGeometry.ConvertRuToEn(raw), TargetLanguage.English);
        }

        return (raw, TargetLanguage.None);
    }

    private static (string Converted, TargetLanguage TargetLang) ConvertPhraseOppositeLayout(string phrase)
    {
        int cyr = 0;
        int enForRu = 0;
        foreach (char c in phrase)
        {
            if (c == ' ') continue;
            if (KeyboardGeometry.IsCyrillicLetter(c)) cyr++;
            else if (KeyboardGeometry.IsEnKeyForRuLetter(c)) enForRu++;
        }

        if (enForRu > cyr && enForRu > 0)
        {
            var sb = new System.Text.StringBuilder(phrase.Length);
            foreach (char c in phrase)
            {
                sb.Append(c == ' ' ? ' ' : KeyboardGeometry.ConvertEnToRu(c.ToString())[0]);
            }
            return (sb.ToString(), TargetLanguage.Russian);
        }

        if (cyr > 0)
        {
            var sb = new System.Text.StringBuilder(phrase.Length);
            foreach (char c in phrase)
            {
                sb.Append(c == ' ' ? ' ' : KeyboardGeometry.ConvertRuToEn(c.ToString())[0]);
            }
            return (sb.ToString(), TargetLanguage.English);
        }

        return (phrase, TargetLanguage.None);
    }

    private bool IsClosingBracketOrQuoteDelimiter(char c)
    {
        if (c is ')' or '»' or '”')
        {
            return true;
        }

        if (c == '"' && _wordEvents.Count > 0)
        {
            if (_rawSinceWhitespace.Count > 0 && _rawSinceWhitespace[0].Character == '"')
            {
                return true;
            }

            for (int i = 0; i < _wordEvents.Count; i++)
            {
                if (KeyboardGeometry.IsCyrillicLetter(_wordEvents[i].Character))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool ShouldDeferEnPunctuationForLayout(char c)
    {
        if (Settings.LayoutMode != LayoutSwitchMode.AutoHighConfidenceAndHotkey)
        {
            return false;
        }

        if (_sawSentencePunctWithoutSpace)
        {
            return false;
        }

        // Trailing '?' on US QWERTY (Shift + /) is Russian ',' when attached to an EN-typed Russian word ("ghbdtn? vbh").
        if (c == '?')
        {
            if (_rawSinceWhitespace.Count < 3)
            {
                return false;
            }

            for (int i = 0; i < _rawSinceWhitespace.Count; i++)
            {
                if (!KeyboardGeometry.IsEnKeyForRuLetter(_rawSinceWhitespace[i].Character))
                {
                    return false;
                }
            }

            string rawBeforeQ = ExtractString(_rawSinceWhitespace);
            string lowerBeforeQ = rawBeforeQ.ToLowerInvariant();
            if (_lexicon.IsKnownEnWord(lowerBeforeQ) || _lexicon.IsProtectedWord(lowerBeforeQ))
            {
                return false;
            }

            string mappedRu = KeyboardGeometry.ConvertEnToRu(rawBeforeQ).ToLowerInvariant();
            if (_lexicon.IsKnownRuWord(mappedRu) ||
                (TokenGuards.IsPureCyrillicWord(mappedRu) && _lexicon.IsProtectedWord(mappedRu)))
            {
                return true;
            }

            var ruProbe = new List<int>(8);
            _lexicon.QueryRuCandidates(mappedRu, maxEditDistance: 1, ruProbe);
            return ruProbe.Count > 0;
        }

        if (c is not (',' or ';'))
        {
            return false;
        }

        if (_rawSinceWhitespace.Count == 0)
        {
            return true;
        }

        for (int i = 0; i < _rawSinceWhitespace.Count; i++)
        {
            if (!KeyboardGeometry.IsEnKeyForRuLetter(_rawSinceWhitespace[i].Character))
            {
                return false;
            }
        }

        string currentRaw = ExtractString(_rawSinceWhitespace);
        string currentLower = currentRaw.ToLowerInvariant();

        if (_lexicon.IsKnownEnWord(currentLower) || _lexicon.IsProtectedWord(currentLower))
        {
            return false;
        }

        string candidateRuPrefix = KeyboardGeometry.ConvertEnToRu(currentRaw + c).ToLowerInvariant();
        if (_lexicon.IsKnownRuWord(candidateRuPrefix))
        {
            return true;
        }

        var probe = new List<int>(8);
        _lexicon.QueryRuCandidates(candidateRuPrefix, maxEditDistance: 1, probe);
        if (probe.Count > 0)
        {
            return true;
        }

        return candidateRuPrefix.Length <= 3;
    }

    private bool ShouldDeferEnTrailingSlashOrAmpersand()
    {
        if (Settings.LayoutMode != LayoutSwitchMode.AutoHighConfidenceAndHotkey)
        {
            return false;
        }

        // Note: _rawSinceWhitespace already contains the newly appended '/' or '&' as its last element.
        int prefixLen = _rawSinceWhitespace.Count - 1;
        if (prefixLen < 3)
        {
            return false;
        }

        for (int i = 0; i < prefixLen; i++)
        {
            char ch = _rawSinceWhitespace[i].Character;
            if (ch == ':' || !KeyboardGeometry.IsEnKeyForRuLetter(ch))
            {
                return false;
            }
        }

        string prefixStr = ExtractString(_rawSinceWhitespace)[..prefixLen].ToLowerInvariant();
        return !_lexicon.IsKnownEnWord(prefixStr) && !_lexicon.IsProtectedWord(prefixStr);
    }

    private static bool IsImmediatePunctuationDelimiter(char c) =>
        c is ',' or '!' or '?' or ';';

    private static bool HasInternalLayoutPunctBeforeLast(List<KeyStrokeEvent> rawEvents)
    {
        for (int i = 0; i < rawEvents.Count - 1; i++)
        {
            if (rawEvents[i].Character is '/' or '&' or '?')
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasInternalDotOrColonBeforeLast(List<KeyStrokeEvent> rawEvents)
    {
        for (int i = 0; i < rawEvents.Count - 1; i++)
        {
            if (rawEvents[i].Character is '.' or ':')
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsAllPeriods(List<KeyStrokeEvent> rawEvents)
    {
        for (int i = 0; i < rawEvents.Count; i++)
        {
            if (rawEvents[i].Character != '.')
            {
                return false;
            }
        }
        return true;
    }

    private static string ExtractString(List<KeyStrokeEvent> events)
    {
        if (events.Count == 0)
        {
            return string.Empty;
        }

        Span<char> span = events.Count <= 64 ? stackalloc char[events.Count] : new char[events.Count];
        for (int i = 0; i < events.Count; i++)
        {
            span[i] = events[i].Character;
        }
        return new string(span);
    }
}
