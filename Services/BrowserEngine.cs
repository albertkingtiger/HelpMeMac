using System.Text.Json.Serialization;

namespace HelpMe.Services;

// Where the AI and chat web clients run. WebView2 keeps them inside Help Me!'s
// own window, in panels 05 and 06; Chrome drives a separate browser window over
// its debugging port. Both speak the same protocol, so the pages are driven the
// same way either way.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserEngine
{
    WebView2,
    Chrome
}
