using System.Text.RegularExpressions;

namespace HelpMe.Services;

// Translate small groups of adjacent sentences together for context. The last
// group remains provisional and is replaced as more speech arrives.
public sealed class DocumentTranslator
{
    private readonly Dictionary<string, string> cache = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private int version;

    public void Clear() { lock (sync) { cache.Clear(); ++version; } }

    public async Task<string> TranslateAsync(string source, Func<string, Task<string>> translate)
    {
        var translated = new List<string>();
        int requestVersion;
        lock (sync) requestVersion = version;
        foreach (var group in ContextGroups(source))
        {
            string? result;
            lock (sync) cache.TryGetValue(group, out result);
            if (result is null)
            {
                result = await translate(group).ConfigureAwait(false);
                lock (sync)
                {
                    if (requestVersion != version) return "";
                    // Growing fragments must be translated again with their full
                    // context rather than accumulating cached fragment outputs.
                    if (IsComplete(group))
                    {
                        if (cache.Count >= 512) cache.Clear();
                        cache[group] = result;
                    }
                }
            }
            translated.Add(result);
        }
        return string.Join(Environment.NewLine + Environment.NewLine, translated);
    }

    public static IReadOnlyList<string> ContextGroups(string source)
    {
        var groups = new List<string>();
        var current = new List<string>();
        foreach (var sentence in Sentences(source))
        {
            current.Add(sentence);
            // Two adjacent sentences give the model local context while keeping
            // requests small. A growing tail is replaced until the pair closes.
            if (current.Count == 2 && IsComplete(sentence))
            {
                groups.Add(string.Join(" ", current));
                current.Clear();
            }
        }
        if (current.Count > 0) groups.Add(string.Join(" ", current));
        return groups;
    }

    private static bool IsComplete(string text)
        => Regex.IsMatch(text, "[.!?][\\\"'”’)]*$");

    public static IReadOnlyList<string> Sentences(string source)
    {
        // Caption line breaks also occur after colons/semicolons and must not
        // turn an unfinished thought into an independent translation request.
        source = Regex.Replace(source, @"\s+", " ").Trim();
        var parts = Regex.Split(source, "(?<=[.!?])\\s+|(?<=[.!?][\\\"'”’)])\\s+")
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
