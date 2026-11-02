using System.IO;
using System.Net.Sockets;

namespace HelpMe.Services;

// One Help Me! per logged-in user. The app starts hidden and can launch at
// login, so a second launch would otherwise add an invisible duplicate: two
// sets of web panels, two capture pipelines and two menu-bar items. A second
// launch hands over to the first instead, asking it to show itself, which is
// what someone who launched it again actually wanted.
//
// Windows had a named mutex and a named event for this. macOS has neither, so
// the claim is an exclusively-held lock file and the request is one byte over a
// Unix domain socket beside it. A killed instance releases both when its file
// descriptors close, so a crash never locks out the next run.
public static class SingleInstance
{
    private static FileStream? claim;
    private static Socket? listener;
    private static CancellationTokenSource? listening;

    private static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "HelpMe");

    private static string LockPath => Path.Combine(StateDirectory, "instance.lock");
    private static string SocketPath => Path.Combine(StateDirectory, "show.sock");

    public static bool TryClaim()
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            claim = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // Listens for a later launch asking the running copy to come forward. The
    // callback arrives on a background thread; the caller marshals it.
    public static IDisposable CreateShowListener(Action onShowRequested)
    {
        var cancellation = new CancellationTokenSource();
        listening = cancellation;
        try
        {
            Directory.CreateDirectory(StateDirectory);
            // A socket file left behind by a killed instance would refuse the
            // bind, and nothing else can be listening on it: the lock file has
            // already established that this is the only copy running.
            if (File.Exists(SocketPath)) File.Delete(SocketPath);
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            listener.Listen(4);
            _ = Task.Run(() => AcceptAsync(listener, onShowRequested, cancellation.Token));
        }
        catch (Exception error) when (error is SocketException or IOException or UnauthorizedAccessException)
        {
            // Without the socket a second launch simply exits quietly instead
            // of raising this window. That is worth no error of its own.
        }
        return new Listener();
    }

    private static async Task AcceptAsync(Socket socket, Action onShowRequested, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket client;
            try { client = await socket.AcceptAsync(token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
            using (client)
            {
                try { onShowRequested(); }
                catch (Exception) { }
            }
        }
    }

    public static void RequestShow()
    {
        try
        {
            if (!File.Exists(SocketPath)) return;
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(SocketPath));
            client.Send(new byte[] { 1 });
        }
        // The owner may still be starting up, or may have just exited. Failing
        // to raise its window is not worth an error on the way out.
        catch (SocketException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Release()
    {
        listening?.Cancel();
        listening?.Dispose();
        listening = null;
        try { listener?.Dispose(); } catch (ObjectDisposedException) { }
        listener = null;
        claim?.Dispose();
        claim = null;
        try { if (File.Exists(SocketPath)) File.Delete(SocketPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class Listener : IDisposable
    {
        public void Dispose() { }
    }
}
