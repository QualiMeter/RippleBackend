using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ripple.Api.Services;

public sealed class AiPlanDocument
{
	[JsonPropertyName("operation")]
	public string Operation { get; set; } = "";

	[JsonPropertyName("summary")]
	public string Summary { get; set; } = "";

	[JsonPropertyName("project")]
	public AiProjectChange? Project { get; set; }

	[JsonPropertyName("employees")]
	public List<AiEmployeeChange> Employees { get; set; } = [];

	[JsonPropertyName("tasks")]
	public List<AiTaskChange> Tasks { get; set; } = [];

	[JsonPropertyName("dependencies")]
	public List<AiDependencyChange> Dependencies { get; set; } = [];
}

public sealed class AiProjectChange
{
	[JsonConverter(typeof(TolerantNullableGuidConverter))]
	public Guid? Id { get; set; }
	public string? TempId { get; set; }
	public string? Name { get; set; }
	public string? StartDate { get; set; }
	public string? EndDate { get; set; }
}

public sealed class AiEmployeeChange
{
	public string Action { get; set; } = "create";
	[JsonConverter(typeof(TolerantNullableGuidConverter))]
	public Guid? Id { get; set; }
	public string? TempId { get; set; }
	public string? Name { get; set; }
	public string? Phone { get; set; }
	public string? Email { get; set; }
}

public sealed class AiTaskChange
{
	public string Action { get; set; } = "create";
	[JsonConverter(typeof(TolerantNullableGuidConverter))]
	public Guid? Id { get; set; }
	public string? TempId { get; set; }
	public string? Name { get; set; }
	public string? StartDate { get; set; }
	public string? EndDate { get; set; }
	public string? Status { get; set; }
	[JsonConverter(typeof(TolerantNullableGuidConverter))]
	public Guid? AssigneeId { get; set; }
	public string? AssigneeTempId { get; set; }
}

public sealed class AiDependencyChange
{
	public string Action { get; set; } = "create";
	[JsonConverter(typeof(TolerantNullableGuidConverter))]
	public Guid? PredecessorTaskId { get; set; }
	[JsonConverter(typeof(TolerantNullableGuidConverter))]
	public Guid? SuccessorTaskId { get; set; }
	public string? PredecessorTempId { get; set; }
	public string? SuccessorTempId { get; set; }
}

internal sealed class TolerantNullableGuidConverter : JsonConverter<Guid?>
{
	public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType == JsonTokenType.Null)
			return null;

		if (reader.TokenType == JsonTokenType.String)
		{
			var value = reader.GetString();
			return Guid.TryParse(value, out var guid) ? guid : null;
		}

		reader.Skip();
		return null;
	}

	public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
	{
		if (value.HasValue)
			writer.WriteStringValue(value.Value);
		else
			writer.WriteNullValue();
	}
}

public sealed record AiPlanContext(
	object? Project,
	IReadOnlyList<object> Employees,
	IReadOnlyList<object> Tasks,
	IReadOnlyList<object> Dependencies);

public sealed record AiPlanBuildResult(AiPlanDocument Document, IReadOnlyList<Ripple.Api.Contracts.AiPlanChangeDto> Changes);

public sealed record OllamaChatResponse(OllamaMessage Message, string? DoneReason);
public sealed record OllamaMessage(string Role, string Content);
