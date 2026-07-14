using System.Globalization;
using System.Windows.Data;
using FFMedia.Core.Media;

namespace FFMedia.Ui.Views;

/// <summary>A <see cref="TimeSpan"/> as the <c>double</c> seconds a <see cref="System.Windows.Controls.Slider"/>
/// speaks. <c>ConvertBack</c> is what makes the seek slider a real TwoWay control — binding straight to
/// <c>Position.TotalSeconds</c> would not work at all, because <see cref="TimeSpan.TotalSeconds"/> has no
/// setter.</summary>
public sealed class TimeSpanSecondsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is TimeSpan span ? span.TotalSeconds : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double seconds && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
}

/// <summary>Formats a <see cref="TimeSpan"/> the same way the range boxes are formatted
/// (<see cref="TrimParsing.Format"/>) — so the duration readout under the player never disagrees with
/// what a capture would write into a Start/End box.</summary>
public sealed class TrimFormatConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is TimeSpan span ? TrimParsing.Format(span) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("Read-only: the duration readout is never written back.");
}
