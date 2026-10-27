using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NgBubbles.Core;

public sealed class ReactionTypeConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number)) return number;
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Unexpected reaction type format.");
        var value = reader.GetString();
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        return value switch
        {
            "sticker" => 1000,
            "love" => 2000, "like" => 2001, "dislike" => 2002,
            "laugh" => 2003, "emphasize" => 2004, "question" => 2005,
            "-love" => 3000, "-like" => 3001, "-dislike" => 3002,
            "-laugh" => 3003, "-emphasize" => 3004, "-question" => 3005,
            _ => -1
        };
    }
    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}
