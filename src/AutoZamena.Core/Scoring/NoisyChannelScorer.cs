using System;
using System.Collections.Generic;
using AutoZamena.Core.Layout;
using AutoZamena.Core.Lexicon;
using AutoZamena.Core.Models;

namespace AutoZamena.Core.Scoring;

public readonly record struct ScoredCandidate(
    string Word,
    float Score,
    float EditCost,
    float ContextBoost,
    uint LemmaId
);

/// <summary>
/// Noisy-channel physical keyboard + timing + contextual language model scorer.
/// </summary>
public static class NoisyChannelScorer
{
    private const float Lambda = 3.20f;

    public static ScoredCandidate? FindBestRuCandidate(
        LexiconDatabase lexicon,
        string typedLower,
        ReadOnlySpan<KeyStrokeEvent> keystrokes,
        RuWordEntry? prevWord,
        SensitivityLevel sensitivity,
        bool isMidSentenceCapitalized)
    {
        int len = typedLower.Length;
        if (len < 3)
        {
            return null;
        }

        int maxSymDist = len <= 4 ? 1 : 2;
        float maxEditCost = GetMaxEditCost(len, sensitivity, isMidSentenceCapitalized);
        float minScore = GetMinScore(len, sensitivity, isMidSentenceCapitalized);
        float lengthBonus = len >= 8 ? MathF.Min(0.55f, (len - 7) * 0.11f) : 0f;
        bool hasFastChord = HasAnyFastChord(keystrokes);

        var candidateIndices = new List<int>(128);
        lexicon.QueryRuCandidates(typedLower, maxSymDist, candidateIndices);
        if (candidateIndices.Count == 0)
        {
            return null;
        }

        ScoredCandidate? best = null;
        ScoredCandidate? secondDiffLemma = null;

        for (int idx = 0; idx < candidateIndices.Count; idx++)
        {
            RuWordEntry entry = lexicon.GetRuEntryByIndex(candidateIndices[idx]);
            if (string.Equals(entry.Word, typedLower, StringComparison.Ordinal))
            {
                continue;
            }

            float contextBoost = prevWord.HasValue
                ? lexicon.GetRuContextBoost(prevWord.Value, in entry)
                : 0f;

            float requiredMinScore = best.HasValue
                ? MathF.Max(minScore, best.Value.Score - 0.26f)
                : minScore;

            float scoreCeiling = entry.Zipf + lengthBonus + contextBoost;
            if (scoreCeiling < requiredMinScore)
            {
                continue;
            }

            // Never truncate a 3-letter token or expand a 3-letter abbreviation ("сво", "чат") without strong context
            if (len == 3 && entry.Word.Length != 3 && contextBoost < 1.0f)
            {
                continue;
            }

            // First-letter substitution on a 3-letter word requires a very common target (e.g. "шде" -> "где", "кще" -> "еще")
            if (len == 3 &&
                entry.Word.Length >= 2 &&
                entry.Word[0] != typedLower[0] &&
                !(entry.Word[0] == typedLower[1] && entry.Word[1] == typedLower[0]) &&
                entry.Zipf < 5.50f &&
                contextBoost < 1.0f)
            {
                continue;
            }

            // Lower-frequency targets (z < 2.75) only match clean single-key edits so rare words never pull 2-edit inputs
            float candMaxEditCost = entry.Zipf < 2.75f
                ? MathF.Min(maxEditCost, 0.88f)
                : maxEditCost;

            candMaxEditCost = MathF.Min(candMaxEditCost, (scoreCeiling - requiredMinScore) / Lambda);
            if (candMaxEditCost < 0.07f)
            {
                continue;
            }

            if (ComputeAdmissibleCostLowerBound(typedLower, entry.Word, hasFastChord) > candMaxEditCost + 0.005f)
            {
                continue;
            }

            float editCost = ComputePhysicalEditCost(typedLower, entry.Word, keystrokes, candMaxEditCost);
            if (editCost > candMaxEditCost)
            {
                continue;
            }

            float score = scoreCeiling - (Lambda * editCost);
            if (score < minScore)
            {
                continue;
            }

            var candidate = new ScoredCandidate(entry.Word, score, editCost, contextBoost, entry.LemmaId);

            if (!best.HasValue || candidate.Score > best.Value.Score)
            {
                if (best.HasValue && best.Value.LemmaId != candidate.LemmaId)
                {
                    secondDiffLemma = best;
                }
                best = candidate;
            }
            else if (candidate.LemmaId != best.Value.LemmaId &&
                     (!secondDiffLemma.HasValue || candidate.Score > secondDiffLemma.Value.Score))
            {
                secondDiffLemma = candidate;
            }
        }

        if (!best.HasValue)
        {
            return null;
        }

        // Ambiguity guard when two unrelated words are almost tied and edit wasn't an obvious chord/transposition
        if (secondDiffLemma.HasValue &&
            best.Value.ContextBoost < 0.5f &&
            best.Value.EditCost > 0.68f &&
            (best.Value.Score - secondDiffLemma.Value.Score) < 0.25f)
        {
            return null;
        }

        return best;
    }

    public static ScoredCandidate? FindBestEnCandidate(
        LexiconDatabase lexicon,
        string typedLower,
        ReadOnlySpan<KeyStrokeEvent> keystrokes,
        EnWordEntry? prevWord,
        SensitivityLevel sensitivity,
        bool isMidSentenceCapitalized)
    {
        int len = typedLower.Length;
        if (len < 3)
        {
            return null;
        }

        int maxSymDist = len <= 4 ? 1 : 2;
        float maxEditCost = GetMaxEditCost(len, sensitivity, isMidSentenceCapitalized);
        float minScore = GetMinScore(len, sensitivity, isMidSentenceCapitalized);
        float lengthBonus = len >= 8 ? MathF.Min(0.55f, (len - 7) * 0.11f) : 0f;
        bool hasFastChord = HasAnyFastChord(keystrokes);

        var candidateIndices = new List<int>(128);
        lexicon.QueryEnCandidates(typedLower, maxSymDist, candidateIndices);
        if (candidateIndices.Count == 0)
        {
            return null;
        }

        ScoredCandidate? best = null;
        ScoredCandidate? second = null;

        for (int idx = 0; idx < candidateIndices.Count; idx++)
        {
            EnWordEntry entry = lexicon.GetEnEntryByIndex(candidateIndices[idx]);
            if (string.Equals(entry.Word, typedLower, StringComparison.Ordinal))
            {
                continue;
            }

            float contextBoost = prevWord.HasValue
                ? lexicon.GetEnContextBoost(prevWord.Value, in entry)
                : 0f;

            float requiredMinScore = best.HasValue
                ? MathF.Max(minScore, best.Value.Score - 0.26f)
                : minScore;

            float scoreCeiling = entry.Zipf + lengthBonus + contextBoost;
            if (scoreCeiling < requiredMinScore)
            {
                continue;
            }

            if (len == 3 && entry.Word.Length != 3 && contextBoost < 1.0f)
            {
                continue;
            }

            if (len == 3 &&
                entry.Word.Length >= 2 &&
                entry.Word[0] != typedLower[0] &&
                !(entry.Word[0] == typedLower[1] && entry.Word[1] == typedLower[0]) &&
                entry.Zipf < 5.60f &&
                contextBoost < 1.0f)
            {
                continue;
            }

            float candMaxEditCost = entry.Zipf < 2.95f
                ? MathF.Min(maxEditCost, 0.88f)
                : maxEditCost;

            candMaxEditCost = MathF.Min(candMaxEditCost, (scoreCeiling - requiredMinScore) / Lambda);
            if (candMaxEditCost < 0.07f)
            {
                continue;
            }

            if (ComputeAdmissibleCostLowerBound(typedLower, entry.Word, hasFastChord) > candMaxEditCost + 0.005f)
            {
                continue;
            }

            float editCost = ComputePhysicalEditCost(typedLower, entry.Word, keystrokes, candMaxEditCost);
            if (editCost > candMaxEditCost)
            {
                continue;
            }

            float score = scoreCeiling - (Lambda * editCost);
            if (score < minScore)
            {
                continue;
            }

            var candidate = new ScoredCandidate(entry.Word, score, editCost, contextBoost, (uint)entry.Index);

            if (!best.HasValue || candidate.Score > best.Value.Score)
            {
                second = best;
                best = candidate;
            }
            else if (!second.HasValue || candidate.Score > second.Value.Score)
            {
                second = candidate;
            }
        }

        if (!best.HasValue)
        {
            return null;
        }

        if (second.HasValue &&
            best.Value.ContextBoost < 0.5f &&
            best.Value.EditCost > 0.68f &&
            (best.Value.Score - second.Value.Score) < 0.25f)
        {
            return null;
        }

        return best;
    }

    private static float ComputeAdmissibleCostLowerBound(string typed, string cand, bool hasFastChord)
    {
        int n = typed.Length;
        int m = cand.Length;
        int diff = Math.Abs(n - m);
        if (diff > 2)
        {
            return 99f;
        }

        int minLen = Math.Min(n, m);
        int pfx = 0;
        while (pfx < minLen && CharsEqualOrYo(typed[pfx], cand[pfx]))
        {
            pfx++;
        }

        int sfx = 0;
        int maxSfx = minLen - pfx;
        while (sfx < maxSfx && CharsEqualOrYo(typed[n - 1 - sfx], cand[m - 1 - sfx]))
        {
            sfx++;
        }

        int remT = n - pfx - sfx;
        int remC = m - pfx - sfx;
        int span = Math.Max(remT, remC);

        if (span == 0)
        {
            return 0f;
        }

        if (span == 1)
        {
            if (remT == 1 && remC == 1)
            {
                return 0.65f;
            }
            if (remT == 1)
            {
                return hasFastChord ? 0.35f : 0.48f;
            }
            return cand.EndsWith("ться", StringComparison.Ordinal) ? 0.28f : 0.45f;
        }

        if (remT == 2 && remC == 2 &&
            CharsEqualOrYo(typed[pfx], cand[pfx + 1]) &&
            CharsEqualOrYo(typed[pfx + 1], cand[pfx]))
        {
            return 0.36f;
        }

        // At least 2 independent non-'ё' edits
        float firstMin = cand.EndsWith("ться", StringComparison.Ordinal)
            ? 0.28f
            : (hasFastChord ? 0.35f : 0.45f);
        float secondMin = hasFastChord ? 0.35f : 0.45f;
        float bound = firstMin + secondMin;
        if (n - m >= 2 && !hasFastChord)
        {
            bound += 0.55f;
        }
        return bound;
    }

    /// <summary>
    /// Computes weighted restricted Damerau-Levenshtein cost incorporating physical keyboard geometry
    /// and millisecond inter-key timing. Returns a value &gt; maxAbortCost if pruned.
    /// </summary>
    public static float ComputePhysicalEditCost(
        ReadOnlySpan<char> typed,
        ReadOnlySpan<char> cand,
        ReadOnlySpan<KeyStrokeEvent> events,
        float maxAbortCost = 2.5f)
    {
        int n = typed.Length;
        int m = cand.Length;
        const float Inf = 99f;

        if (Math.Abs(n - m) > 2 || m > 32 || n > 32)
        {
            return Inf;
        }

        int cols = m + 1;
        Span<float> prev2 = stackalloc float[cols];
        Span<float> prev1 = stackalloc float[cols];
        Span<float> curr = stackalloc float[cols];

        prev1[0] = 0f;
        for (int j = 1; j <= m; j++)
        {
            float delInTypedCost = GetMissingCharCost(cand, j - 1);
            prev1[j] = prev1[j - 1] + delInTypedCost;
        }

        float prevRowMin = 0f;

        for (int i = 1; i <= n; i++)
        {
            char tc = typed[i - 1];
            float extraFirstCost = GetExtraTypedCharCost(typed, i - 1, events);
            curr[0] = prev1[0] + extraFirstCost;
            float rowMin = curr[0];

            for (int j = 1; j <= m; j++)
            {
                char cc = cand[j - 1];

                // 1. Match or Substitution
                float subCost = GetSubstitutionCost(tc, cc, isFirstChar: i == 1 && j == 1);
                float best = prev1[j - 1] + subCost;

                // 2. Extra char in typed (fat-finger insertion in typed -> delete tc)
                float insTypedCost = GetExtraTypedCharCost(typed, i - 1, events);
                float vIns = prev1[j] + insTypedCost;
                if (vIns < best) best = vIns;

                // 3. Missing char in typed (user skipped cc)
                float missCost = GetMissingCharCost(cand, j - 1);
                float vMiss = curr[j - 1] + missCost;
                if (vMiss < best) best = vMiss;

                // 4. Adjacent transposition
                if (i >= 2 && j >= 2 &&
                    CharsEqualOrYo(typed[i - 2], cc) &&
                    CharsEqualOrYo(tc, cand[j - 2]))
                {
                    bool fastRollover = i - 1 < events.Length &&
                                        (events[i - 1].OverlapWithPrev || (events[i - 1].DeltaPrevMs > 0 && events[i - 1].DeltaPrevMs <= 50));
                    float transCost = fastRollover ? 0.36f : 0.45f;
                    float vTrans = prev2[j - 2] + transCost;
                    if (vTrans < best) best = vTrans;
                }

                curr[j] = best;
                if (best < rowMin) rowMin = best;
            }

            // Require two consecutive high-cost rows before aborting so 2-row transpositions are never pruned
            if (rowMin > maxAbortCost + 0.01f && prevRowMin > maxAbortCost + 0.01f)
            {
                return Inf;
            }

            prevRowMin = rowMin;
            prev1.CopyTo(prev2);
            curr.CopyTo(prev1);
        }

        float totalCost = prev1[m];

        // Guard against shrinking a 6+ letter word by 2 non-chord characters into a shorter common word ("малако" -> "мало")
        if (n - m >= 2 && !HasAnyFastChord(events))
        {
            totalCost += 0.55f;
        }

        return totalCost;
    }

    private static bool HasAnyFastChord(ReadOnlySpan<KeyStrokeEvent> events)
    {
        for (int i = 0; i < events.Length; i++)
        {
            if (events[i].OverlapWithPrev || (events[i].DeltaPrevMs > 0 && events[i].DeltaPrevMs <= 35))
            {
                return true;
            }
        }
        return false;
    }

    private static bool CharsEqualOrYo(char a, char b) =>
        a == b || (a == 'е' && b == 'ё') || (a == 'ё' && b == 'е');

    private static bool IsRussianVowelConfusionPair(char a, char b) =>
        (a == 'а' && b == 'о') || (a == 'о' && b == 'а') ||
        (a == 'е' && b == 'и') || (a == 'и' && b == 'е') ||
        (a == 'е' && b == 'я') || (a == 'я' && b == 'е') ||
        (a == 'а' && b == 'я') || (a == 'я' && b == 'а') ||
        (a == 'у' && b == 'ю') || (a == 'ю' && b == 'у') ||
        (a == 'ы' && b == 'и') || (a == 'и' && b == 'ы');

    private static bool IsRussianConsonantConfusionPair(char a, char b) =>
        (a == 'д' && b == 'т') || (a == 'т' && b == 'д') ||
        (a == 'з' && b == 'с') || (a == 'с' && b == 'з') ||
        (a == 'б' && b == 'п') || (a == 'п' && b == 'б') ||
        (a == 'в' && b == 'ф') || (a == 'ф' && b == 'в') ||
        (a == 'г' && b == 'к') || (a == 'к' && b == 'г') ||
        (a == 'г' && b == 'в') || (a == 'в' && b == 'г') ||
        (a == 'ч' && b == 'ш') || (a == 'ш' && b == 'ч') ||
        (a == 'щ' && b == 'ш') || (a == 'ш' && b == 'щ');

    private static float GetSubstitutionCost(char typedChar, char candChar, bool isFirstChar)
    {
        if (typedChar == candChar)
        {
            return 0f;
        }

        if ((typedChar == 'е' && candChar == 'ё') || (typedChar == 'ё' && candChar == 'е'))
        {
            return 0.08f;
        }

        float dist = KeyboardGeometry.GetKeyDistance(typedChar, candChar);
        if (dist <= 1.38f)
        {
            return isFirstChar ? 0.77f : 0.65f;
        }

        if (!isFirstChar)
        {
            if (IsRussianVowelConfusionPair(typedChar, candChar))
            {
                return 0.74f;
            }
            if (IsRussianConsonantConfusionPair(typedChar, candChar))
            {
                return 0.80f;
            }
            // Physical QWERTY 'O' key pressed for Russian 'о' ("чтщ" -> "что")
            if (typedChar == 'щ' && candChar == 'о')
            {
                return 0.78f;
            }
        }

        if (dist <= 2.15f)
        {
            return isFirstChar ? 1.37f : 1.02f;
        }

        return isFirstChar ? 2.05f : 1.50f;
    }

    private static float GetExtraTypedCharCost(
        ReadOnlySpan<char> typed,
        int index,
        ReadOnlySpan<KeyStrokeEvent> events)
    {
        char c = typed[index];

        // Accidental double tap of same key (приввет)
        if (index > 0 && typed[index - 1] == c)
        {
            return 0.48f;
        }

        bool adjPrev = index > 0 && KeyboardGeometry.AreAdjacentKeys(typed[index - 1], c);
        bool adjNext = index + 1 < typed.Length && KeyboardGeometry.AreAdjacentKeys(c, typed[index + 1]);

        if (adjPrev || adjNext)
        {
            bool fastChord = false;
            if (index < events.Length)
            {
                KeyStrokeEvent ev = events[index];
                if (ev.OverlapWithPrev || (ev.DeltaPrevMs > 0 && ev.DeltaPrevMs <= 35))
                {
                    fastChord = true;
                }
            }
            if (!fastChord && index + 1 < events.Length)
            {
                KeyStrokeEvent nextEv = events[index + 1];
                if (nextEv.OverlapWithPrev || (nextEv.DeltaPrevMs > 0 && nextEv.DeltaPrevMs <= 35))
                {
                    fastChord = true;
                }
            }

            return fastChord ? 0.35f : 0.62f;
        }

        return index == 0 ? 1.45f : 1.20f;
    }

    private static float GetMissingCharCost(ReadOnlySpan<char> cand, int candIndex)
    {
        char missing = cand[candIndex];

        // Missing soft/hard sign (especially -тся -> -ться)
        if (missing is 'ь' or 'ъ')
        {
            if (missing == 'ь' && cand.EndsWith("ться", StringComparison.Ordinal) && candIndex == cand.Length - 3)
            {
                return 0.28f;
            }
            return 0.45f;
        }

        // Missing double consonant/vowel (програма -> программа)
        if (candIndex > 0 && cand[candIndex - 1] == missing)
        {
            return 0.48f;
        }

        // Dropped final character right before Space (наза -> назад)
        if (candIndex == cand.Length - 1)
        {
            return 0.72f;
        }

        if (candIndex == 0)
        {
            return 1.35f;
        }

        return 0.88f;
    }

    private static float GetMaxEditCost(int len, SensitivityLevel sensitivity, bool isMidSentenceCapitalized)
    {
        if (isMidSentenceCapitalized)
        {
            return 0.70f;
        }

        float baseMax = len switch
        {
            <= 3 => 0.79f,
            4 => 0.95f,
            5 => 1.42f,
            6 => 1.52f,
            _ => 1.82f
        };

        return sensitivity switch
        {
            SensitivityLevel.Conservative => MathF.Max(0.66f, baseMax - 0.28f),
            SensitivityLevel.Aggressive => baseMax + 0.22f,
            _ => baseMax
        };
    }

    private static float GetMinScore(int len, SensitivityLevel sensitivity, bool isMidSentenceCapitalized)
    {
        float baseMin = len switch
        {
            <= 3 => 2.85f,
            4 => 2.05f,
            5 => 1.35f,
            <= 7 => 0.95f,
            <= 9 => 0.45f,
            _ => 0.15f
        };

        if (isMidSentenceCapitalized)
        {
            baseMin += 1.05f;
        }

        return sensitivity switch
        {
            SensitivityLevel.Conservative => baseMin + 0.45f,
            SensitivityLevel.Aggressive => baseMin - 0.35f,
            _ => baseMin
        };
    }
}
