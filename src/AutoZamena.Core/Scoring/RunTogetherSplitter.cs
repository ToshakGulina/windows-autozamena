using System;
using System.Collections.Generic;
using AutoZamena.Core.Lexicon;

namespace AutoZamena.Core.Scoring;

public readonly record struct SplitCandidate(
    string Text,
    float Score,
    bool HasBigram
);

/// <summary>
/// Splits accidentally merged (run-together) words caused by a missed Spacebar or missing particle hyphen,
/// while protecting solid compound words and productive verb/noun prefixes.
/// </summary>
public static class RunTogetherSplitter
{
    private static readonly HashSet<string> RuCompoundLeftStems = new(StringComparer.Ordinal)
    {
        "авто", "мото", "вело", "теле", "видео", "аудио", "фото", "радио", "кино", "медиа",
        "микро", "макро", "мульти", "супер", "гипер", "ультра", "мини", "макси", "моно", "поли",
        "стерео", "аэро", "гидро", "термо", "электро", "энерго", "тепло", "водо", "газо", "звуко",
        "свето", "цвето", "само", "взаимо", "много", "мало", "высоко", "низко", "широко", "узко",
        "глубоко", "дально", "близко", "быстро", "долго", "ново", "старо", "средне", "верхне",
        "нижне", "выше", "ниже", "чисто", "светло", "темно", "ярко", "слабо", "сильно", "громко",
        "тихо", "прямо", "равно", "одно", "двух", "трех", "трёх", "четырех", "четырёх", "пяти",
        "шести", "семи", "восьми", "девяти", "десяти", "сто", "пол", "полу", "сверх", "меж",
        "спец", "глав", "гос", "соц", "фин", "тех", "мед", "юр", "хоз", "строй", "торг", "пром",
        "кибер", "онлайн", "офлайн", "оффлайн", "веб", "топ", "пост", "пре", "анти", "контр", "псевдо"
    };

    private static readonly HashSet<string> RuSeparateAfterNe = new(StringComparer.Ordinal)
    {
        "знаю", "знаешь", "знает", "знаем", "знаете", "знают", "знал", "знала", "знали",
        "могу", "можешь", "может", "можем", "можете", "могут", "мог", "могла", "могло", "могли",
        "хочу", "хочешь", "хочет", "хотим", "хотите", "хотят", "хотел", "хотела", "хотели",
        "буду", "будешь", "будет", "будем", "будете", "будут", "был", "была", "было", "были",
        "надо", "нужно", "можно", "жаль", "пора", "нельзя", "есть", "нет", "стоит", "стоило",
        "рад", "рада", "рады", "готов", "готова", "готовы", "должен", "должна", "должно", "должны",
        "обязан", "обязана", "обязаны", "уверен", "уверена", "уверены", "согласен", "согласна", "согласны",
        "очень", "так", "там", "тут", "здесь", "тогда", "сейчас", "всегда", "совсем", "слишком",
        "только", "просто", "уже", "еще", "ещё", "даже", "вчера", "сегодня", "завтра",
        "я", "ты", "он", "она", "оно", "мы", "вы", "они", "мне", "меня", "тебе", "тебя", "нам", "вам",
        "кто", "что", "это", "этот", "эта", "эти", "тот", "та", "те", "все", "всё", "вся", "всю", "всех", "всем",
        "мой", "моя", "моё", "мои", "твой", "свой", "наш", "ваш",
        "для", "без", "про", "при", "от", "до", "за", "по", "из", "на", "в", "с", "у", "к", "о", "со", "ко", "во", "об"
    };

    private static readonly HashSet<string> RuObliquePronouns = new(StringComparer.Ordinal)
    {
        "меня", "тебя", "него", "нее", "неё", "нас", "вас", "них",
        "мне", "тебе", "нему", "ней", "нам", "вам", "ним", "ними",
        "мной", "тобой", "собой",
        "этом", "этой", "этих", "этот", "эта", "это", "этого", "этому", "этим",
        "том", "той", "тех", "того", "тому", "тем",
        "ком", "чем", "чём", "всех", "всем", "всеми", "всю", "всей"
    };

    private static readonly HashSet<string> RuTwoCharPrefixes = new(StringComparer.Ordinal)
    {
        "на", "за", "по", "от", "до", "из", "со", "во", "об", "вы", "ко"
    };

    private static readonly HashSet<string> RuLongVerbPrefixes = new(StringComparer.Ordinal)
    {
        "под", "над", "пред", "при", "про", "без", "через", "пере", "раз", "рас"
    };

    private static readonly HashSet<string> RuInterrogativeToStems = new(StringComparer.Ordinal)
    {
        "кто", "что", "где", "куда", "откуда", "когда", "как", "почему", "зачем", "отчего", "сколько",
        "какой", "какая", "какое", "какие", "какого", "какому", "каким", "каком", "какую", "каких", "какими",
        "чей", "чья", "чье", "чьё", "чьи", "чьего", "чьему", "чьим", "чьем", "чьей", "чью", "чьих", "чьими"
    };

    private static readonly HashSet<string> EnCompoundLeftStems = new(StringComparer.Ordinal)
    {
        "over", "under", "super", "hyper", "ultra", "multi", "inter", "intra", "trans", "micro", "macro",
        "cyber", "photo", "video", "audio", "auto", "self", "non", "pre", "post", "sub", "anti", "counter",
        "cross", "back", "down", "out", "up", "work", "time", "life", "home", "hand", "head", "foot", "eye",
        "book", "water", "fire", "air", "land", "snow", "rain", "sun", "star", "day", "night", "week", "year",
        "soft", "hard", "free", "full", "half", "high", "low", "long", "short", "black", "white", "blue",
        "green", "red", "gold", "user", "data", "code", "file", "web", "net", "cloud", "key", "lock", "pass",
        "word", "type", "line", "page", "view", "port", "host", "name", "space", "place", "side", "light",
        "house", "room", "shop", "store", "any", "every", "some", "no"
    };

    private static readonly HashSet<string> EnCompoundRightStems = new(StringComparer.Ordinal)
    {
        "work", "time", "land", "wood", "stone", "field", "town", "port", "bridge", "gate", "wall", "way",
        "ward", "wise", "like", "less", "full", "ness", "ship", "hood", "ment", "able", "line", "base",
        "name", "word", "page", "site", "room", "house", "shop", "store", "book", "box", "board", "ware",
        "flow", "set", "lock", "key", "code", "file", "type", "view", "host", "space", "place", "side",
        "light", "man", "men", "woman", "women", "boy", "girl", "body", "thing", "where", "one", "screen", "stroke"
    };

    private static readonly HashSet<string> EnShortPrepositions = new(StringComparer.Ordinal)
    {
        "on", "in", "to", "of", "at", "by", "as", "or", "if", "so", "we", "he", "it", "my", "is", "be", "do", "go"
    };

    private static readonly HashSet<string> EnCommonShortTargets = new(StringComparer.Ordinal)
    {
        "the", "you", "not", "all", "one", "our", "out", "him", "her", "his", "its", "now", "how", "who",
        "this", "that", "them", "they", "what", "when", "here", "there", "your", "have", "will", "been"
    };

    public static SplitCandidate? TrySplitRussian(
        LexiconDatabase lexicon,
        string typedLower,
        RuWordEntry? prevWord)
    {
        if (typedLower.Length < 4 || typedLower.Length > 30)
        {
            return null;
        }

        if (lexicon.IsKnownRuWord(typedLower) || lexicon.IsProtectedWord(typedLower))
        {
            return null;
        }

        // 1. Productive missing-hyphen particles: -нибудь, -либо, interrogative + -то, кое- + interrogative
        if (typedLower.EndsWith("нибудь", StringComparison.Ordinal) && typedLower.Length > 8)
        {
            string stem = typedLower[..^6];
            if (lexicon.IsKnownRuWord(stem))
            {
                return new SplitCandidate($"{stem}-нибудь", 7.20f, HasBigram: true);
            }
        }

        if (typedLower.EndsWith("либо", StringComparison.Ordinal) && typedLower.Length > 6)
        {
            string stem = typedLower[..^4];
            if (lexicon.IsKnownRuWord(stem))
            {
                return new SplitCandidate($"{stem}-либо", 7.10f, HasBigram: true);
            }
        }

        if (typedLower.EndsWith("то", StringComparison.Ordinal) && typedLower.Length >= 5)
        {
            string stem = typedLower[..^2];
            if (RuInterrogativeToStems.Contains(stem))
            {
                return new SplitCandidate($"{stem}-то", 7.10f, HasBigram: true);
            }
        }

        if (typedLower.StartsWith("кое", StringComparison.Ordinal) && typedLower.Length >= 6)
        {
            string tail = typedLower[3..];
            if (RuInterrogativeToStems.Contains(tail))
            {
                return new SplitCandidate($"кое-{tail}", 7.00f, HasBigram: true);
            }
        }

        // 2. Two-word missing-space split
        SplitCandidate? twoWord = FindBestRussianTwoWordSplit(lexicon, typedLower, prevWord);
        if (twoWord.HasValue)
        {
            return twoWord;
        }

        // 3. Three-word missing-space split for common phrases ("янезнаю" -> "я не знаю", "насамомделе" -> "на самом деле")
        if (typedLower.Length >= 7 && typedLower.Length <= 24)
        {
            SplitCandidate? bestThree = null;
            int maxFirst = Math.Min(7, typedLower.Length - 5);

            for (int k = 1; k <= maxFirst; k++)
            {
                string first = typedLower[..k];
                string rest = typedLower[k..];

                if (!lexicon.TryGetRuWord(first, out RuWordEntry eFirst) || eFirst.Zipf < 3.60f)
                {
                    continue;
                }

                if (k == 1 && first is not ("я" or "и" or "а" or "в" or "с" or "у" or "к" or "о"))
                {
                    continue;
                }

                if (RuCompoundLeftStems.Contains(first))
                {
                    continue;
                }

                if (lexicon.TryGetExplicitFix(rest, out string? explicitRest) && explicitRest.Contains(' '))
                {
                    string secondWord = explicitRest[..explicitRest.IndexOf(' ')];
                    if (lexicon.TryGetRuWord(secondWord, out RuWordEntry eSec))
                    {
                        float b12 = lexicon.GetRuSurfaceBigramBoost(in eFirst, in eSec);
                        if (k >= 2 || b12 >= 0.55f || first == "я" && secondWord == "не")
                        {
                            float sc = eFirst.Zipf * 0.5f + 3.2f + b12;
                            if (!bestThree.HasValue || sc > bestThree.Value.Score)
                            {
                                bestThree = new SplitCandidate($"{first} {explicitRest}", sc, HasBigram: true);
                            }
                        }
                    }
                    continue;
                }

                SplitCandidate? restSplit = FindBestRussianTwoWordSplit(lexicon, rest, eFirst);
                if (restSplit.HasValue && restSplit.Value.HasBigram && restSplit.Value.Score >= 4.40f)
                {
                    int sp = restSplit.Value.Text.IndexOf(' ');
                    if (sp > 0)
                    {
                        string secondWord = restSplit.Value.Text[..sp];
                        if (lexicon.TryGetRuWord(secondWord, out RuWordEntry eSec))
                        {
                            float b12 = lexicon.GetRuSurfaceBigramBoost(in eFirst, in eSec);
                            bool allowFirst = k >= 3
                                ? (eFirst.Zipf >= 4.20f && b12 >= 0.55f)
                                : (b12 >= 0.65f || (first == "я" && secondWord == "не") || (first is "у" or "с" or "в" or "к" && RuObliquePronouns.Contains(secondWord)));

                            if (allowFirst)
                            {
                                float sc = (eFirst.Zipf + restSplit.Value.Score) * 0.5f + b12 * 0.5f;
                                if (!bestThree.HasValue || sc > bestThree.Value.Score)
                                {
                                    bestThree = new SplitCandidate($"{first} {restSplit.Value.Text}", sc, HasBigram: true);
                                }
                            }
                        }
                    }
                }
            }

            if (bestThree.HasValue)
            {
                return bestThree;
            }
        }

        return null;
    }

    private static SplitCandidate? FindBestRussianTwoWordSplit(
        LexiconDatabase lexicon,
        string typedLower,
        RuWordEntry? prevWord)
    {
        SplitCandidate? best = null;

        for (int k = 1; k < typedLower.Length; k++)
        {
            string left = typedLower[..k];
            string right = typedLower[k..];
            if (right.Length < 2)
            {
                continue;
            }

            if (!lexicon.TryGetRuWord(left, out RuWordEntry el) ||
                !lexicon.TryGetRuWord(right, out RuWordEntry er))
            {
                continue;
            }

            float surfBi = lexicon.GetRuSurfaceBigramBoost(in el, in er);
            float fullBi = lexicon.GetRuContextBoost(in el, in er);
            float prevBoost = prevWord.HasValue ? lexicon.GetRuContextBoost(prevWord.Value, in el) * 0.35f : 0f;

            if (RuCompoundLeftStems.Contains(left) && surfBi < 1.25f)
            {
                continue;
            }

            // Case 1: "не" + verb / pronoun / separate predicate
            if (left == "не")
            {
                if (er.Zipf >= 2.25f && (RuSeparateAfterNe.Contains(right) || LooksLikeRussianVerb(right, er.Flags)))
                {
                    float sc = (el.Zipf + er.Zipf) * 0.5f + MathF.Max(fullBi, 1.10f) + prevBoost;
                    if (!best.HasValue || sc > best.Value.Score)
                    {
                        best = new SplitCandidate($"{left} {right}", sc, HasBigram: true);
                    }
                }
                continue;
            }

            // Case 2: 1-letter left ('в', 'с', 'к', 'у', 'о', 'и', 'а', 'я')
            if (left.Length == 1)
            {
                if (right.Length < 3)
                {
                    continue;
                }

                bool ok = false;
                if (left is "у" or "к" or "с" or "в" or "о" && RuObliquePronouns.Contains(right))
                {
                    ok = true;
                }
                else if (surfBi >= 0.70f && er.Zipf >= 3.40f)
                {
                    ok = !(left is "в" or "с" or "о" or "у" && LooksLikeRussianVerb(right, er.Flags));
                }
                else if (left == "я" && er.Zipf >= 3.80f &&
                         (LooksLikeRussianVerb(right, er.Flags) ||
                          right is "тут" or "здесь" or "там" or "уже" or "еще" or "ещё" or "тоже" or "просто" or "очень"))
                {
                    ok = true;
                }
                else if (left is "и" or "а" && er.Zipf >= 4.50f &&
                         right is "это" or "все" or "всё" or "что" or "кто" or "как" or "где" or "там" or "тут" or "он" or "она" or "они" or "мы" or "вы" or "ты")
                {
                    ok = true;
                }

                if (ok)
                {
                    float sc = (el.Zipf + er.Zipf) * 0.5f + MathF.Max(fullBi, 0.80f) - 0.25f + prevBoost;
                    if (!best.HasValue || sc > best.Value.Score)
                    {
                        best = new SplitCandidate($"{left} {right}", sc, HasBigram: surfBi >= 0.55f || ok);
                    }
                }
                continue;
            }

            // Case 3: 2-letter left
            if (left.Length == 2)
            {
                if (RuTwoCharPrefixes.Contains(left))
                {
                    if (LooksLikeRussianVerb(right, er.Flags))
                    {
                        continue;
                    }

                    bool allowedObj =
                        surfBi >= 0.65f ||
                        RuObliquePronouns.Contains(right) ||
                        (left == "до" && right == "свидания") ||
                        (left == "по" && right is "поводу" or "умолчанию" or "ошибке" or "сути");

                    if (allowedObj && er.Zipf >= 3.00f)
                    {
                        float sc = (el.Zipf + er.Zipf) * 0.5f + MathF.Max(fullBi, 0.85f) - 0.15f + prevBoost;
                        if (!best.HasValue || sc > best.Value.Score)
                        {
                            best = new SplitCandidate($"{left} {right}", sc, HasBigram: true);
                        }
                    }
                    continue;
                }

                bool nonPfxOk =
                    surfBi >= 0.65f ||
                    (left is "он" or "мы" or "вы" or "ты" or "но" or "да" or "ну" &&
                     (right == "не" || (right.Length >= 3 && er.Zipf >= 4.20f && LooksLikeRussianVerb(right, er.Flags))));

                if (nonPfxOk)
                {
                    float sc = (el.Zipf + er.Zipf) * 0.5f + MathF.Max(fullBi, 0.75f) - 0.20f + prevBoost;
                    if (!best.HasValue || sc > best.Value.Score)
                    {
                        best = new SplitCandidate($"{left} {right}", sc, HasBigram: true);
                    }
                }
                continue;
            }

            // Case 4: right is 2 letters (left.Length >= 3)
            if (right.Length == 2)
            {
                if (right == "то")
                {
                    continue;
                }

                if (right == "ли")
                {
                    if (surfBi >= 0.65f || left is "вряд" or "навряд" or "едва" or "чуть")
                    {
                        float sc = (el.Zipf + er.Zipf) * 0.5f + MathF.Max(fullBi, 0.90f) + prevBoost;
                        if (!best.HasValue || sc > best.Value.Score)
                        {
                            best = new SplitCandidate($"{left} {right}", sc, HasBigram: true);
                        }
                    }
                    continue;
                }

                if (right is "не" or "же" or "бы" && el.Zipf >= 4.00f)
                {
                    float sc = (el.Zipf + er.Zipf) * 0.5f + fullBi * 0.85f - 0.15f + prevBoost;
                    if (!best.HasValue || sc > best.Value.Score)
                    {
                        best = new SplitCandidate($"{left} {right}", sc, HasBigram: fullBi >= 0.55f);
                    }
                }
                continue;
            }

            // Case 5: Both left.Length >= 3 and right.Length >= 3
            if (RuLongVerbPrefixes.Contains(left) && LooksLikeRussianVerb(right, er.Flags))
            {
                continue;
            }

            float minZ = MathF.Min(el.Zipf, er.Zipf);
            float avgZ = (el.Zipf + er.Zipf) * 0.5f;
            float pairSc = avgZ + fullBi * 0.85f + prevBoost;

            bool twoFullOk = fullBi >= 0.55f
                ? minZ >= 2.15f
                : (left.Length == 3 || right.Length == 3)
                    ? (minZ >= 3.35f && pairSc >= 4.10f)
                    : (minZ >= 2.20f && pairSc >= 3.25f);

            if (twoFullOk && (!best.HasValue || pairSc > best.Value.Score))
            {
                best = new SplitCandidate($"{left} {right}", pairSc, HasBigram: fullBi >= 0.55f);
            }
        }

        return best;
    }

    public static SplitCandidate? TrySplitEnglish(
        LexiconDatabase lexicon,
        string typedLower,
        EnWordEntry? prevWord)
    {
        if (typedLower.Length < 4 || typedLower.Length > 26)
        {
            return null;
        }

        if (lexicon.IsKnownEnWord(typedLower) || lexicon.IsProtectedWord(typedLower))
        {
            return null;
        }

        // Check 2-word split (including contraction on left/right like "dontknow" -> "don't know")
        SplitCandidate? twoWord = FindBestEnglishTwoWordSplit(lexicon, typedLower, prevWord);
        if (twoWord.HasValue)
        {
            return twoWord;
        }

        // Check 3-word split for "idontknow" -> "I don't know", "ontheother" -> "on the other"
        if (typedLower.Length >= 7 && typedLower.Length <= 20)
        {
            if (typedLower.StartsWith("i", StringComparison.Ordinal))
            {
                string rest = typedLower[1..];
                SplitCandidate? restSplit = FindBestEnglishTwoWordSplit(lexicon, rest, null);
                if (restSplit.HasValue && restSplit.Value.Score >= 4.80f &&
                    (rest.StartsWith("dont", StringComparison.Ordinal) ||
                     rest.StartsWith("cant", StringComparison.Ordinal) ||
                     rest.StartsWith("wont", StringComparison.Ordinal) ||
                     rest.StartsWith("didnt", StringComparison.Ordinal) ||
                     rest.StartsWith("have", StringComparison.Ordinal) ||
                     rest.StartsWith("will", StringComparison.Ordinal) ||
                     rest.StartsWith("need", StringComparison.Ordinal) ||
                     rest.StartsWith("want", StringComparison.Ordinal) ||
                     rest.StartsWith("think", StringComparison.Ordinal) ||
                     rest.StartsWith("know", StringComparison.Ordinal)))
                {
                    return new SplitCandidate($"I {restSplit.Value.Text}", restSplit.Value.Score + 0.40f, HasBigram: true);
                }
            }
        }

        return null;
    }

    private static SplitCandidate? FindBestEnglishTwoWordSplit(
        LexiconDatabase lexicon,
        string typedLower,
        EnWordEntry? prevWord)
    {
        SplitCandidate? best = null;

        for (int k = 1; k < typedLower.Length; k++)
        {
            string left = typedLower[..k];
            string right = typedLower[k..];
            if (right.Length < 2)
            {
                continue;
            }

            string? leftExpanded = null;
            float leftZipf = 0f;
            EnWordEntry el = default;
            bool hasEl = lexicon.TryGetEnWord(left, out el);
            if (hasEl)
            {
                leftExpanded = left;
                leftZipf = el.Zipf;
            }
            else if (lexicon.TryGetExplicitFix(left, out string? expL) && expL.Contains('\''))
            {
                leftExpanded = expL;
                leftZipf = 5.60f;
            }

            if (leftExpanded is null)
            {
                continue;
            }

            if (!lexicon.TryGetEnWord(right, out EnWordEntry er))
            {
                continue;
            }

            if (EnCompoundLeftStems.Contains(left) || EnCompoundRightStems.Contains(right))
            {
                continue;
            }

            float bi = hasEl ? lexicon.GetEnContextBoost(in el, in er) : 0.85f;
            float prevBoost = prevWord.HasValue && hasEl ? lexicon.GetEnContextBoost(prevWord.Value, in el) * 0.30f : 0f;

            if (left.Length == 1)
            {
                if (left == "a" && right == "lot")
                {
                    return new SplitCandidate("a lot", 6.80f, HasBigram: true);
                }
                if (left == "i" && (leftExpanded != left || right is "am" or "have" or "will" or "can" or "need" or "want" or "think" or "know"))
                {
                    float sc = 5.80f + bi * 0.5f;
                    if (!best.HasValue || sc > best.Value.Score)
                    {
                        best = new SplitCandidate($"I {right}", sc, HasBigram: true);
                    }
                }
                continue;
            }

            if (left.Length == 2 || right.Length == 2)
            {
                bool shortOk =
                    bi >= 0.70f ||
                    (EnShortPrepositions.Contains(left) && EnCommonShortTargets.Contains(right)) ||
                    (right is "be" or "to" or "in" or "on" or "it" or "me" or "us" && leftZipf >= 5.20f && bi >= 0.55f);

                if (shortOk && leftZipf >= 4.20f && er.Zipf >= 4.20f)
                {
                    float sc = (leftZipf + er.Zipf) * 0.5f + MathF.Max(bi, 0.75f) - 0.20f + prevBoost;
                    if (!best.HasValue || sc > best.Value.Score)
                    {
                        best = new SplitCandidate($"{leftExpanded} {right}", sc, HasBigram: true);
                    }
                }
                continue;
            }

            float minZ = MathF.Min(leftZipf, er.Zipf);
            float avgZ = (leftZipf + er.Zipf) * 0.5f;
            float pairSc = avgZ + bi * 0.85f + prevBoost;

            bool fullOk = bi >= 0.55f
                ? minZ >= 3.20f
                : (minZ >= 3.85f && pairSc >= 4.75f);

            if (fullOk && (!best.HasValue || pairSc > best.Value.Score))
            {
                best = new SplitCandidate($"{leftExpanded} {right}", pairSc, HasBigram: bi >= 0.55f);
            }
        }

        return best;
    }

    private static bool LooksLikeRussianVerb(string w, RuGrammarFlags flags)
    {
        if ((flags & (RuGrammarFlags.Infinitive | RuGrammarFlags.Finite3Per)) != 0)
        {
            return true;
        }

        ReadOnlySpan<string> unambiguousVerbSuffixes =
        [
            "ться", "тся", "лся", "лась", "лось", "лись",
            "ешь", "ёшь", "ете", "ёте", "ишь", "ите", "ут", "ют", "ат", "ят",
            "ала", "яла", "ила", "ела", "ула", "ало", "яло", "ило", "ело",
            "али", "яли", "или", "ели", "ули", "ыли", "ть", "ти", "чь"
        ];

        for (int i = 0; i < unambiguousVerbSuffixes.Length; i++)
        {
            string suf = unambiguousVerbSuffixes[i];
            if (w.Length >= suf.Length + 2 && w.EndsWith(suf, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (w.Length >= 4 &&
            !w.EndsWith("ому", StringComparison.Ordinal) &&
            !w.EndsWith("ему", StringComparison.Ordinal) &&
            (w.EndsWith("ет", StringComparison.Ordinal) ||
             w.EndsWith("ит", StringComparison.Ordinal) ||
             w.EndsWith("ем", StringComparison.Ordinal) ||
             w.EndsWith("им", StringComparison.Ordinal) ||
             w.EndsWith("ал", StringComparison.Ordinal) ||
             w.EndsWith("ил", StringComparison.Ordinal) ||
             w.EndsWith("ел", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }
}
