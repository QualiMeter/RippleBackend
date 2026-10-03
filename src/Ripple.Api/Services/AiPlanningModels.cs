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
	public Guid? Id { get; set; }
	public string? Name { get; set; }
	public string? StartDate { get; set; }
	public string? EndDate { get; set; }
}

public sealed class AiEmployeeChange
{
	public string Action { get; set; } = "create";
	public Guid? Id { get; set; }
	public string? TempId { get; set; }
	public string? Name { get; set; }
	public string? Phone { get; set; }
	public string? Email { get; set; }
}

public sealed class AiTaskChange
{
	public string Action { get; set; } = "create";
	public Guid? Id { get; set; }
	public string? TempId { get; set; }
	public string? Name { get; set; }
	public string? StartDate { get; set; }
	public string? EndDate { get; set; }
	public string? Status { get; set; }
	public Guid? AssigneeId { get; set; }
	public string? AssigneeTempId { get; set; }
}

public sealed class AiDependencyChange
{
	public string Action { get; set; } = "create";
	public Guid? PredecessorTaskId { get; set; }
	public Guid? SuccessorTaskId { get; set; }
	public string? PredecessorTempId { get; set; }
	public string? SuccessorTempId { get; set; }
}

public sealed record AiPlanContext(
	object? Project,
	IReadOnlyList<object> Employees,
	IReadOnlyList<object> Tasks,
	IReadOnlyList<object> Dependencies);

public sealed record AiPlanBuildResult(AiPlanDocument Document, IReadOnlyList<Ripple.Api.Contracts.AiPlanChangeDto> Changes);

public sealed record OllamaChatResponse(OllamaMessage Message);
public sealed record OllamaMessage(string Role, string Content);
