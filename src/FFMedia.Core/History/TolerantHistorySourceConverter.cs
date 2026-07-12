using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFMedia.Core.History;

/// <summary>Reads <see cref="HistorySource"/> without ever throwing: an unrecognized name, a null,
/// or a number all fall back to <see cref="HistorySource.Download"/>. Writes by name.</summary>
/// <remarks><para>The strict converter's failure mode here is catastrophically out of proportion to
/// the fault. <c>JsonStore&lt;T&gt;</c> quarantines a file it cannot parse to <c>.bak</c> and returns
/// a default — so <em>one</em> unreadable <c>Source</c> value does not skip that row, it silently
/// erases the user's <em>entire</em> history.</para>
/// <para>That is not hypothetical for long. FFMedia is a toolbox that gains tools; the day a third
/// member ships, every older build that reads the newer <c>history.json</c> throws away everything
/// the user ever did. A history row whose origin we cannot name is still a history row worth
/// keeping, so degrade the field, never the file.</para></remarks>
public sealed class TolerantHistorySourceConverter : JsonConverter<HistorySource>
{
    public override HistorySource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Deliberately total. Anything we cannot interpret means "written by something we don't
        // know about" — which is a reason to keep the row, not to destroy the file it lives in.
        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse<HistorySource>(reader.GetString(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return HistorySource.Download;
    }

    public override void Write(Utf8JsonWriter writer, HistorySource value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}
