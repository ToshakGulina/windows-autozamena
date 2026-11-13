using System;
using System.Collections.Generic;
using System.Text;
using AutoZamena.Core.Engine;
using AutoZamena.Core.Lexicon;
using AutoZamena.Core.Models;

namespace AutoZamena.Core.Layout;

/// <summary>
/// Smart whole-word text converters for selected text and snippets:
/// - RU &lt;-&gt; EN layout conversion with whole-word punctuation &amp; URL preservation (Caramba / Mahou style)
/// - Case cycling (КАПСЛОК -&gt; Обычный регистр -&gt; строчные -&gt; ПРОПИСНЫЕ)
/// - Bidirectional RU &lt;-&gt; Latin transliteration
/// - Dynamic snippet macro expansion ({{date}}, {{time}}, {{iso}}, {{clipboard}}, | cursor marker)
/// </summary>
public static class SmartTextConverter
{
    private static readonly (string RuLower, string EnLower, string EnTitle)[] RuToTranslitPairs =
    {
        ("щ", "shch", "Shch"),
        ("ш", "sh", "Sh"),
        ("ч", "ch", "Ch"),
        ("ж", "zh", "Zh"),
        ("ц", "ts", "Ts"),
        ("х", "kh", "Kh"),
        ("ё", "yo", "Yo"),
        ("ю", "yu", "Yu"),
        ("я", "ya", "Ya"),
        ("а", "a", "A"),
        ("б", "b", "B"),
        ("в", "v", "V"),
        ("г", "g", "G"),
        ("д", "d", "D"),
        ("е", "e", "E"),
        ("з", "z", "Z"),
        ("и", "i", "I"),
        ("й", "y", "Y"),
        ("к", "k", "K"),
        ("л", "l", "L"),
        ("м", "m", "M"),
        ("н", "n", "N"),
        ("о", "o", "O"),
        ("п", "p", "P"),
        ("р", "r", "R"),
        ("с", "s", "S"),
        ("т", "t", "T"),
        ("у", "u", "U"),
        ("ф", "f", "F"),
        ("ъ", "", ""),
        ("ы", "y", "Y"),
        ("ь", "", ""),
        ("э", "e", "E"),
    };

    private static readonly (string LatSeq, char CyrLower, char CyrUpper)[] TranslitToRuSeqs =
    {
        ("shch", 'щ', 'Щ'),
        ("sch", 'щ', 'Щ'),
        ("yo", 'ё', 'Ё'),
        ("zh", 'ж', 'Ж'),
        ("kh", 'х', 'Х'),
        ("ts", 'ц', 'Ц'),
        ("ch", 'ч', 'Ч'),
        ("sh", 'ш', 'Ш'),
        ("yu", 'ю', 'Ю'),
        ("ya", 'я', 'Я'),
        ("a", 'а', 'А'),
        ("b", 'б', 'Б'),
        ("v", 'в', 'В'),
        ("w", 'в', 'В'),
        ("g", 'г', 'Г'),
        ("d", 'д', 'Д'),
        ("e", 'е', 'Е'),
        ("z", 'з', 'З'),
        ("i", 'и', 'И'),
        ("j", 'й', 'Й'),
        ("k", 'к', 'К'),
        ("l", 'л', 'Л'),
        ("m", 'м', 'М'),
        ("n", 'н', 'Н'),
        ("o", 'о', 'О'),
        ("p", 'п', 'П'),
        ("r", 'р', 'Р'),
        ("s", 'с', 'С'),
        ("t", 'т', 'Т'),
        ("u", 'у', 'У'),
        ("f", 'ф', 'Ф'),
        ("h", 'х', 'Х'),
        ("c", 'ц', 'Ц'),
        ("y", 'ы', 'Ы'),
        ("q", 'к', 'К'),
        ("x", 'к', 'К'),
    };

    /// <summary>
    /// Converts a multi-word or multi-line selection between Russian and English keyboard layouts
    /// while preserving numbers, valid URLs, emojis, and resolving boundary punctuation cleanly.
    /// </summary>
    public static (string Converted, TargetLanguage TargetLanguage) ConvertSelectionLayout(
        string text,
        LexiconDatabase? lexicon = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (text, TargetLanguage.None);
        }

        lexicon ??= LexiconDatabase.Default;

        int cyrCount = 0;
        int latCount = 0;
        foreach (char c in text)
        {
            if (KeyboardGeometry.IsCyrillicLetter(c))
            {
                cyrCount++;
            }
            else if (KeyboardGeometry.IsLatinLetter(c) || KeyboardGeometry.IsEnKeyForRuLetter(c))
            {
                latCount++;
            }
        }

        if (cyrCount == 0 && latCount == 0)
        {
            return (text, TargetLanguage.None);
        }

        TargetLanguage targetLang = cyrCount > latCount
            ? TargetLanguage.English
            : TargetLanguage.Russian;

        var sb = new StringBuilder(text.Length);
        int idx = 0;
        while (idx < text.Length)
        {
            if (char.IsWhiteSpace(text[idx]))
            {
                sb.Append(text[idx]);
                idx++;
                continue;
            }

            int start = idx;
            while (idx < text.Length && !char.IsWhiteSpace(text[idx]))
            {
                idx++;
            }

            string chunk = text[start..idx];
            string nextNonSpace = PeekNextNonSpaceChunk(text, idx);
            sb.Append(ConvertSingleChunk(chunk, nextNonSpace, targetLang, lexicon));
        }

        string result = sb.ToString();
        return string.Equals(result, text, StringComparison.Ordinal)
            ? (text, TargetLanguage.None)
            : (result, targetLang);
    }

    /// <summary>
    /// Checks if a Cyrillic token typed in RU layout is actually a domain/URL meant in EN layout
    /// (e.g. "пщщпдуюсщь" -> "google.com", "цццюпшегиьюсщь" -> "www.github.com").
    /// </summary>
    public static bool TryConvertRuTypedDomainToEn(string rawToken, out string domainEn)
    {
        domainEn = string.Empty;
        if (rawToken.Length < 5 || !rawToken.Contains('ю') && !rawToken.Contains('Ю'))
        {
            return false;
        }

        int cyrCount = 0;
        foreach (char c in rawToken)
        {
            if (KeyboardGeometry.IsCyrillicLetter(c))
            {
                cyrCount++;
            }
            else if (KeyboardGeometry.IsLatinLetter(c))
            {
                return false;
            }
        }

        if (cyrCount < 4)
        {
            return false;
        }

        string mapped = KeyboardGeometry.ConvertRuToEn(rawToken);
        if (!TokenGuards.LooksLikeUrlOrDomain(mapped))
        {
            return false;
        }

        // Verify domain labels contain clean ASCII hostname characters (no stray brackets/quotes from х/ъ/э/ж)
        string hostPart = mapped;
        if (hostPart.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            hostPart = hostPart[7..];
        }
        else if (hostPart.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            hostPart = hostPart[8..];
        }

        int slashIdx = hostPart.IndexOf('/');
        if (slashIdx >= 0)
        {
            hostPart = hostPart[..slashIdx];
        }

        if (hostPart.Length < 4 || hostPart[0] == '.' || hostPart[^1] == '.' || hostPart.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char hc in hostPart)
        {
            bool validHostChar = (hc >= 'a' && hc <= 'z') ||
                                 (hc >= 'A' && hc <= 'Z') ||
                                 (hc >= '0' && hc <= '9') ||
                                 hc is '.' or '-';
            if (!validHostChar)
            {
                return false;
            }
        }

        domainEn = mapped;
        return true;
    }

    /// <summary>
    /// Cycles text casing:
    /// - ALL CAPS / Inverted / Sticky Caps -&gt; Sentence case ("ПРИВЕТ МИР" -&gt; "Привет мир")
    /// - lowercase -&gt; Title / Sentence case ("привет мир" -&gt; "Привет мир")
    /// - Sentence / Title case -&gt; ALL CAPS ("Привет мир" -&gt; "ПРИВЕТ МИР")
    /// </summary>
    public static string CycleCase(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        bool hasUpper = false;
        bool hasLower = false;
        foreach (char c in text)
        {
            if (char.IsLetter(c))
            {
                if (char.IsUpper(c)) hasUpper = true;
                else if (char.IsLower(c)) hasLower = true;
            }
        }

        if (!hasUpper && !hasLower)
        {
            return text;
        }

        // 1. ALL CAPS -> Sentence case (or lowercase if single letter)
        if (hasUpper && !hasLower)
        {
            return ToSentenceCase(text);
        }

        // 2. all lowercase -> Sentence case
        if (hasLower && !hasUpper)
        {
            return ToSentenceCase(text);
        }

        // 3. Mixed case: if it's inverted caps ("пРИВЕТ") or sticky caps ("ПРивет"), normalize to Sentence case first;
        //    if it's already clean Sentence case ("Привет мир"), cycle to UPPERCASE ("ПРИВЕТ МИР") -> next press will go lowercase via step 4!
        string sentence = ToSentenceCase(text);
        if (!string.Equals(text, sentence, StringComparison.Ordinal))
        {
            return sentence;
        }

        // Wait: to make a clean 3-state cycle (lowercase -> Sentence case -> UPPERCASE -> lowercase):
        // Check if first letter is upper and rest are lower (Sentence case) -> go UPPERCASE; Wait: then UPPERCASE goes to lowercase!
        return text.ToUpperInvariant();
    }

    /// <summary>
    /// Deterministic 3-step case cycle: lowercase -&gt; Sentence case -&gt; UPPERCASE -&gt; lowercase
    /// (with abnormal caps like пРИВЕТ / ПРивет normalizing straight to Sentence case).
    /// </summary>
    public static string CycleCaseThreeWay(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        bool hasUpper = false;
        bool hasLower = false;
        foreach (char c in text)
        {
            if (char.IsLetter(c))
            {
                if (char.IsUpper(c)) hasUpper = true;
                else if (char.IsLower(c)) hasLower = true;
            }
        }

        if (!hasUpper && !hasLower)
        {
            return text;
        }

        if (hasUpper && !hasLower)
        {
            return text.ToLowerInvariant();
        }

        if (hasLower && !hasUpper)
        {
            return ToSentenceCase(text);
        }

        string sentence = ToSentenceCase(text);
        if (!string.Equals(text, sentence, StringComparison.Ordinal))
        {
            return sentence;
        }

        return text.ToUpperInvariant();
    }

    /// <summary>
    /// Bidirectional transliteration: Cyrillic -&gt; Latin translit ("Привет" -&gt; "Privet"),
    /// or Latin translit -&gt; Cyrillic ("Privet" -&gt; "Привет") when input has no Cyrillic letters.
    /// </summary>
    public static string Transliterate(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        bool hasCyr = false;
        bool hasLat = false;
        foreach (char c in text)
        {
            if (KeyboardGeometry.IsCyrillicLetter(c)) hasCyr = true;
            else if (KeyboardGeometry.IsLatinLetter(c)) hasLat = true;
        }

        if (hasCyr)
        {
            return TransliterateRuToLat(text);
        }

        if (hasLat)
        {
            return TransliterateLatToRu(text);
        }

        return text;
    }

    /// <summary>
    /// Expands dynamic macros ({{date}}, {{time}}, {{iso}}, {{clipboard}}) and extracts '|' cursor position offset.
    /// </summary>
    public static string ExpandSnippetTemplate(
        string template,
        string? clipboardText,
        DateTime now,
        out int cursorMoveLeftCount)
    {
        cursorMoveLeftCount = 0;
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        string expanded = template;
        if (expanded.Contains("{{", StringComparison.Ordinal))
        {
            expanded = expanded
                .Replace("{{date}}", now.ToString("dd.MM.yyyy"), StringComparison.OrdinalIgnoreCase)
                .Replace("{{time}}", now.ToString("HH:mm"), StringComparison.OrdinalIgnoreCase)
                .Replace("{{iso}}", now.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
                .Replace("{{clipboard}}", clipboardText ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        int pipeIdx = expanded.IndexOf('|');
        if (pipeIdx >= 0 && expanded.IndexOf('|', pipeIdx + 1) < 0)
        {
            cursorMoveLeftCount = expanded.Length - 1 - pipeIdx;
            expanded = expanded.Remove(pipeIdx, 1);
        }

        return expanded;
    }

    private static string ConvertSingleChunk(
        string chunk,
        string nextNonSpace,
        TargetLanguage targetLang,
        LexiconDatabase lexicon)
    {
        if (targetLang == TargetLanguage.Russian)
        {
            // Preserve genuine ASCII URLs & emails when converting surrounding text EN -> RU
            if (TokenGuards.LooksLikeUrlOrDomain(chunk))
            {
                return chunk;
            }

            return ConvertChunkEnToRu(chunk, lexicon);
        }
        else
        {
            if (TryConvertRuTypedDomainToEn(chunk, out string domainEn))
            {
                return domainEn;
            }

            return ConvertChunkRuToEn(chunk, nextNonSpace);
        }
    }

    private static string ConvertChunkEnToRu(string chunk, LexiconDatabase lexicon)
    {
        // Peel outer non-letter wrappers like ( ... ) or !
        int left = 0;
        while (left < chunk.Length && chunk[left] is '(' or '«')
        {
            left++;
        }

        int right = chunk.Length;
        while (right > left && chunk[right - 1] is ')' or '!' or '»')
        {
            right--;
        }

        string prefix = chunk[..left];
        string core = chunk[left..right];
        string suffix = chunk[right..];

        if (core.Length == 0)
        {
            return chunk;
        }

        // Preserve CamelCase tech brands ("GitHub", "YouTube", "iPhone") when their RU mapping is gibberish
        if (core.Length >= 3 && TokenGuards.ClassifyCasing(core) == CasingKind.MixedCamel)
        {
            string mappedCheck = KeyboardGeometry.ConvertEnToRu(core).ToLowerInvariant();
            if (!lexicon.IsKnownRuWord(mappedCheck))
            {
                return chunk;
            }
        }

        // Check trailing punctuation nuance on core:
        // 1) '?', '/', '&' at end of EN->RU word map via KeyboardGeometry to ',', '.', '?' automatically!
        // 2) ',' or '.' at end of EN->RU word could be EITHER a Russian letter ('б'/'ю' as in "[kt,"="хлеб", "pyf."="знаю")
        //    OR literal punctuation typed visually ("Ghbdtn," -> "Привет,", "vbh." -> "мир.")!
        if (core.Length >= 2 && core[^1] is ',' or '.')
        {
            char trail = core[^1];
            string withoutTrail = core[..^1];
            string fullMapped = KeyboardGeometry.ConvertEnToRu(core);
            string baseMapped = KeyboardGeometry.ConvertEnToRu(withoutTrail);

            string fullLower = fullMapped.ToLowerInvariant();
            string baseLower = baseMapped.ToLowerInvariant();

            bool fullKnown = lexicon.TryGetRuWord(fullLower, out RuWordEntry fullEntry);
            bool baseKnown = lexicon.TryGetRuWord(baseLower, out RuWordEntry baseEntry) || lexicon.IsProtectedWord(baseLower);
            float baseZipf = baseEntry.Zipf > 0 ? baseEntry.Zipf : (baseKnown ? 4.2f : 0f);

            // Keep ',' or '.' as literal punctuation if fullMapped isn't a real word (e.g. "Приветб")
            // OR if baseMapped is vastly more common than a rare homograph (e.g. "мир." [5.5] vs "мирю" [2.1])
            if (!fullKnown || (baseKnown && (baseZipf - fullEntry.Zipf) > 2.0f))
            {
                return prefix + baseMapped + trail + suffix;
            }
        }

        // Similarly for ':' at end of word ("Cskrf:" -> "Ссылка:", whereas ":bpym" -> "Жизнь")
        if (core.Length >= 2 && core[^1] == ':')
        {
            string withoutColon = core[..^1];
            string fullMapped = KeyboardGeometry.ConvertEnToRu(core);
            if (!lexicon.IsKnownRuWord(fullMapped.ToLowerInvariant()))
            {
                return prefix + KeyboardGeometry.ConvertEnToRu(withoutColon) + ":" + suffix;
            }
        }

        return prefix + KeyboardGeometry.ConvertEnToRu(core) + suffix;
    }

    private static string ConvertChunkRuToEn(string chunk, string nextNonSpace)
    {
        // Note: on RU layout, ',' is Shift+/ ('?' on EN), while literal ',' may also have been typed visually.
        // If chunk ends with ',' and nextNonSpace starts with a lowercase Cyrillic letter ("Руддщ, цщкдв"),
        // keep ',' as ',' rather than turning mid-sentence comma into '?'!
        if (chunk.Length >= 2 && chunk[^1] == ',' &&
            nextNonSpace.Length > 0 && KeyboardGeometry.IsCyrillicLetter(nextNonSpace[0]) && char.IsLower(nextNonSpace[0]))
        {
            return KeyboardGeometry.ConvertRuToEn(chunk[..^1]) + ",";
        }

        return KeyboardGeometry.ConvertRuToEn(chunk);
    }

    private static string PeekNextNonSpaceChunk(string text, int startIdx)
    {
        int i = startIdx;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }
        int s = i;
        while (i < text.Length && !char.IsWhiteSpace(text[i]))
        {
            i++;
        }
        return text[s..i];
    }

    private static string ToSentenceCase(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool capNextLetter = true;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsLetter(c))
            {
                sb.Append(capNextLetter ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
                capNextLetter = false;
            }
            else
            {
                sb.Append(c);
                if (c is '.' or '!' or '?' or '\n' or '\r')
                {
                    capNextLetter = true;
                }
            }
        }

        return sb.ToString();
    }

    private static string TransliterateRuToLat(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!KeyboardGeometry.IsCyrillicLetter(c))
            {
                sb.Append(c);
                continue;
            }

            bool isUpper = char.IsUpper(c);
            string lowerStr = char.ToLowerInvariant(c).ToString();

            bool nextIsUpper = (i + 1 < text.Length && char.IsLetter(text[i + 1]) && char.IsUpper(text[i + 1])) ||
                               (i > 0 && char.IsLetter(text[i - 1]) && char.IsUpper(text[i - 1]) && (i + 1 >= text.Length || !char.IsLetter(text[i + 1])));

            bool matched = false;
            foreach (var (ruL, enL, enT) in RuToTranslitPairs)
            {
                if (ruL == lowerStr)
                {
                    if (!isUpper)
                    {
                        sb.Append(enL);
                    }
                    else if (nextIsUpper)
                    {
                        sb.Append(enL.ToUpperInvariant());
                    }
                    else
                    {
                        sb.Append(enT);
                    }
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string TransliterateLatToRu(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (!KeyboardGeometry.IsLatinLetter(text[i]))
            {
                sb.Append(text[i]);
                i++;
                continue;
            }

            bool matched = false;
            foreach (var (seq, cyrL, cyrU) in TranslitToRuSeqs)
            {
                if (i + seq.Length <= text.Length &&
                    text.AsSpan(i, seq.Length).Equals(seq, StringComparison.OrdinalIgnoreCase))
                {
                    bool isUpper = char.IsUpper(text[i]);
                    sb.Append(isUpper ? cyrU : cyrL);
                    i += seq.Length;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                sb.Append(text[i]);
                i++;
            }
        }

        return sb.ToString();
    }
}
