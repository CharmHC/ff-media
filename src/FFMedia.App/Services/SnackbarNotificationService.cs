using System.Windows;
using FFMedia.Core.Notifications;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FFMedia.App.Services;

/// <summary>Shows <see cref="Notification"/>s as WPF-UI snackbars. Safe to call from any thread —
/// marshals onto the UI dispatcher before touching the presenter.</summary>
public sealed class SnackbarNotificationService : INotificationService
{
    private readonly ISnackbarService _snackbar;

    public SnackbarNotificationService(ISnackbarService snackbar)
    {
        ArgumentNullException.ThrowIfNull(snackbar);
        _snackbar = snackbar;
    }

    public void Notify(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var app = Application.Current;
        if (app?.Dispatcher is null) return;

        app.Dispatcher.Invoke(() => _snackbar.Show(
            notification.Title,
            notification.Message,
            Map(notification.Severity),
            null!,
            TimeSpan.FromSeconds(4)));
    }

    private static ControlAppearance Map(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Success => ControlAppearance.Success,
        NotificationSeverity.Warning => ControlAppearance.Caution,
        NotificationSeverity.Error => ControlAppearance.Danger,
        _ => ControlAppearance.Info,
    };
}
