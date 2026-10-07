using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ripple.Api.Services;

/// <summary>
/// Ollama sometimes returns human-readable placeholder identifiers such as
/// "task_114" instead of a real GUID. For the AI plan contract those values
/// are treated as stable synthetic IDs; real domain IDs remain unchanged.
/// </summary>
public sealed class AiGuidJsonConverter : JsonConverter<Guid?>
{
	public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType == JsonTokenType.Null)
			return null;

		if (reader.TokenType != JsonTokenType.String)
			throw new JsonException($"AI identifier must be a string or null, got {reader.TokenType}.");

		var value = reader.GetString();
		if (string.IsNullOrWhiteSpace(value))
			return null;

		if (Guid.TryParse(value, out var guid))
			return guid;

		return CreateSyntheticGuid(value);
	}

	public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
	{
		if (value.HasValue)
			writer.WriteStringValue(value.Value);
		else
			writer.WriteNullValue();
	}

	public static Guid CreateSyntheticGuid(string value)
	{
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
		Span<byte> bytes = stackalloc byte[16];
		hash.AsSpan(0, 16).CopyTo(bytes);

		// Mark it as a UUID-like version 5 value so it cannot be confused with
		// arbitrary byte data. This is only an internal AI reference.
		bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
		bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
		return new Guid(bytes);
	}
}
