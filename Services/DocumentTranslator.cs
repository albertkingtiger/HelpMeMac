using System.Text.RegularExpressions;

namespace HelpMe.Services;

// Reuse translations of unchanged sentences, but translate a revised/growing
// sentence as a whole. Never concatenate translations of individual word deltas.
public sealed class DocumentTranslator
{
    private readonly Dictionary<string, string> cache = new(StringComparer.Ordinal);
    private readonly object sync = new();

    public void Clear() { lock (sync) cache.Clear(); }

    public async Task<string> TranslateAsync(string source, Func<string, Task<string>> translate)
    {
        var translated = new List<string>();
        foreach (var sentence in Sentences(source))
        {
            string? result;
            lock (sync) cache.TryGetValue(sentence, out result);
            if (result is null)
            {
                result = await translate(sentence).ConfigureAwait(false);
                lock (sync)
                {
                    if (cache.Count >= 512) cache.Clear();
                    cache[sentence] = result;
                }
            }
            translated.Add(result);
        }
        return string.Join(" ", translated);
    }

    public static IReadOnlyList<string> Sentences(string source)
    {
        var parts = Regex.Split(source.Trim(), @"(?<=[.!?])\s+|\r?\n+")
            .Select(x => x.Trim()).Where(x => x.Length > 0);
        var result = new List<string>();
        foreach (var part in parts)
        {
            if (result.Count > 0 && Regex.IsMatch(result[^1], @"\b(?:Mr|Mrs|Ms|Dr|Prof|Sr|Jr|vs|e\.g|i\.e|[A-Z])\.$"))
                result[^1] += " " + part;
            else result.Add(part);
        }
        return result;
    }
}
