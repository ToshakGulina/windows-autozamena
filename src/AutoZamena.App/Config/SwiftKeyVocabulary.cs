using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using AutoZamena.Core.Lexicon;

namespace AutoZamena.App.Config;

/// <summary>
/// Reads the human-readable SwiftKey vocabulary export. This is not the binary
/// dynamic.lm language model; SwiftKey does NOT import edits to vocabulary.txt.
/// </summary>
internal static class SwiftKeyVocabulary
{
    private const long MaxFileBytes = 8 * 1024 * 1024;
    private const int MaxLines = 200_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static int Import(string path, UserDictionary dictionary)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string word in ReadWords(path)) words.Add(word);
        return dictionary.AddIgnoredWords(words);
    }

    internal static IEnumerable<string> ReadWords(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes)
            throw new InvalidDataException("Файл SwiftKey слишком большой.");
        using var reader = new StreamReader(path, StrictUtf8, detectEncodingFromByteOrderMarks: true);
        string? line;
        int count = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            if (++count > MaxLines) throw new InvalidDataException("Слишком много строк в файле SwiftKey.");
            // SwiftKey's export starts with explanatory comments. Do not treat
            // punctuation, numbers, phrases or control characters as words.
            string word = line.Trim();
            if (IsWord(word)) yield return word;
        }
    }

    internal static bool IsWord(string word)
    {
        if (word.Length is < 2 or > 64 || word[0] is '#' or '\uFEFF') return false;
        if (!char.IsLetter(word[0]) || !char.IsLetter(word[^1])) return false;
        for (int i = 1; i < word.Length - 1; i++)
        {
            char c = word[i];
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (!char.IsLetter(c) && category is not (UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) &&
                c is not ('-' or '\'' or '’'))
                return false;
        }
        return true;
    }

    internal static void Export(string path, UserDictionary dictionary)
    {
        // Plain text portability only: SwiftKey's prediction model is dynamic.lm,
        // and copying this file back into SwiftKey cannot train or change that model.
        using var writer = new StreamWriter(path, append: false, StrictUtf8);
        writer.WriteLine("# Слова, сохранённые в AutoZamena. Список для просмотра и переноса.");
        writer.WriteLine("# SwiftKey не импортирует правки vocabulary.txt; dynamic.lm этим файлом не изменяется.");
        foreach (string word in dictionary.IgnoredWords.Where(IsWord).OrderBy(w => w, StringComparer.OrdinalIgnoreCase))
            writer.WriteLine(word);
    }
}
