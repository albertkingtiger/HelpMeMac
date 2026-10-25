using HelpMe.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var calls = new List<string>();
Task<string> Translate(string text)
{
    calls.Add(text);
    return Task.FromResult($"[{text}]");
}
var translator = new DocumentTranslator();
await translator.TranslateAsync("If the server fails", Translate);
var revised = await translator.TranslateAsync("If the server fails we switch to the backup.", Translate);
Check(calls[^1] == "If the server fails we switch to the backup.", "A growing sentence must be translated as a whole.");
Check(!revised.Contains("[If the server fails]"), "The provisional fragment must be replaced, not appended.");
Console.WriteLine("PASS: growing sentence replaces provisional translation");

await translator.TranslateAsync("Alex bought a laptop. It was expensive.", Translate);
Check(calls[^1] == "Alex bought a laptop. It was expensive.", "Adjacent sentences must share model context.");
var previousCalls = calls.Count;
var expanded = await translator.TranslateAsync("Alex bought a laptop. It was expensive. He returned it", Translate);
Check(calls.Count == previousCalls + 1 && calls[^1] == "He returned it", "Completed groups must be reused while the new tail is translated.");
Check(expanded.Contains(Environment.NewLine + Environment.NewLine), "Translation groups must retain readable paragraph breaks.");
Console.WriteLine("PASS: adjacent sentence context and completed-group cache");

Check(DocumentTranslator.Sentences("The reason is:\nwe need more time;\nplease wait.").Count == 1,
    "Caption line breaks must not split an incomplete sentence.");
Check(DocumentTranslator.Sentences("Dr. Lee paid 3.5 dollars. He left.").Count == 2,
    "Titles and decimals must not split a sentence.");
Check(DocumentTranslator.Sentences("He said \"Ready.\" Then left.").Count == 2,
    "Quoted sentence endings must be recognized.");
Console.WriteLine("PASS: caption line breaks, titles, decimals and quoted endings");

var pending = new TaskCompletionSource<string>();
var oldRequest = translator.TranslateAsync("A pending request.", _ => pending.Task);
translator.Clear();
pending.SetResult("Old response");
Check(await oldRequest == "", "Clearing translation must invalidate in-flight work.");
calls.Clear();
await translator.TranslateAsync("A pending request.", Translate);
Check(calls.Count == 1, "Cleared in-flight results must not repopulate the cache.");
Console.WriteLine("PASS: clear invalidates pending results and cache entries");
