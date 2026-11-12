namespace AutoZamena.Core.Layout;

/// <summary>
/// Physical 2D keyboard geometry for Russian (ЙЦУКЕН) and English (QWERTY) layouts,
/// plus exact bidirectional character mapping for wrong-layout recovery.
/// </summary>
public static class KeyboardGeometry
{
    private readonly record struct KeyPos(float X, float Y);

    private static readonly Dictionary<char, KeyPos> RuPositions = new()
    {
        ['ё'] = new(-1.0f, -1.0f),
        // Row 1
        ['й'] = new(0.0f, 0.0f), ['ц'] = new(1.0f, 0.0f), ['у'] = new(2.0f, 0.0f),
        ['к'] = new(3.0f, 0.0f), ['е'] = new(4.0f, 0.0f), ['н'] = new(5.0f, 0.0f),
        ['г'] = new(6.0f, 0.0f), ['ш'] = new(7.0f, 0.0f), ['щ'] = new(8.0f, 0.0f),
        ['з'] = new(9.0f, 0.0f), ['х'] = new(10.0f, 0.0f), ['ъ'] = new(11.0f, 0.0f),
        // Row 2 (stagger +0.25)
        ['ф'] = new(0.25f, 1.0f), ['ы'] = new(1.25f, 1.0f), ['в'] = new(2.25f, 1.0f),
        ['а'] = new(3.25f, 1.0f), ['п'] = new(4.25f, 1.0f), ['р'] = new(5.25f, 1.0f),
        ['о'] = new(6.25f, 1.0f), ['л'] = new(7.25f, 1.0f), ['д'] = new(8.25f, 1.0f),
        ['ж'] = new(9.25f, 1.0f), ['э'] = new(10.25f, 1.0f),
        // Row 3 (stagger +0.75)
        ['я'] = new(0.75f, 2.0f), ['ч'] = new(1.75f, 2.0f), ['с'] = new(2.75f, 2.0f),
        ['м'] = new(3.75f, 2.0f), ['и'] = new(4.75f, 2.0f), ['т'] = new(5.75f, 2.0f),
        ['ь'] = new(6.75f, 2.0f), ['б'] = new(7.75f, 2.0f), ['ю'] = new(8.75f, 2.0f),
    };

    private static readonly Dictionary<char, KeyPos> EnPositions = new()
    {
        // Row 1
        ['q'] = new(0.0f, 0.0f), ['w'] = new(1.0f, 0.0f), ['e'] = new(2.0f, 0.0f),
        ['r'] = new(3.0f, 0.0f), ['t'] = new(4.0f, 0.0f), ['y'] = new(5.0f, 0.0f),
        ['u'] = new(6.0f, 0.0f), ['i'] = new(7.0f, 0.0f), ['o'] = new(8.0f, 0.0f),
        ['p'] = new(9.0f, 0.0f),
        // Row 2 (stagger +0.25)
        ['a'] = new(0.25f, 1.0f), ['s'] = new(1.25f, 1.0f), ['d'] = new(2.25f, 1.0f),
        ['f'] = new(3.25f, 1.0f), ['g'] = new(4.25f, 1.0f), ['h'] = new(5.25f, 1.0f),
        ['j'] = new(6.25f, 1.0f), ['k'] = new(7.25f, 1.0f), ['l'] = new(8.25f, 1.0f),
        // Row 3 (stagger +0.75)
        ['z'] = new(0.75f, 2.0f), ['x'] = new(1.75f, 2.0f), ['c'] = new(2.75f, 2.0f),
        ['v'] = new(3.75f, 2.0f), ['b'] = new(4.75f, 2.0f), ['n'] = new(5.75f, 2.0f),
        ['m'] = new(6.75f, 2.0f),
    };

    private static readonly Dictionary<char, char> EnToRuMap = new()
    {
        ['`'] = 'ё', ['~'] = 'Ё',
        ['q'] = 'й', ['w'] = 'ц', ['e'] = 'у', ['r'] = 'к', ['t'] = 'е', ['y'] = 'н',
        ['u'] = 'г', ['i'] = 'ш', ['o'] = 'щ', ['p'] = 'з', ['['] = 'х', [']'] = 'ъ',
        ['a'] = 'ф', ['s'] = 'ы', ['d'] = 'в', ['f'] = 'а', ['g'] = 'п', ['h'] = 'р',
        ['j'] = 'о', ['k'] = 'л', ['l'] = 'д', [';'] = 'ж', ['\''] = 'э',
        ['z'] = 'я', ['x'] = 'ч', ['c'] = 'с', ['v'] = 'м', ['b'] = 'и', ['n'] = 'т',
        ['m'] = 'ь', [','] = 'б', ['.'] = 'ю', ['/'] = '.',
        ['Q'] = 'Й', ['W'] = 'Ц', ['E'] = 'У', ['R'] = 'К', ['T'] = 'Е', ['Y'] = 'Н',
        ['U'] = 'Г', ['I'] = 'Ш', ['O'] = 'Щ', ['P'] = 'З', ['{'] = 'Х', ['}'] = 'Ъ',
        ['A'] = 'Ф', ['S'] = 'Ы', ['D'] = 'В', ['F'] = 'А', ['G'] = 'П', ['H'] = 'Р',
        ['J'] = 'О', ['K'] = 'Л', ['L'] = 'Д', [':'] = 'Ж', ['"'] = 'Э',
        ['Z'] = 'Я', ['X'] = 'Ч', ['C'] = 'С', ['V'] = 'М', ['B'] = 'И', ['N'] = 'Т',
        ['M'] = 'Ь', ['<'] = 'Б', ['>'] = 'Ю', ['?'] = ',', ['&'] = '?'
    };

    private static readonly Dictionary<char, char> RuToEnMap;

    static KeyboardGeometry()
    {
        RuToEnMap = new Dictionary<char, char>(EnToRuMap.Count);
        foreach (var kv in EnToRuMap)
        {
            // Keep primary letter mappings unambiguous
            if (!RuToEnMap.ContainsKey(kv.Value))
            {
                RuToEnMap[kv.Value] = kv.Key;
            }
        }
    }

    public static bool IsCyrillicLetter(char c) =>
        (c >= 'а' && c <= 'я') || (c >= 'А' && c <= 'Я') || c == 'ё' || c == 'Ё';

    public static bool IsLatinLetter(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    /// <summary>
    /// True if <paramref name="c"/> is an English-layout key that corresponds to a Russian letter
    /// (including punctuation keys , . ; ' [ ] ` and their shifted variants).
    /// </summary>
    public static bool IsEnKeyForRuLetter(char c) =>
        EnToRuMap.TryGetValue(c, out char mapped) && IsCyrillicLetter(mapped);

    /// <summary>
    /// Returns physical Euclidean distance in key-widths on the keyboard, or 4.0f if unknown/cross-script.
    /// </summary>
    public static float GetKeyDistance(char a, char b)
    {
        char la = char.ToLowerInvariant(a);
        char lb = char.ToLowerInvariant(b);
        if (la == lb)
        {
            return 0f;
        }

        if (RuPositions.TryGetValue(la, out var pa) && RuPositions.TryGetValue(lb, out var pb))
        {
            float dx = pa.X - pb.X;
            float dy = pa.Y - pb.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        if (EnPositions.TryGetValue(la, out var ea) && EnPositions.TryGetValue(lb, out var eb))
        {
            float dx = ea.X - eb.X;
            float dy = ea.Y - eb.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        return 4.0f;
    }

    public static bool AreAdjacentKeys(char a, char b) => GetKeyDistance(a, b) <= 1.38f;

    public static string ConvertEnToRu(ReadOnlySpan<char> input)
    {
        Span<char> buffer = input.Length <= 128 ? stackalloc char[input.Length] : new char[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            buffer[i] = EnToRuMap.TryGetValue(input[i], out char mapped) ? mapped : input[i];
        }
        return new string(buffer);
    }

    public static string ConvertRuToEn(ReadOnlySpan<char> input)
    {
        Span<char> buffer = input.Length <= 128 ? stackalloc char[input.Length] : new char[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            buffer[i] = RuToEnMap.TryGetValue(input[i], out char mapped) ? mapped : input[i];
        }
        return new string(buffer);
    }
}
