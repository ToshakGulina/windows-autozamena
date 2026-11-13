using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace AutoZamena.Core.Lexicon;

[Flags]
public enum RuGrammarFlags : byte
{
    None = 0,
    Infinitive = 1,
    Finite3Per = 2,
    TakesInfinitiveAfter = 4,
    TakesFinite3PerAfter = 8,
    CandidateTarget = 16
}

public readonly record struct RuWordEntry(
    int Index,
    string Word,
    float Zipf,
    RuGrammarFlags Flags,
    uint LemmaId
);

public readonly record struct EnWordEntry(
    int Index,
    string Word,
    float Zipf,
    bool IsCandidateTarget
);

/// <summary>
/// High-speed in-memory bilingual lexicon (185k RU + 55k EN words, morphological flags,
/// surface &amp; lemma bigrams, and Prefix-7 SymSpell candidate index).
/// </summary>
public sealed class LexiconDatabase
{
    public const string EmbeddedResourceName = "AutoZamena.Core.Data.lexicon_data.gz";

    private static readonly Lazy<LexiconDatabase> DefaultInstance =
        new(LoadFromEmbeddedResource, LazyThreadSafetyMode.ExecutionAndPublication);

    public static LexiconDatabase Default => DefaultInstance.Value;

    private readonly RuWordEntry[] _ruEntries;
    private readonly Dictionary<string, int> _ruLookup;
    private readonly EnWordEntry[] _enEntries;
    private readonly Dictionary<string, int> _enLookup;
    private readonly HashSet<string> _protectedWords;
    private readonly Dictionary<string, string> _explicitFixes;

    private readonly Dictionary<ulong, float> _ruLemmaBigrams;
    private readonly Dictionary<ulong, float> _ruSurfaceBigrams;
    private readonly Dictionary<ulong, float> _enSurfaceBigrams;

    private readonly PrefixSymSpellIndex _ruSymSpell;
    private readonly PrefixSymSpellIndex _enSymSpell;

    public int RussianWordCount => _ruEntries.Length;
    public int EnglishWordCount => _enEntries.Length;

    private LexiconDatabase(
        RuWordEntry[] ruEntries,
        Dictionary<string, int> ruLookup,
        EnWordEntry[] enEntries,
        Dictionary<string, int> enLookup,
        HashSet<string> protectedWords,
        Dictionary<string, string> explicitFixes,
        Dictionary<ulong, float> ruLemmaBigrams,
        Dictionary<ulong, float> ruSurfaceBigrams,
        Dictionary<ulong, float> enSurfaceBigrams)
    {
        _ruEntries = ruEntries;
        _ruLookup = ruLookup;
        _enEntries = enEntries;
        _enLookup = enLookup;
        _protectedWords = protectedWords;
        _explicitFixes = explicitFixes;
        _ruLemmaBigrams = ruLemmaBigrams;
        _ruSurfaceBigrams = ruSurfaceBigrams;
        _enSurfaceBigrams = enSurfaceBigrams;

        _ruSymSpell = new PrefixSymSpellIndex(
            _ruEntries.Length,
            i => (_ruEntries[i].Flags & RuGrammarFlags.CandidateTarget) != 0,
            i => NormalizeRuKey(_ruEntries[i].Word)
        );

        _enSymSpell = new PrefixSymSpellIndex(
            _enEntries.Length,
            i => _enEntries[i].IsCandidateTarget,
            i => _enEntries[i].Word
        );
    }

    public static LexiconDatabase LoadFromEmbeddedResource()
    {
        Assembly asm = typeof(LexiconDatabase).Assembly;
        string resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("lexicon_data.gz", StringComparison.OrdinalIgnoreCase))
            ?? EmbeddedResourceName;

        using Stream? rawStream = asm.GetManifestResourceStream(resourceName);
        if (rawStream is null)
        {
            string available = string.Join(", ", asm.GetManifestResourceNames());
            throw new InvalidOperationException(
                $"Embedded lexicon resource '{resourceName}' was not found. Available: [{available}]");
        }

        using var gzip = new GZipStream(rawStream, CompressionMode.Decompress);
        return LoadFromDecompressedStream(gzip);
    }

    public static LexiconDatabase LoadFromFile(string gzFilePath)
    {
        using FileStream fs = File.OpenRead(gzFilePath);
        using var gzip = new GZipStream(fs, CompressionMode.Decompress);
        return LoadFromDecompressedStream(gzip);
    }

    private static LexiconDatabase LoadFromDecompressedStream(Stream stream)
    {
        using var ms = new MemoryStream(4 * 1024 * 1024);
        stream.CopyTo(ms);
        ms.Position = 0;

        using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        byte[] magic = reader.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != 'A' || magic[1] != 'Z' || magic[2] != 'L' || magic[3] != 'X')
        {
            throw new InvalidDataException("Invalid lexicon header magic.");
        }

        ushort version = reader.ReadUInt16();
        if (version != 1)
        {
            throw new InvalidDataException($"Unsupported lexicon version: {version}.");
        }

        // Section 1: Russian words
        int ruCount = (int)reader.ReadUInt32();
        _ = reader.ReadUInt32(); // lemmaCount
        var ruEntries = new RuWordEntry[ruCount];
        var ruLookup = new Dictionary<string, int>(ruCount, StringComparer.Ordinal);
        string prev = string.Empty;
        for (int i = 0; i < ruCount; i++)
        {
            byte pfx = reader.ReadByte();
            string suffix = ReadShortUtf8(reader);
            string word = pfx == 0 ? suffix : string.Concat(prev.AsSpan(0, pfx), suffix);
            ushort zipfScaled = reader.ReadUInt16();
            var flags = (RuGrammarFlags)reader.ReadByte();
            uint lemmaId = reader.ReadUInt32();

            ruEntries[i] = new RuWordEntry(i, word, zipfScaled / 100.0f, flags, lemmaId);
            ruLookup[word] = i;
            prev = word;
        }

        // Section 2: English words
        int enCount = (int)reader.ReadUInt32();
        var enEntries = new EnWordEntry[enCount];
        var enLookup = new Dictionary<string, int>(enCount, StringComparer.Ordinal);
        prev = string.Empty;
        for (int i = 0; i < enCount; i++)
        {
            byte pfx = reader.ReadByte();
            string suffix = ReadShortUtf8(reader);
            string word = pfx == 0 ? suffix : string.Concat(prev.AsSpan(0, pfx), suffix);
            ushort zipfScaled = reader.ReadUInt16();
            byte flags = reader.ReadByte();

            enEntries[i] = new EnWordEntry(i, word, zipfScaled / 100.0f, (flags & 16) != 0);
            enLookup[word] = i;
            prev = word;
        }

        // Section 3: Protected words
        int protCount = (int)reader.ReadUInt32();
        var protectedWords = new HashSet<string>(protCount, StringComparer.OrdinalIgnoreCase);
        prev = string.Empty;
        for (int i = 0; i < protCount; i++)
        {
            byte pfx = reader.ReadByte();
            string suffix = ReadShortUtf8(reader);
            string word = pfx == 0 ? suffix : string.Concat(prev.AsSpan(0, pfx), suffix);
            protectedWords.Add(word);
            prev = word;
        }

        // Section 4: Explicit fixes
        int fixCount = (int)reader.ReadUInt32();
        var explicitFixes = new Dictionary<string, string>(fixCount, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < fixCount; i++)
        {
            string typo = ReadShortUtf8(reader);
            string repl = ReadShortUtf8(reader);
            explicitFixes[typo] = repl;
        }

        // Section 5: RU lemma bigrams
        int ruLemmaBiCount = (int)reader.ReadUInt32();
        var ruLemmaBigrams = new Dictionary<ulong, float>(ruLemmaBiCount);
        for (int i = 0; i < ruLemmaBiCount; i++)
        {
            uint l1 = reader.ReadUInt32();
            uint l2 = reader.ReadUInt32();
            ushort boost = reader.ReadUInt16();
            ruLemmaBigrams[PackPair(l1, l2)] = boost / 100.0f;
        }

        // Section 6: RU surface bigrams
        int ruSurfBiCount = (int)reader.ReadUInt32();
        var ruSurfaceBigrams = new Dictionary<ulong, float>(ruSurfBiCount);
        for (int i = 0; i < ruSurfBiCount; i++)
        {
            uint w1 = reader.ReadUInt32();
            uint w2 = reader.ReadUInt32();
            ushort boost = reader.ReadUInt16();
            ruSurfaceBigrams[PackPair(w1, w2)] = boost / 100.0f;
        }

        // Section 7: EN surface bigrams
        int enSurfBiCount = (int)reader.ReadUInt32();
        var enSurfaceBigrams = new Dictionary<ulong, float>(enSurfBiCount);
        for (int i = 0; i < enSurfBiCount; i++)
        {
            uint w1 = reader.ReadUInt32();
            uint w2 = reader.ReadUInt32();
            ushort boost = reader.ReadUInt16();
            enSurfaceBigrams[PackPair(w1, w2)] = boost / 100.0f;
        }

        return new LexiconDatabase(
            ruEntries,
            ruLookup,
            enEntries,
            enLookup,
            protectedWords,
            explicitFixes,
            ruLemmaBigrams,
            ruSurfaceBigrams,
            enSurfaceBigrams
        );
    }

    public bool IsProtectedWord(string lowerWord) => _protectedWords.Contains(lowerWord);

    public bool TryGetExplicitFix(string lowerWord, out string replacement) =>
        _explicitFixes.TryGetValue(lowerWord, out replacement!);

    public bool TryGetRuWord(string lowerWord, out RuWordEntry entry)
    {
        if (_ruLookup.TryGetValue(lowerWord, out int idx))
        {
            entry = _ruEntries[idx];
            return true;
        }

        if (lowerWord.Contains('ё'))
        {
            string norm = lowerWord.Replace('ё', 'е');
            if (_ruLookup.TryGetValue(norm, out idx))
            {
                entry = _ruEntries[idx];
                return true;
            }
        }

        entry = default;
        return false;
    }

    public bool IsKnownRuWord(string lowerWord) => TryGetRuWord(lowerWord, out _);

    public RuWordEntry GetRuEntryByIndex(int index) => _ruEntries[index];

    public bool TryGetEnWord(string lowerWord, out EnWordEntry entry)
    {
        if (_enLookup.TryGetValue(lowerWord, out int idx))
        {
            entry = _enEntries[idx];
            return true;
        }

        entry = default;
        return false;
    }

    public bool IsKnownEnWord(string lowerWord) => _enLookup.ContainsKey(lowerWord);

    public EnWordEntry GetEnEntryByIndex(int index) => _enEntries[index];

    public float GetRuSurfaceBigramBoost(in RuWordEntry prevWord, in RuWordEntry nextWord)
    {
        return _ruSurfaceBigrams.TryGetValue(PackPair((uint)prevWord.Index, (uint)nextWord.Index), out float surf)
            ? surf
            : 0f;
    }

    public float GetRuContextBoost(in RuWordEntry prevWord, in RuWordEntry candidate)
    {
        float boost = 0f;

        if (_ruSurfaceBigrams.TryGetValue(PackPair((uint)prevWord.Index, (uint)candidate.Index), out float surf))
        {
            boost = MathF.Max(boost, surf);
        }

        if (_ruLemmaBigrams.TryGetValue(PackPair(prevWord.LemmaId, candidate.LemmaId), out float lem))
        {
            boost = MathF.Max(boost, lem);
        }

        // Grammatical -тся / -ться context rules
        bool prevTakesInf = (prevWord.Flags & RuGrammarFlags.TakesInfinitiveAfter) != 0;
        bool prevTakes3Per = (prevWord.Flags & RuGrammarFlags.TakesFinite3PerAfter) != 0;
        bool candIsInf = (candidate.Flags & RuGrammarFlags.Infinitive) != 0;
        bool candIs3Per = (candidate.Flags & RuGrammarFlags.Finite3Per) != 0;

        if (prevTakesInf)
        {
            if (candIsInf && !candIs3Per)
            {
                boost += 2.15f;
            }
            else if (candIs3Per && !candIsInf && EndsWithReflexive(candidate.Word))
            {
                boost -= 1.10f;
            }
        }
        else if (prevTakes3Per)
        {
            if (candIs3Per && !candIsInf)
            {
                boost += 1.65f;
            }
            else if (candIsInf && !candIs3Per && EndsWithReflexive(candidate.Word))
            {
                boost -= 0.95f;
            }
        }

        return boost;
    }

    public float GetEnContextBoost(in EnWordEntry prevWord, in EnWordEntry candidate)
    {
        return _enSurfaceBigrams.TryGetValue(PackPair((uint)prevWord.Index, (uint)candidate.Index), out float surf)
            ? surf
            : 0f;
    }

    public void QueryRuCandidates(string lowerQuery, int maxEditDistance, List<int> results)
    {
        string norm = NormalizeRuKey(lowerQuery);
        _ruSymSpell.Query(norm, maxEditDistance, results, idx => _ruEntries[idx].Word.Length);
    }

    public void QueryEnCandidates(string lowerQuery, int maxEditDistance, List<int> results)
    {
        _enSymSpell.Query(lowerQuery, maxEditDistance, results, idx => _enEntries[idx].Word.Length);
    }

    private static bool EndsWithReflexive(string w) =>
        w.EndsWith("тся", StringComparison.Ordinal) || w.EndsWith("ться", StringComparison.Ordinal);

    private static string NormalizeRuKey(string s) => s.Contains('ё') ? s.Replace('ё', 'е') : s;

    private static ulong PackPair(uint a, uint b) => ((ulong)a << 32) | b;

    private static string ReadShortUtf8(BinaryReader reader)
    {
        byte len = reader.ReadByte();
        if (len == 0)
        {
            return string.Empty;
        }
        Span<byte> buf = stackalloc byte[len];
        int read = reader.Read(buf);
        return Encoding.UTF8.GetString(buf[..read]);
    }

    /// <summary>
    /// Prefix-7 SymSpell index collapsing inflected stems into prefix buckets for low RAM and sub-0.2ms lookup.
    /// </summary>
    private sealed class PrefixSymSpellIndex
    {
        private const int PrefixLength = 7;

        private readonly List<int>[] _buckets;
        private readonly Dictionary<string, List<int>> _deleteMap;

        public PrefixSymSpellIndex(int itemCount, Func<int, bool> includePredicate, Func<int, string> wordSelector)
        {
            var prefixToBucket = new Dictionary<string, int>(32768, StringComparer.Ordinal);
            var bucketLists = new List<List<int>>(32768);

            for (int i = 0; i < itemCount; i++)
            {
                if (!includePredicate(i))
                {
                    continue;
                }

                string w = wordSelector(i);
                string pfx = w.Length <= PrefixLength ? w : w[..PrefixLength];

                if (!prefixToBucket.TryGetValue(pfx, out int bucketId))
                {
                    bucketId = bucketLists.Count;
                    prefixToBucket[pfx] = bucketId;
                    bucketLists.Add(new List<int>(2));
                }

                bucketLists[bucketId].Add(i);
            }

            _buckets = bucketLists.ToArray();
            _deleteMap = new Dictionary<string, List<int>>(_buckets.Length * 8, StringComparer.Ordinal);

            var localDeletes = new HashSet<string>(32, StringComparer.Ordinal);
            foreach (var kv in prefixToBucket)
            {
                string pfx = kv.Key;
                int bucketId = kv.Value;
                localDeletes.Clear();
                localDeletes.Add(pfx);
                GenerateDeletes(pfx, maxDist: 2, localDeletes);

                foreach (string del in localDeletes)
                {
                    if (!_deleteMap.TryGetValue(del, out List<int>? list))
                    {
                        list = new List<int>(2);
                        _deleteMap[del] = list;
                    }
                    list.Add(bucketId);
                }
            }
        }

        public void Query(string query, int maxDist, List<int> outIndices, Func<int, int> wordLengthGetter)
        {
            outIndices.Clear();
            if (string.IsNullOrEmpty(query))
            {
                return;
            }

            var queryKeys = new HashSet<string>(64, StringComparer.Ordinal);

            string pfxShort = query.Length <= PrefixLength ? query : query[..PrefixLength];
            queryKeys.Add(pfxShort);
            GenerateDeletes(pfxShort, maxDist, queryKeys);

            if (query.Length > PrefixLength)
            {
                int extLen = Math.Min(query.Length, PrefixLength + maxDist);
                string pfxExt = query[..extLen];
                var extDeletes = new HashSet<string>(48, StringComparer.Ordinal) { pfxExt };
                GenerateDeletes(pfxExt, maxDist, extDeletes);
                foreach (string s in extDeletes)
                {
                    queryKeys.Add(s.Length <= PrefixLength ? s : s[..PrefixLength]);
                }
            }

            var seenBuckets = new HashSet<int>();
            int qLen = query.Length;

            foreach (string key in queryKeys)
            {
                if (!_deleteMap.TryGetValue(key, out List<int>? bucketIds))
                {
                    continue;
                }

                for (int b = 0; b < bucketIds.Count; b++)
                {
                    int bucketId = bucketIds[b];
                    if (!seenBuckets.Add(bucketId))
                    {
                        continue;
                    }

                    List<int> items = _buckets[bucketId];
                    for (int k = 0; k < items.Count; k++)
                    {
                        int wordIdx = items[k];
                        if (Math.Abs(wordLengthGetter(wordIdx) - qLen) <= maxDist)
                        {
                            outIndices.Add(wordIdx);
                        }
                    }
                }
            }
        }

        private static void GenerateDeletes(string word, int maxDist, HashSet<string> sink)
        {
            if (maxDist <= 0 || word.Length <= 1)
            {
                return;
            }

            for (int i = 0; i < word.Length; i++)
            {
                string d1 = word.Remove(i, 1);
                sink.Add(d1);

                if (maxDist >= 2 && d1.Length > 1)
                {
                    for (int j = i; j < d1.Length; j++)
                    {
                        sink.Add(d1.Remove(j, 1));
                    }
                }
            }
        }
    }
}
