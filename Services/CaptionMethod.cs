using System.Text.Json.Serialization;

namespace HelpMe.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CaptionMethod
{
    Whisper
}
