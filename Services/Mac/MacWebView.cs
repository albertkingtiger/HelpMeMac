using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace HelpMe.Services.Mac;

// One WKWebView, embedded in the window as panels 05 and 06.
//
// Avalonia hosts a real native view through NativeControlHost: the handle
// returned here is an NSView that AppKit adds as a subview of the panel, so the
// page is inside Help Me! rather than in a browser window of its own, exactly
// as the WebView2 panels were on Windows.
//
// WebKit is not thread-safe and every message below must go to the main thread.
// On macOS Avalonia's UI thread is that thread, so Dispatcher.UIThread is both
// the Avalonia rule and the AppKit one.
public sealed class MacWebView : NativeControlHost
{
    private IntPtr view;
    private IntPtr configuration;

    // Both panels keep the sign-ins they are given. WKWebView's default data
    // store is persistent and lives beside the app's other data, which is what
    // makes a sign-in survive a restart without any profile folder of our own.
    private const string SafariUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15";

    public IntPtr Handle => view;
    public bool IsCreated => view != IntPtr.Zero;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        // Away from macOS the panel is an empty native control. The app still
        // builds and its layout can be looked at on a Windows desktop; only the
        // pages are missing, and WebViewBrowserSession says so rather than
        // failing somewhere further in.
        if (!OperatingSystem.IsMacOS()) return base.CreateNativeControlCore(parent);

        configuration = ObjC.New("WKWebViewConfiguration");
        var allocated = ObjC.Alloc("WKWebView");
        var frame = new ObjC.CGRect(0, 0, 800, 600);
        view = ObjC.SendRect(allocated, ObjC.Sel("initWithFrame:configuration:"), frame, configuration);

        // NSViewWidthSizable | NSViewHeightSizable: the page follows the panel
        // when the splitters are dragged.
        ObjC.SendVoid(view, ObjC.Sel("setAutoresizingMask:"), (nuint)(2 | 16));
        // WKWebView's own user agent omits the Version/Safari tokens, and
        // WhatsApp Web turns away a browser it cannot recognise.
        ObjC.SendVoid(view, ObjC.Sel("setCustomUserAgent:"), ObjC.NSString(SafariUserAgent));
        return new PlatformHandle(view, "NSView");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (!OperatingSystem.IsMacOS()) { base.DestroyNativeControlCore(control); return; }
        var retained = view;
        var retainedConfiguration = configuration;
        view = IntPtr.Zero;
        configuration = IntPtr.Zero;
        ObjC.Release(retained);
        ObjC.Release(retainedConfiguration);
    }

    public void Navigate(string url)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (view == IntPtr.Zero) return;
        var request = ObjC.Send(ObjC.Class("NSURLRequest"), ObjC.Sel("requestWithURL:"), ObjC.NsUrlOrNull(url));
        if (request == IntPtr.Zero) return;
        ObjC.SendVoid(view, ObjC.Sel("loadRequest:"), request);
    }

    public void Reload()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (view != IntPtr.Zero) ObjC.SendVoid(view, ObjC.Sel("reload"));
    }

    public string CurrentUrl()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (view == IntPtr.Zero) return "";
        var url = ObjC.Send(view, ObjC.Sel("URL"));
        return url == IntPtr.Zero ? "" : ObjC.FromNSString(ObjC.Send(url, ObjC.Sel("absoluteString")));
    }

    // Runs a script in the page and hands back whatever it produced, as JSON.
    //
    // callAsyncJavaScript is used rather than evaluateJavaScript because it
    // awaits a promise the script returns, which is what a DevTools evaluate
    // with awaitPromise does. The body is wrapped so the result always comes
    // back as one JSON string: an arbitrary Objective-C object graph would have
    // to be walked by hand, and a string needs only to be parsed.
    public Task<string> EvaluateJsonAsync(string expression, CancellationToken token)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!OperatingSystem.IsMacOS() || view == IntPtr.Zero)
        {
            completion.SetException(new IOException("The web panels are only available on macOS."));
            return completion.Task;
        }
        var body = "const __value = await (" + expression + ");\n" +
                   "return JSON.stringify(__value === undefined ? null : __value);";
        Dispatcher.UIThread.Post(() =>
        {
            if (view == IntPtr.Zero)
            {
                completion.TrySetException(new IOException("The web panel was closed."));
                return;
            }
            var world = ObjC.Send(ObjC.Class("WKContentWorld"), ObjC.Sel("pageWorld"));
            var handler = ObjCBlock.CreateValueErrorHandler((value, error) =>
            {
                if (error != IntPtr.Zero)
                {
                    completion.TrySetException(new IOException(ObjC.ErrorMessage(error)));
                    return;
                }
                completion.TrySetResult(ObjC.FromNSString(value));
            });
            ObjC.Send(view, ObjC.Sel("callAsyncJavaScript:arguments:inFrame:inContentWorld:completionHandler:"),
                ObjC.NSString(body), IntPtr.Zero, IntPtr.Zero, world, handler);
        });
        return completion.Task.WaitAsync(token);
    }
}
