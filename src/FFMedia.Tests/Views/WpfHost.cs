using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace FFMedia.Tests.Views;

/// <summary>One STA thread and one <see cref="Application"/>, shared by every test that builds a real
/// page.
///
/// <para>WPF allows exactly <b>one</b> <c>Application</c> per AppDomain, and it belongs to the thread
/// that created it. Each WPF test class used to start its own STA thread and call
/// <c>Application.Current ?? new Application()</c> — which worked only while exactly one such class
/// existed. The moment a second appeared, the two raced: whichever lost threw
/// <i>"Cannot create more than one System.Windows.Application instance in the same AppDomain"</i>, and
/// pages built against an <c>Application</c> owned by a different (or already-dead) thread threw
/// <c>XamlParseException</c> while resolving WPF-UI's resources. Both are confusing failures a long way
/// from their cause.</para>
///
/// <para>So: one host, created once, owning the Application and the merged dictionaries; every page is
/// built on its dispatcher. xUnit shares it across the <c>wpf</c> collection, which also serializes
/// those classes — WPF has global state and cannot be exercised from two threads at once.</para></summary>
public sealed class WpfHost : IDisposable
{
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;

    public WpfHost()
    {
        var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;

        _thread = new Thread(() =>
        {
            // Mirrors App.xaml. Without ControlsDictionary every WPF-UI style lookup on a page fails.
            var app = Application.Current ?? new Application();

            // Load-bearing: WPF's default ShutdownMode is OnLastWindowClose, so the FIRST test that
            // closes its window tears the Application down — and every test after it dies with
            // "The Application object is being shut down" while resolving a pack:// resource. The host
            // owns the Application's lifetime; only Dispose ends it.
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary());
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());

            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();

            Dispatcher.Run(); // pumps until InvokeShutdown, so the Application stays alive and owned here
        })
        {
            IsBackground = true,
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        _dispatcher = dispatcher!;
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread and returns whatever it threw, so a test
    /// can assert on the failure instead of losing it on a background thread.</summary>
    public Exception? Run(Action action)
    {
        Exception? captured = null;
        _dispatcher.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        return captured;
    }

    public void Dispose()
    {
        _dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}

/// <summary>Serializes every WPF test class onto the one <see cref="WpfHost"/>.</summary>
[CollectionDefinition("wpf")]
public sealed class WpfCollection : ICollectionFixture<WpfHost>
{
}
