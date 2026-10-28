using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Extensions.Json.Converters
{
    /// <summary>
    /// Legacy DateTime converter.
    /// Milliseconds aren't output if zero by default.
    /// </summary>
    public class JsonDateTimeConverter : JsonConverter<DateTime>
    {
        /// <inheritdoc />
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.GetDateTime();
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            if (value.Millisecond == 0)
            {
                // Remaining ticks value will be 0, manually format. Dates stored in the database mostly have
                // no milliseconds, so this is the common path: format on the stack rather than allocating.
                Span<char> buffer = stackalloc char[32];
                if (value.TryFormat(buffer, out var written, "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffffZ", CultureInfo.InvariantCulture))
                {
                    writer.WriteStringValue(buffer[..written]);
                }
                else
                {
                    writer.WriteStringValue(value.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffffZ", CultureInfo.InvariantCulture));
                }
            }
            else
            {
                writer.WriteStringValue(value);
            }
        }
    }
}
