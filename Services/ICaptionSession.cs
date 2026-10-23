namespace HelpMe.Services;

public interface ICaptionSession : IAsyncDisposable
{
    Task StartAsync();
}
