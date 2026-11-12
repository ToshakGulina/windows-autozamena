using System.Collections.Concurrent;

namespace AutoZamena.Core.Lexicon;

/// <summary>
/// Thread-safe personal dictionary storing user-whitelisted words (manual + learned via Undo)
/// and custom trigger -> expansion pairs.
/// </summary>
public sealed class UserDictionary
{
    private readonly ConcurrentDictionary<string, byte> _ignoredWords = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _customReplacements = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public IReadOnlyCollection<string> IgnoredWords => _ignoredWords.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyDictionary<string, string> CustomReplacements =>
        new Dictionary<string, string>(_customReplacements, StringComparer.OrdinalIgnoreCase);

    public void Load(IEnumerable<string>? ignoredWords, IDictionary<string, string>? customReplacements)
    {
        _ignoredWords.Clear();
        if (ignoredWords is not null)
        {
            foreach (string w in ignoredWords)
            {
                if (!string.IsNullOrWhiteSpace(w))
                {
                    _ignoredWords[w.Trim()] = 1;
                }
            }
        }

        _customReplacements.Clear();
        if (customReplacements is not null)
        {
            foreach (var kv in customReplacements)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                {
                    _customReplacements[kv.Key.Trim()] = kv.Value;
                }
            }
        }
    }

    public bool IsIgnored(string word) =>
        !string.IsNullOrEmpty(word) && _ignoredWords.ContainsKey(word);

    public bool AddIgnoredWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return false;
        }

        bool added = _ignoredWords.TryAdd(word.Trim(), 1);
        if (added)
        {
            Changed?.Invoke();
        }
        return added;
    }

    public bool RemoveIgnoredWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return false;
        }

        bool removed = _ignoredWords.TryRemove(word.Trim(), out _);
        if (removed)
        {
            Changed?.Invoke();
        }
        return removed;
    }

    public bool TryGetCustomReplacement(string trigger, out string replacement) =>
        _customReplacements.TryGetValue(trigger, out replacement!);

    public void SetCustomReplacement(string trigger, string replacement)
    {
        if (string.IsNullOrWhiteSpace(trigger) || string.IsNullOrEmpty(replacement))
        {
            return;
        }

        _customReplacements[trigger.Trim()] = replacement;
        Changed?.Invoke();
    }

    public bool RemoveCustomReplacement(string trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger))
        {
            return false;
        }

        bool removed = _customReplacements.TryRemove(trigger.Trim(), out _);
        if (removed)
        {
            Changed?.Invoke();
        }
        return removed;
    }
}
