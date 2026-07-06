namespace FFMedia.Core.Notifications;

/// <summary>Surfaces <see cref="Notification"/>s to the user. UI implementation lives in the app layer.</summary>
public interface INotificationService
{
    /// <summary>Show a notification. Implementations must be safe to call from any thread.</summary>
    void Notify(Notification notification);
}
