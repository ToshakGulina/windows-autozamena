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

    private readonly LexiconDatabase _lexicon;
    private readonly UserDictionary _userDictionary;

    private readonly List<KeyStrokeEvent> _wordEvents = new(32);
    private readonly List<KeyStrokeEvent> _rawSinceWhitespace = new(32);
    private readonly HashSet<string> _tempUndoSuppressed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingLearnAfterUndo = new(StringComparer.OrdinalIgnoreCase);

    private RuWordEntry? _prevRuWord;
    private EnWordEntry? _prevEnWord;

    private PendingBrokenStub? _prevUnfixedStub;
    private CompletedTailAddition? _lastTailAddition;
    private UndoSnapshot? _undoSnapshot;

    private bool _afterSentenceEndPunct;
    private bool _sawSentencePunctWithoutSpace;
    private bool _afterDotOrColonWithoutSpace;
    private bool _inCompoundToken;
    private bool _hasTypedAnyWordInContext;

    public EngineSettings Settings { get; set; }

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

    private readonly record struct UndoSnapshot(
        string OriginalTokenForLearn,
        string OriginalOnScreenBeforeDelimiter,
        string CurrentOnScreenWithDelimiter,
        TargetLanguage RevertLayoutTo,
        RuWordEntry? PreviousRuBeforeCorrection,
        EnWordEntry? PreviousEnBeforeCorrection
    );

    public void ResetContext()
    {
        _wordEvents.Clear();
        _rawSinceWhitespace.Clear();
        _prevRuWord = null;
        _prevEnWord = null;
        _prevUnfixedStub = null;
        _lastTailAddition = null;
        _undoSnapshot = null;
        _afterSentenceEndPunct = false;
        _sawSentencePunctWithoutSpace = false;
        _afterDotOrColonWithoutSpace = false;
        _inCompoundToken = false;
        _hasTypedAnyWordInContext = false;
    }

    /// <summary>
    /// Feeds a non-whitespace character typed by the user.
    /// Returns a <see cref="CorrectionResult"/> only when a punctuation delimiter triggers an immediate fix;
    /// otherwise returns <see cref="CorrectionResult.NoChange"/>.
    /// </summary>
    public CorrectionResult OnCharacter(KeyStrokeEvent ev)
    {
        char c = ev.Character;
        _undoSnapshot = null;

        if (c == ' ' || c == '\t')
        {
            return OnWhitespace(c, ev.TimestampMs);
        }

        if (c == '\r' || c == '\n')
        {
            CorrectionResult res = EvaluateCurrentBufferOnDelimiter(c, ev.TimestampMs, allowWrongLayoutRaw: true);
            ResetContext();
            return res;
        }

        // Check if this character is a punctuation delimiter vs part of a word / wrong-layout token
        if (IsSentenceOrClauseDelimiter(c))
        {
            // Could c be a Russian letter typed on an EN keyboard (e.g. 'k.,k.' = 'люблю' or ';bpym' = 'жизнь')?
            if (ShouldDeferEnPunctuationForLayout(c))
            {
                _rawSinceWhitespace.Add(ev);
                _wordEvents.Clear();
                return CorrectionResult.NoChange;
            }

            CorrectionResult punctRes = EvaluateCurrentBufferOnDelimiter(c, ev.TimestampMs, allowWrongLayoutRaw: false);

            _wordEvents.Clear();
            _rawSinceWhitespace.Clear();
            _prevUnfixedStub = null;
            _lastTailAddition = null;
            _afterDotOrColonWithoutSpace = c is '.' or ':';

            if (c is '.' or '!' or '?')
            {
                _sawSentencePunctWithoutSpace = true;
                _prevRuWord = null;
                _prevEnWord = null;
            }
            else if (c is ',' or ';' or ':')
            {
                _sawSentencePunctWithoutSpace = false;
            }

            return punctRes;
        }

        // Any character typed immediately after '.' or ':' with NO space (e.g. site.org, AutoZamena.exe, https://)
        // marks the token as a compound technical identifier until whitespace!
        if (_afterDotOrColonWithoutSpace)
        {
            _inCompoundToken = true;
            _afterDotOrColonWithoutSpace = false;
        }

        _sawSentencePunctWithoutSpace = false;
        _rawSinceWhitespace.Add(ev);

        if (char.IsLetterOrDigit(c) || c == '-')
        {
            _wordEvents.Add(ev);
        }
        else
        {
            // Symbol like [, ], ', `, ", @, / — keep in _rawSinceWhitespace for layout/URL checks
            if (!KeyboardGeometry.IsEnKeyForRuLetter(c))
            {
                _wordEvents.Clear();
                _inCompoundToken = true;
            }
        }

        return CorrectionResult.NoChange;
    }

    /// <summary>
    /// Handles Space or Tab delimiter.
    /// </summary>
    public CorrectionResult OnWhitespace(char whitespaceChar = ' ', long timestampMs = 0)
    {
        _undoSnapshot = null;
        _afterDotOrColonWithoutSpace = false;

        if (_sawSentencePunctWithoutSpace && _rawSinceWhitespace.Count == 0)
        {
            _afterSentenceEndPunct = true;
            _sawSentencePunctWithoutSpace = false;
            _inCompoundToken = false;
            return CorrectionResult.NoChange;
        }

        _sawSentencePunctWithoutSpace = false;

        CorrectionResult res = EvaluateCurrentBufferOnDelimiter(
            whitespaceChar,
            timestampMs,
            allowWrongLayoutRaw: whitespaceChar == ' '
        );

        _inCompoundToken = false;
        _wordEvents.Clear();
        _rawSinceWhitespace.Clear();
        return res;
    }

    /// <summary>
    /// Handles physical Backspace key. If the immediately preceding keystroke triggered an auto-correction,
    /// returns an <see cref="CorrectionKind.UndoRestore"/> diff restoring the user's exact typed characters!
    /// </summary>
    public CorrectionResult OnBackspace()
    {
        if (_undoSnapshot.HasValue && Settings.Enabled && Settings.UndoOnImmediateBackspace)
        {
            UndoSnapshot snap = _undoSnapshot.Value;
            _undoSnapshot = null;

            if (!string.IsNullOrEmpty(snap.OriginalTokenForLearn))
            {
                _tempUndoSuppressed.Add(snap.OriginalTokenForLearn);
                if (Settings.AutoLearnOnUndo)
                {
                    _pendingLearnAfterUndo.Add(snap.OriginalTokenForLearn);
                }
            }

            _prevRuWord = snap.PreviousRuBeforeCorrection;
            _prevEnWord = snap.PreviousEnBeforeCorrection;
            _prevUnfixedStub = null;
            _lastTailAddition = null;

            // Rebuild buffer to match restored token so user can either press Space or edit further
            _wordEvents.Clear();
            _rawSinceWhitespace.Clear();
            foreach (char ch in snap.OriginalOnScreenBeforeDelimiter)
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
    /// Explicitly converts the layout (RU &lt;-&gt; EN) of the currently buffered word or the just-finished word
    /// when the user presses the global layout-convert hotkey (e.g. Pause/Break).
    /// </summary>
    public CorrectionResult ConvertLastWordLayoutByHotkey()
    {
        // Case 1: User is currently at the end of a typed token (before pressing Space)
        if (_rawSinceWhitespace.Count > 0)
        {
            string raw = ExtractString(_rawSinceWhitespace);
            (string converted, TargetLanguage targetLang) = ConvertTokenOppositeLayout(raw);
            if (converted != raw && targetLang != TargetLanguage.None)
            {
                _rawSinceWhitespace.Clear();
                _wordEvents.Clear();
                foreach (char ch in converted)
                {
                    var kev = new KeyStrokeEvent(ch);
                    _rawSinceWhitespace.Add(kev);
                    if (char.IsLetterOrDigit(ch) || ch == '-')
                    {
                        _wordEvents.Add(kev);
                    }
                }

                UpdatePreviousLanguageContext(converted);
                return CorrectionResult.FromDiff(
                    CorrectionKind.HotkeyLayoutSwitch,
                    raw,
                    raw,
                    converted,
                    trailingDelimiter: null,
                    switchLayoutTo: targetLang,
                    confidenceScore: 1.0,
                    reason: targetLang == TargetLanguage.Russian ? "Хоткей раскладки EN→RU" : "Хоткей раскладки RU→EN"
                );
            }
        }

        // Case 2: User already pressed Space after a token (e.g. "ghbdtn ")
        if (_prevUnfixedStub.HasValue && _prevUnfixedStub.Value.DelimiterUsed == ' ')
        {
            string raw = _prevUnfixedStub.Value.RawToken;
            string currentScreenWord = _prevUnfixedStub.Value.CommittedOnScreen;
            (string converted, TargetLanguage targetLang) = ConvertTokenOppositeLayout(raw);
            if (converted != raw && targetLang != TargetLanguage.None)
            {
                string onScreen = currentScreenWord + " ";
                string desired = converted + " ";
                _prevUnfixedStub = null;
                UpdatePreviousLanguageContext(converted);

                return CorrectionResult.FromDiff(
                    CorrectionKind.HotkeyLayoutSwitch,
                    raw,
                    onScreen,
                    desired,
                    trailingDelimiter: null,
                    switchLayoutTo: targetLang,
                    confidenceScore: 1.0,
                    reason: targetLang == TargetLanguage.Russian ? "Хоткей раскладки EN→RU" : "Хоткей раскладки RU→EN"
                );
            }
        }

        return CorrectionResult.NoChange;
    }

    /// <summary>
    /// Convenience simulator for unit testing and interactive sandbox preview.
    /// </summary>
    public string SimulateText(string typedSequence)
    {
        var screen = new System.Text.StringBuilder(typedSequence.Length + 16);
        long t = 1000;

        foreach (char c in typedSequence)
        {
            t += 95;
            if (c == '\b')
            {
                CorrectionResult undo = OnBackspace();
                if (undo.HasChange)
                {
                    ApplyToBuffer(screen, undo);
                }
                else if (screen.Length > 0)
                {
                    screen.Length -= 1;
                }
                continue;
            }

            CorrectionResult res = OnCharacter(new KeyStrokeEvent(c, DeltaPrevMs: 95, OverlapWithPrev: false, TimestampMs: t));
            if (res.HasChange)
            {
                ApplyToBuffer(screen, res);
            }
            else
            {
                screen.Append(c);
            }
        }

        return screen.ToString();
    }

    private static void ApplyToBuffer(System.Text.StringBuilder screen, CorrectionResult res)
    {
        int remove = Math.Min(screen.Length, res.BackspacesCount);
        screen.Length -= remove;
        screen.Append(res.AppendText);
        if (res.TrailingDelimiter.HasValue)
        {
            screen.Append(res.TrailingDelimiter.Value);
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
            return CorrectionResult.NoChange;
        }

        string rawSpan = ExtractString(_rawSinceWhitespace);
        string wordToken = ExtractString(_wordEvents);

        if (string.IsNullOrEmpty(rawSpan))
        {
            return CorrectionResult.NoChange;
        }

        bool shouldCapSentence =
            (_afterSentenceEndPunct && Settings.AutoCapitalizeAfterSentence) ||
            (!_hasTypedAnyWordInContext && Settings.AutoCapitalizeFirstWord);

        // Save previous context snapshot for potential Undo
        RuWordEntry? savedPrevRu = _prevRuWord;
        EnWordEntry? savedPrevEn = _prevEnWord;

        // 0. If the user previously undid a word and now confirms that exact word with a delimiter -> learn it!
        if (_pendingLearnAfterUndo.Remove(rawSpan))
        {
            _userDictionary.AddIgnoredWord(rawSpan);
        }
        else if (!string.IsNullOrEmpty(wordToken) && _pendingLearnAfterUndo.Remove(wordToken))
        {
            _userDictionary.AddIgnoredWord(wordToken);
        }

        // 1. User custom replacement / snippet (highest priority, explicit user intent)
        if (_userDictionary.TryGetCustomReplacement(rawSpan, out string? customRepl))
        {
            return CommitCorrection(
                CorrectionKind.UserSnippet,
                rawSpan,
                rawSpan,
                customRepl,
                delimiter,
                TargetLanguage.None,
                savedPrevRu,
                savedPrevEn,
                1.0,
                "Личная автозамена"
            );
        }

        // Check URL / code / numbers / symbols / mid-dot compound guard on rawSpan BEFORE anything else,
        // EXCEPT when rawSpan is an EN-layout Russian word (e.g. "k.,k." or ";bpym")!
        if (_inCompoundToken ||
            TokenGuards.ContainsDisqualifyingSymbolOrDigit(rawSpan) ||
            TokenGuards.LooksLikeUrlOrDomain(rawSpan))
        {
            _afterSentenceEndPunct = false;
            _hasTypedAnyWordInContext = true;
            _prevRuWord = null;
            _prevEnWord = null;
            _prevUnfixedStub = null;
            _lastTailAddition = null;
            return CorrectionResult.NoChange;
        }

        // 2. Wrong keyboard layout auto-detection (e.g. "ghbdtn" -> "привет", "k.,k." -> "люблю", "руддщ" -> "hello")
        if (Settings.LayoutMode == LayoutSwitchMode.AutoHighConfidenceAndHotkey &&
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

        // From here on we need a clean word token matching rawSpan
        if (string.IsNullOrEmpty(wordToken) || wordToken != rawSpan)
        {
            _afterSentenceEndPunct = false;
            _hasTypedAnyWordInContext = true;
            _prevUnfixedStub = new PendingBrokenStub(rawSpan, rawSpan, CasingKind.Lower, delimiter, timestampMs);
            return CorrectionResult.NoChange;
        }

        // 3. Spacebar Rollover checks ("приве т " -> "привет " and delayed tail "назад д " -> "назад ")
        if (Settings.FixSpacebarRollover && wordToken.Length == 1 && delimiter == ' ')
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
                    // Erase the stray single letter, swallow the new Space -> screen stays "назад "
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
            // Works even if the broken stub ("делае") had just been replaced on screen with "дела"!
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
                            string.Empty,
                            rawTypedTwoTokens,
                            desiredMerged + delimiter,
                            TargetLanguage.None,
                            savedPrevRu,
                            savedPrevEn
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
        if (casing is CasingKind.AllCaps or CasingKind.MixedCamel)
        {
            _afterSentenceEndPunct = false;
            _hasTypedAnyWordInContext = true;
            _prevUnfixedStub = null;
            UpdatePreviousLanguageContext(wordToken);
            return CorrectionResult.NoChange;
        }

        if (_tempUndoSuppressed.Contains(wordToken) || _userDictionary.IsIgnored(wordToken))
        {
            _afterSentenceEndPunct = false;
            _hasTypedAnyWordInContext = true;
            _prevUnfixedStub = null;
            UpdatePreviousLanguageContext(wordToken);
            return CorrectionResult.NoChange;
        }

        // 4. Sticky Shift Double-Caps check (ПРивет -> Привет) when underlying word is valid or protected
        bool stickyCaps = Settings.FixDoubleCaps && casing == CasingKind.StickyDoubleCaps;
        bool isProtected = _lexicon.IsProtectedWord(lower);
        bool isPureRu = TokenGuards.IsPureCyrillicWord(lower);
        bool isPureEn = !isPureRu && TokenGuards.IsPureLatinWord(lower);

        // 5. Explicit unambiguous fixes ("незнаю" -> "не знаю", "вобщем" -> "в общем", "teh" -> "the")
        if (Settings.FixTypos && !isProtected && _lexicon.TryGetExplicitFix(lower, out string? explicitTarget))
        {
            string casedTarget = TokenGuards.ApplyCasing(explicitTarget, casing, shouldCapSentence);
            return CommitCorrection(
                CorrectionKind.ExplicitFix,
                wordToken,
                wordToken,
                casedTarget,
                delimiter,
                TargetLanguage.None,
                savedPrevRu,
                savedPrevEn,
                0.99,
                "Частая орфографическая опечатка"
            );
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
                    string casedReflexive = TokenGuards.ApplyCasing(reflexiveFix, casing, shouldCapSentence);
                    return CommitCorrection(
                        CorrectionKind.Typo,
                        wordToken,
                        wordToken,
                        casedReflexive,
                        delimiter,
                        TargetLanguage.None,
                        savedPrevRu,
                        savedPrevEn,
                        0.96,
                        "Грамматика -тся/-ться по контексту"
                    );
                }
            }

            // Handle sticky double-caps ("ПРивет" -> "Привет") or sentence capitalization (". на" -> ". На")
            if (stickyCaps && (wordToken.Length >= 4 || (!isProtected && knownRu.Zipf >= 5.0f)))
            {
                string fixedCaps = TokenGuards.ApplyCasing(lower, CasingKind.Capitalized, forceCapitalize: true);
                return CommitCorrection(
                    CorrectionKind.DoubleCaps,
                    wordToken,
                    wordToken,
                    fixedCaps,
                    delimiter,
                    TargetLanguage.None,
                    savedPrevRu,
                    savedPrevEn,
                    0.99,
                    "Исправление залипшего Shift"
                );
            }

            if (shouldCapSentence && casing == CasingKind.Lower)
            {
                string capWord = TokenGuards.ApplyCasing(lower, CasingKind.Capitalized, forceCapitalize: true);
                return CommitCorrection(
                    CorrectionKind.AutoCapitalize,
                    wordToken,
                    wordToken,
                    capWord,
                    delimiter,
                    TargetLanguage.None,
                    savedPrevRu,
                    savedPrevEn,
                    0.99,
                    "Заглавная буква в начале предложения"
                );
            }

            _afterSentenceEndPunct = false;
            _hasTypedAnyWordInContext = true;
            _prevUnfixedStub = null;
            if (!isProtected) _prevRuWord = knownRu;
            else _prevRuWord = null;
            _prevEnWord = null;
            return CorrectionResult.NoChange;
        }

        if (isPureEn && (_lexicon.TryGetEnWord(lower, out EnWordEntry knownEn) || isProtected))
        {
            if (stickyCaps && (wordToken.Length >= 4 || (!isProtected && knownEn.Zipf >= 5.0f)))
            {
                string fixedCaps = TokenGuards.ApplyCasing(lower, CasingKind.Capitalized, forceCapitalize: true);
                return CommitCorrection(
                    CorrectionKind.DoubleCaps,
                    wordToken,
                    wordToken,
                    fixedCaps,
                    delimiter,
                    TargetLanguage.None,
                    savedPrevRu,
                    savedPrevEn,
                    0.99,
                    "Исправление залипшего Shift"
                );
            }

            if (shouldCapSentence && casing == CasingKind.Lower)
            {
                string capWord = TokenGuards.ApplyCasing(lower, CasingKind.Capitalized, forceCapitalize: true);
                return CommitCorrection(
                    CorrectionKind.AutoCapitalize,
                    wordToken,
                    wordToken,
                    capWord,
                    delimiter,
                    TargetLanguage.None,
                    savedPrevRu,
                    savedPrevEn,
                    0.99,
                    "Заглавная буква в начале предложения"
                );
            }

            _afterSentenceEndPunct = false;
            _hasTypedAnyWordInContext = true;
            _prevUnfixedStub = null;
            if (!isProtected) _prevEnWord = knownEn;
            else _prevEnWord = null;
            _prevRuWord = null;
            return CorrectionResult.NoChange;
        }

        // 7. Unknown word: run Noisy-Channel Physical + Timing + Context Typo Scorer!
        if (Settings.FixTypos && wordToken.Length >= 3)
        {
            bool isMidSentenceCap = casing == CasingKind.Capitalized &&
                                    !shouldCapSentence &&
                                    _hasTypedAnyWordInContext;

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
                    string finalWord = TokenGuards.ApplyCasing(bestRu.Value.Word, casing, shouldCapSentence);
                    string reason = bestRu.Value.ContextBoost >= 0.8f
                        ? "Опечатка + контекст соседнего слова"
                        : "Геометрия клавиш и частотность";

                    // Record tail-completion metadata in case of a lagging spacebar-rollover key
                    if (bestRu.Value.Word.Length == lower.Length + 1 &&
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
                        wordToken,
                        finalWord,
                        delimiter,
                        TargetLanguage.None,
                        savedPrevRu,
                        savedPrevEn,
                        bestRu.Value.Score,
                        reason
                    );
                    _prevUnfixedStub = new PendingBrokenStub(wordToken, finalWord, casing, delimiter, timestampMs);
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
                    string finalWord = TokenGuards.ApplyCasing(bestEn.Value.Word, casing, shouldCapSentence);
                    string reason = bestEn.Value.ContextBoost >= 0.8f
                        ? "Typo + bigram context"
                        : "Геометрия клавиш (EN)";

                    if (bestEn.Value.Word.Length == lower.Length + 1 &&
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
                        wordToken,
                        finalWord,
                        delimiter,
                        TargetLanguage.None,
                        savedPrevRu,
                        savedPrevEn,
                        bestEn.Value.Score,
                        reason
                    );
                    _prevUnfixedStub = new PendingBrokenStub(wordToken, finalWord, casing, delimiter, timestampMs);
                    return enFix;
                }
            }
        }

        // Word was left untouched (e.g. unknown slang, name, or incomplete broken stub like "приве")
        _afterSentenceEndPunct = false;
        _hasTypedAnyWordInContext = true;
        _prevUnfixedStub = new PendingBrokenStub(wordToken, wordToken, casing, delimiter, timestampMs);
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
        // Only auto-switch tokens of length >= 4 to guarantee zero irritation on short abbreviations (if, go, ok, pr, вс, ок)
        if (rawSpan.Length < 4)
        {
            return null;
        }

        if (_tempUndoSuppressed.Contains(rawSpan) || _userDictionary.IsIgnored(rawSpan))
        {
            return null;
        }

        string rawLower = rawSpan.ToLowerInvariant();
        if (_lexicon.IsProtectedWord(rawLower))
        {
            return null;
        }

        // Direction 1: Typed in EN layout -> intended RU word (e.g. "ghbdtn" -> "привет", "k.,k." -> "люблю", ";bpym" -> "жизнь")
        bool allEnKeysForRu = true;
        for (int i = 0; i < rawSpan.Length; i++)
        {
            if (!KeyboardGeometry.IsEnKeyForRuLetter(rawSpan[i]))
            {
                allEnKeysForRu = false;
                break;
            }
        }

        if (allEnKeysForRu && !_lexicon.IsKnownEnWord(rawLower))
        {
            // Also make sure it isn't just a simple 1-key typo of a very common English word (e.g. "hellp" -> "hello")!
            if (TokenGuards.IsPureLatinWord(rawLower))
            {
                ScoredCandidate? closeEn = NoisyChannelScorer.FindBestEnCandidate(
                    _lexicon,
                    rawLower,
                    ReadOnlySpan<KeyStrokeEvent>.Empty,
                    _prevEnWord,
                    SensitivityLevel.Conservative,
                    isMidSentenceCapitalized: false
                );
                if (closeEn.HasValue && closeEn.Value.EditCost <= 0.75f && closeEn.Value.Score >= 3.2f)
                {
                    return null;
                }
            }

            string mappedRu = KeyboardGeometry.ConvertEnToRu(rawSpan);
            string mappedRuLower = mappedRu.ToLowerInvariant();
            CasingKind mappedCasing = TokenGuards.ClassifyCasing(mappedRu);
            if (mappedCasing is not CasingKind.AllCaps and not CasingKind.MixedCamel &&
                _lexicon.TryGetRuWord(mappedRuLower, out RuWordEntry ruTarget) &&
                ruTarget.Zipf >= 3.10f)
            {
                string finalRu = TokenGuards.ApplyCasing(mappedRuLower, mappedCasing, shouldCapSentence);
                TargetLanguage switchTo = Settings.SwitchSystemLayoutOnAutoFix
                    ? TargetLanguage.Russian
                    : TargetLanguage.None;

                return CommitCorrection(
                    CorrectionKind.WrongLayout,
                    rawSpan,
                    rawSpan,
                    finalRu,
                    delimiter,
                    switchTo,
                    savedPrevRu,
                    savedPrevEn,
                    ruTarget.Zipf,
                    "Забытая раскладка EN→RU"
                );
            }
        }

        // Direction 2: Typed in RU layout -> intended EN word (e.g. "руддщ" -> "hello")
        if (TokenGuards.IsPureCyrillicWord(rawSpan) && !_lexicon.IsKnownRuWord(rawLower))
        {
            ScoredCandidate? closeRu = NoisyChannelScorer.FindBestRuCandidate(
                _lexicon,
                rawLower,
                ReadOnlySpan<KeyStrokeEvent>.Empty,
                _prevRuWord,
                SensitivityLevel.Conservative,
                isMidSentenceCapitalized: false
            );
            if (closeRu.HasValue && closeRu.Value.EditCost <= 0.75f && closeRu.Value.Score >= 3.2f)
            {
                return null;
            }

            string mappedEn = KeyboardGeometry.ConvertRuToEn(rawSpan);
            string mappedEnLower = mappedEn.ToLowerInvariant();
            CasingKind mappedCasing = TokenGuards.ClassifyCasing(mappedEn);
            if (mappedCasing is not CasingKind.AllCaps and not CasingKind.MixedCamel &&
                (_lexicon.TryGetEnWord(mappedEnLower, out EnWordEntry enTarget) && enTarget.Zipf >= 3.30f ||
                 _lexicon.IsProtectedWord(mappedEnLower)))
            {
                string finalEn = TokenGuards.ApplyCasing(mappedEnLower, mappedCasing, shouldCapSentence);
                TargetLanguage switchTo = Settings.SwitchSystemLayoutOnAutoFix
                    ? TargetLanguage.English
                    : TargetLanguage.None;

                return CommitCorrection(
                    CorrectionKind.WrongLayout,
                    rawSpan,
                    rawSpan,
                    finalEn,
                    delimiter,
                    switchTo,
                    savedPrevRu,
                    savedPrevEn,
                    4.5,
                    "Забытая раскладка RU→EN"
                );
            }
        }

        return null;
    }

    private string? TryFixValidReflexiveVerbGrammar(string lowerWord, in RuWordEntry current, in RuWordEntry prev)
    {
        // "приходится учится" / "надо получится" -> -ться
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

        // "он учиться" / "оно получиться" -> -тся
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
        string reason)
    {
        _afterSentenceEndPunct = false;
        _hasTypedAnyWordInContext = true;
        _prevUnfixedStub = null;

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
            originalWordForLearn,
            onScreenBeforeDelimiter,
            desiredWord + delimiter,
            revertLayout,
            savedPrevRu,
            savedPrevEn
        );

        return CorrectionResult.FromDiff(
            kind,
            originalWordForLearn,
            onScreenBeforeDelimiter,
            desiredWord,
            delimiter,
            switchLayoutTo,
            confidence,
            reason
        );
    }

    private void UpdatePreviousLanguageContext(string committedText)
    {
        // If multi-word replacement (e.g. "не знаю"), take the last word for bigram context
        int spaceIdx = committedText.LastIndexOf(' ');
        string lastToken = (spaceIdx >= 0 ? committedText[(spaceIdx + 1)..] : committedText).ToLowerInvariant();

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

    private bool ShouldDeferEnPunctuationForLayout(char c)
    {
        if (Settings.LayoutMode != LayoutSwitchMode.AutoHighConfidenceAndHotkey)
        {
            return false;
        }

        // Only ',', '.', ';', ':' map to Russian letters б, ю, ж, Ж
        if (c is not (',' or '.' or ';' or ':'))
        {
            return false;
        }

        // Never defer consecutive sentence punctuation such as "..." ellipsis
        if (_sawSentencePunctWithoutSpace)
        {
            return false;
        }

        // If buffer is empty, ';' or ':' or ',' or '.' could start a Russian word like ";bpym" (жизнь) or ",kby" (блин)
        if (_rawSinceWhitespace.Count == 0)
        {
            return true;
        }

        // Ensure all previous chars in _rawSinceWhitespace also map to RU letters
        for (int i = 0; i < _rawSinceWhitespace.Count; i++)
        {
            if (!KeyboardGeometry.IsEnKeyForRuLetter(_rawSinceWhitespace[i].Character))
            {
                return false;
            }
        }

        string currentRaw = ExtractString(_rawSinceWhitespace);
        string currentLower = currentRaw.ToLowerInvariant();

        // If what's typed so far is already a known English word ("hello,"), do NOT defer comma/period!
        if (_lexicon.IsKnownEnWord(currentLower) || _lexicon.IsProtectedWord(currentLower))
        {
            return false;
        }

        // Check if currentRaw + c maps to a Russian prefix that actually has candidates in RU lexicon
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

        // Also defer short 1-2 char prefixes like "k." ("лю" -> "люблю") where full word is longer than prefix+1
        return candidateRuPrefix.Length <= 3;
    }

    private static bool IsSentenceOrClauseDelimiter(char c) =>
        c is '.' or ',' or '!' or '?' or ';' or ':';

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
