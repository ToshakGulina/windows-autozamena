using AutoZamena.Core.Layout;

namespace AutoZamena.Core.Engine;

public enum CasingKind
{
    Lower = 0,
    Capitalized = 1,
    StickyDoubleCaps = 2,
    AllCaps = 3,
    MixedCamel = 4
}

public static class TokenGuards
{
    private static readonly HashSet<string> StickyCapsUnitsAllowlist = new(StringComparer.Ordinal)
    {
        "МГц", "ГГц", "кГц", "ТГц", "МБайт", "ГБайт", "кБайт", "мАч", "кВт", "МВт", "ГВт"
    };

    public static bool ContainsDisqualifyingSymbolOrDigit(ReadOnlySpan<char> span)
    {
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (char.IsDigit(c))
            {
                return true;
            }

            if (c is '@' or '/' or '\\' or '_' or '#' or '$' or '%' or '^' or '*' or '+' or '=' or '|')
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Detects intentional expressive chat stretches of 3+ identical consecutive letters
    /// (e.g. "ммм", "дааа", "нееет", "ооочень", "nooo", "hmmm") so they are never "corrected" into dry words.
    /// Accidental 2-letter double taps ("приввет") return false and are still fixed.
    /// </summary>
    public static bool HasExpressiveLetterRepeat(ReadOnlySpan<char> span)
    {
        if (span.Length < 3)
        {
            return false;
        }

        int run = 1;
        char prev = char.ToLowerInvariant(span[0]);
        for (int i = 1; i < span.Length; i++)
        {
            char cur = char.ToLowerInvariant(span[i]);
            if (cur == prev && char.IsLetter(cur))
            {
                run++;
                if (run >= 3)
                {
                    return true;
                }
            }
            else
            {
                run = 1;
                prev = cur;
            }
        }

        return false;
    }

    public static bool LooksLikeUrlOrDomain(ReadOnlySpan<char> rawSpan)
    {
        if (rawSpan.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            rawSpan.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int dotIdx = rawSpan.LastIndexOf('.');
        if (dotIdx > 0 && dotIdx < rawSpan.Length - 1)
        {
            ReadOnlySpan<char> tld = rawSpan[(dotIdx + 1)..];
            if (tld.Equals("ru", StringComparison.OrdinalIgnoreCase) ||
                tld.Equals("com", StringComparison.OrdinalIgnoreCase) ||
                tld.Equals("org", StringComparison.OrdinalIgnoreCase) ||
                tld.Equals("net", StringComparison.OrdinalIgnoreCase) ||
                tld.Equals("io", StringComparison.OrdinalIgnoreCase) ||
                tld.Equals("dev", StringComparison.OrdinalIgnoreCase) ||
                tld.Equals("ai", StringComparison.OrdinalIgnoreCase) ||
                tld.Equals("app", StringComparison.OrdinalIgnoreCase) ||
                tld.Equals("exe", StringComparison.OrdinalIgnoreCase) ||
                tld.Equals("dll", StringComparison.OrdinalIgnoreCase) ||
                tld.Equals("md", StringComparison.OrdinalIgnoreCase) ||
                tld.Equals("json", StringComparison.OrdinalIgnoreCase) ||
                tld.Equals("cs", StringComparison.OrdinalIgnoreCase) ||
                tld.Equals("py", StringComparison.OrdinalIgnoreCase) ||
                tld.Equals("js", StringComparison.OrdinalIgnoreCase) ||
                tld.Equals("ts", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static CasingKind ClassifyCasing(ReadOnlySpan<char> word)
    {
        if (word.IsEmpty)
        {
            return CasingKind.Lower;
        }

        int upperCount = 0;
        int lowerCount = 0;
        for (int i = 0; i < word.Length; i++)
        {
            char c = word[i];
            if (char.IsUpper(c)) upperCount++;
            else if (char.IsLower(c)) lowerCount++;
        }

        if (upperCount == 0)
        {
            return CasingKind.Lower;
        }

        if (lowerCount == 0 && upperCount >= 2)
        {
            return CasingKind.AllCaps;
        }

        if (upperCount == 1 && char.IsUpper(word[0]))
        {
            return CasingKind.Capitalized;
        }

        // Check XXxxxx pattern (sticky Shift at start of word: ПРивет, ЧТо)
        if (word.Length >= 3 &&
            upperCount == 2 &&
            char.IsUpper(word[0]) &&
            char.IsUpper(word[1]))
        {
            string str = word.ToString();
            if (!StickyCapsUnitsAllowlist.Contains(str))
            {
                return CasingKind.StickyDoubleCaps;
            }
        }

        return CasingKind.MixedCamel;
    }

    public static bool IsPureCyrillicWord(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty) return false;
        for (int i = 0; i < span.Length; i++)
        {
            if (!KeyboardGeometry.IsCyrillicLetter(span[i]))
            {
                return false;
            }
        }
        return true;
    }

    public static bool IsPureLatinWord(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty) return false;
        for (int i = 0; i < span.Length; i++)
        {
            if (!KeyboardGeometry.IsLatinLetter(span[i]))
            {
                return false;
            }
        }
        return true;
    }

    public static bool IsWellFormedHyphenatedWord(ReadOnlySpan<char> span, out bool isCyrillic, out bool isLatin)
    {
        isCyrillic = false;
        isLatin = false;
        if (span.Length < 3 || span[0] == '-' || span[^1] == '-')
        {
            return false;
        }

        bool sawHyphen = false;
        bool prevHyphen = false;
        bool allCyr = true;
        bool allLat = true;

        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (c == '-')
            {
                if (prevHyphen)
                {
                    return false;
                }
                sawHyphen = true;
                prevHyphen = true;
                continue;
            }

            prevHyphen = false;
            if (!KeyboardGeometry.IsCyrillicLetter(c)) allCyr = false;
            if (!KeyboardGeometry.IsLatinLetter(c)) allLat = false;
            if (!allCyr && !allLat)
            {
                return false;
            }
        }

        if (!sawHyphen)
        {
            return false;
        }

        isCyrillic = allCyr;
        isLatin = allLat;
        return allCyr || allLat;
    }

    public static string ApplyCasing(string replacementLower, CasingKind originalCasing, bool forceCapitalize)
    {
        if (string.IsNullOrEmpty(replacementLower))
        {
            return replacementLower;
        }

        bool shouldCapitalize = forceCapitalize ||
                                originalCasing == CasingKind.Capitalized ||
                                originalCasing == CasingKind.StickyDoubleCaps;

        if (!shouldCapitalize)
        {
            return replacementLower;
        }

        return string.Concat(char.ToUpperInvariant(replacementLower[0]).ToString(), replacementLower.AsSpan(1));
    }
}
