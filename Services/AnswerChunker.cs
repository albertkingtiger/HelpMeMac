namespace HelpMe.Services;

// Auto-send forwards an answer a few sentences at a time while the AI is still
// typing, so a slow reply starts arriving at once instead of all at the end.
//
// Chunks are counted in sentences rather than character offsets: the AI page
// re-renders its own markdown as the reply grows, which moves the text around,
// and an offset into it would end up pointing at the wrong place.
//
// DocumentTranslator splits captions into sentences too, but it collapses all
// whitespace and keeps no unfinished tail, which is right for a caption and
// wrong for an answer: a message has to keep its own line breaks and code
// blocks, and must never go out while its last sentence is still being typed.
public static class AnswerChunker
{
    // The next whole chunk, or null while the answer has not produced one yet.
    public static string? NextChunk(string answer, int alreadySent, int perMessage, out int sentenceCount)
    {
        sentenceCount = alreadySent;
        if (perMessage < 1) return null;
        var sentences = Split(answer);
        if (alreadySent < 0 || alreadySent + perMessage > sentences.Count) return null;
        sentenceCount = alreadySent + perMessage;
        return Slice(answer, sentences[alreadySent].Start, sentences[sentenceCount - 1].End);
    }

    // The flush once the answer is complete: everything still unsent, including
    // a trailing fragment that the sentence rules would otherwise wait on, and
    // null when the answer went out in full while it was still being written.
    //
    // It must count the answer as finished text. While a reply streams, its last
    // sentence is not counted, because the words after it are what prove it
    // ended; counting the same text as unfinished here would offer that already
    // sent sentence again, and a flush that keeps finding something to send
    // never stops.
    public static string? RemainingChunk(string answer, int alreadySent, out int sentenceCount)
    {
        var sentences = Split(answer, final: true);
        if (alreadySent < 0) alreadySent = 0;
        sentenceCount = Math.Max(sentences.Count, alreadySent);
        if (alreadySent >= sentences.Count) return null;
        return Slice(answer, sentences[alreadySent].Start, answer.Length);
    }

    // How much of an answer already counts as sent, for auto-send switched on
    // part way through: what is on screen now is the reader's to catch up on.
    public static int SentenceCount(string answer) => Split(answer).Count;

    private static string? Slice(string answer, int start, int end)
    {
        var text = answer[start..end].Trim();
        return text.Length > 0 ? text : null;
    }

    // Ranges are contiguous, so any run of sentences can be taken as a single
    // slice of the original text and keep its own line breaks and list layout.
    //
    // `final` = the answer is complete: nothing more is coming to prove the last
    // sentence ended, so the tail counts as a sentence of its own, and a code
    // fence that was never closed no longer holds the text after it back.
    private static List<(int Start, int End)> Split(string answer, bool final = false)
    {
        var sentences = new List<(int Start, int End)>();
        var limit = final ? answer.Length : OpenCodeFence(answer);
        var start = 0;
        for (var i = 0; i < limit; i++)
        {
            // A finished code block travels in one piece: the periods and line
            // breaks inside it are not sentence ends.
            if (i + 3 <= limit && answer.AsSpan(i, 3).SequenceEqual("```"))
            {
                var close = answer.IndexOf("```", i + 3, StringComparison.Ordinal);
                i = close < 0 ? limit : close + 2;
                continue;
            }
            if (IsFullWidthTerminator(answer[i]))
            {
                sentences.Add((start, i + 1));
                start = i + 1;
                continue;
            }
            if (answer[i] is not ('.' or '!' or '?' or '…')) continue;
            var end = i + 1;
            // Repeated marks and a closing quote or bracket end the same sentence.
            while (end < limit && (answer[end] is '.' or '!' or '?' or '…'
                or '"' or '\'' or ')' or ']' or '”' or '’')) end++;
            // Nothing after the mark yet, so nothing proves the sentence ended.
            if (end >= limit) break;
            // "3.5" and "example.com" keep going; a real end has space after it.
            if (!char.IsWhiteSpace(answer[end])) continue;
            if (IsAbbreviation(answer, i)) continue;
            if (!StartsNewSentence(answer, end)) continue;
            sentences.Add((start, end));
            start = end;
            i = end - 1;
        }
        if (final && answer.AsSpan(start).Trim().Length > 0) sentences.Add((start, answer.Length));
        return sentences;
    }

    // Half of a fenced code block is no use to the reader, so everything from an
    // unclosed fence onward is held back until the fence closes.
    private static int OpenCodeFence(string answer)
    {
        var open = -1;
        for (var i = answer.IndexOf("```", StringComparison.Ordinal); i >= 0;
             i = answer.IndexOf("```", i + 3, StringComparison.Ordinal))
            open = open < 0 ? i : -1;
        return open < 0 ? answer.Length : open;
    }

    // A reply that continues in lower case was never a sentence end: "e.g. three
    // of them" and "see fig. 2 below" read as one sentence, not two. What comes
    // next is read from the whole answer, so a sentence that runs up to a code
    // fence is released rather than waiting for the block to close.
    private static bool StartsNewSentence(string answer, int index)
    {
        while (index < answer.Length && char.IsWhiteSpace(answer[index])) index++;
        return index < answer.Length && !char.IsLower(answer[index]);
    }

    // Titles, initials and short forms take a period without ending anything.
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "approx",
        "fig", "no", "cf", "al", "inc", "ltd", "co", "dept", "est", "vol", "ca",
        "min", "max", "sec", "hr", "pp", "am", "pm", "eg", "ie",
    };

    private static bool IsAbbreviation(string answer, int period)
    {
        if (answer[period] is not '.') return false;
        var start = period;
        while (start > 0 && (char.IsLetter(answer[start - 1]) || answer[start - 1] is '.')) start--;
        var token = answer[start..period];
        // "e.g." and "U.S." carry their own periods; "J." is an initial.
        if (token.Contains('.')) return true;
        if (token.Length == 1 && char.IsUpper(token[0])) return true;
        return Abbreviations.Contains(token);
    }

    // CJK punctuation ends a sentence without a following space.
    private static bool IsFullWidthTerminator(char value) => value is '。' or '！' or '？';
}
