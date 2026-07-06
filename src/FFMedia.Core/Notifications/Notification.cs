namespace FFMedia.Core.Notifications;

/// <summary>A user-facing message raised by the app (e.g. a download finished or failed).</summary>
public sealed record Notification(string Title, string Message, NotificationSeverity Severity);
