using System;
using Velopack;

namespace FFMedia.App;

/// <summary>
/// Explicit entry point. Velopack MUST run before WPF starts so it can service its
/// install/update/uninstall hooks (this returns immediately in normal runs).
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
