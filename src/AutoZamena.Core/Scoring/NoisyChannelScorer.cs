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
    private const float Lambda = 3.35f;

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

        var candidateIndices = new List<int>(64);
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

            // Never truncate a 3-letter token (e.g. abbreviation "сво", "кпд", "мгу") into a 2-letter preposition
            if (len == 3 && entry.Word.Length < 3)
            {
                continue;
            }

            // Do not merely force 'ё' onto valid 'е' words; handled upstream by IsKnownRuWord
            float editCost = ComputePhysicalEditCost(typedLower, entry.Word, keystrokes, maxEditCost);
            if (editCost > maxEditCost)
            {
                continue;
            }

            float contextBoost = prevWord.HasValue
                ? lexicon.GetRuContextBoost(prevWord.Value, in entry)
                : 0f;

            float score = entry.Zipf + contextBoost - (Lambda * editCost);
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

        var candidateIndices = new List<int>(64);
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

            if (len == 3 && entry.Word.Length < 3)
            {
                continue;
            }

            float editCost = ComputePhysicalEditCost(typedLower, entry.Word, keystrokes, maxEditCost);
            if (editCost > maxEditCost)
            {
                continue;
            }

            float contextBoost = prevWord.HasValue
                ? lexicon.GetEnContextBoost(prevWord.Value, in entry)
                : 0f;

            float score = entry.Zipf + contextBoost - (Lambda * editCost);
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

            if (rowMin > maxAbortCost + 0.01f)
            {
                return Inf;
            }

            // Rotate rows: prev2 <- prev1, prev1 <- curr
            prev1.CopyTo(prev2);
            curr.CopyTo(prev1);
        }

        return prev1[m];
    }

    private static bool CharsEqualOrYo(char a, char b) =>
        a == b || (a == 'е' && b == 'ё') || (a == 'ё' && b == 'е');

    private static bool IsRussianPhoneticConfusionPair(char a, char b) =>
        (a == 'а' && b == 'о') || (a == 'о' && b == 'а') ||
        (a == 'е' && b == 'и') || (a == 'и' && b == 'е') ||
        (a == 'д' && b == 'т') || (a == 'т' && b == 'д') ||
        (a == 'з' && b == 'с') || (a == 'с' && b == 'з') ||
        (a == 'б' && b == 'п') || (a == 'п' && b == 'б');

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
        float baseCost = dist <= 1.38f ? 0.65f
                       : (!isFirstChar && IsRussianPhoneticConfusionPair(typedChar, candChar)) ? 0.85f
                       : dist <= 2.15f ? 1.05f
                       : 1.55f;

        if (isFirstChar)
        {
            baseCost += dist <= 1.38f ? 0.25f : 0.60f;
        }

        return baseCost;
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

        return index == 0 ? 1.45f : 1.25f;
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
            return 1.40f;
        }

        return 0.95f;
    }

    private static float GetMaxEditCost(int len, SensitivityLevel sensitivity, bool isMidSentenceCapitalized)
    {
        if (isMidSentenceCapitalized)
        {
            return 0.70f;
        }

        float baseMax = len switch
        {
            <= 3 => 0.72f,
            4 => 0.92f,
            <= 6 => 1.35f,
            _ => 1.78f
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
            <= 3 => 2.65f,
            4 => 2.15f,
            <= 8 => 1.65f,
            _ => 1.05f
        };

        if (isMidSentenceCapitalized)
        {
            baseMin += 0.85f;
        }

        return sensitivity switch
        {
            SensitivityLevel.Conservative => baseMin + 0.45f,
            SensitivityLevel.Aggressive => baseMin - 0.35f,
            _ => baseMin
        };
    }
}
