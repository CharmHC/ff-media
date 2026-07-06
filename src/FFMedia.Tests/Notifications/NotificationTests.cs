using FFMedia.Core.Notifications;
using Xunit;

namespace FFMedia.Tests.Notifications;

public class NotificationTests
{
    [Fact]
    public void Notification_CarriesTitleMessageSeverity()
    {
        var n = new Notification("Done", "\"Clip\" finished.", NotificationSeverity.Success);

        Assert.Equal("Done", n.Title);
        Assert.Equal("\"Clip\" finished.", n.Message);
        Assert.Equal(NotificationSeverity.Success, n.Severity);
    }
}
