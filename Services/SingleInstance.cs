namespace HelpMe.Services;

// One Help Me! per signed-in user. The app starts hidden and can launch at
// sign-in, so a second launch would otherwise add an invisible duplicate: two
// managed Chrome sessions, two capture pipelines, and two processes fighting
// over the Live Captions window. A second launch hands over to the first
// instead, asking it to show itself, which is what someone who launched it
// again actually wanted.
public static class SingleInstance
{
    private const string InstanceName = @"Local\HelpMe.SingleInstance";
    private const string ShowRequestName = @"Local\HelpMe.ShowRequest";
    private static Mutex? claim;

    // The mutex is never owned, only held open: ownership would have to survive
    // a force-kill, and an abandoned mutex is a worse problem than a stale name.
    // Windows destroys the name once the last handle closes, so a killed
    // instance never locks out the next one.
    public static bool TryClaim()
    {
        var mutex = new Mutex(initiallyOwned: false, InstanceName, out var created);
        if (created) { claim = mutex; return true; }
        mutex.Dispose();
        return false;
    }

    public static EventWaitHandle CreateShowListener()
        => new(false, EventResetMode.AutoReset, ShowRequestName);

    public static void RequestShow()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ShowRequestName, out var handle)) return;
            using (handle) handle.Set();
        }
        // The owner may still be starting up, or belong to another desktop.
        // Failing to raise its window is not worth an error on the way out.
        catch (WaitHandleCannotBeOpenedException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Release()
    {
        claim?.Dispose();
        claim = null;
    }
}
