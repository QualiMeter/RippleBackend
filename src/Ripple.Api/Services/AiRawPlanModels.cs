using System.Text.Json.Serialization;

namespace Ripple.Api.Services;

internal sealed class AiRawPlanDocument
{
	[JsonPropertyName("operation")]
	public string Operation { get; set; } = "";

	[JsonPropertyName("summary")]
	public string Summary { get; set; } = "";

	[JsonPropertyName("project")]
	public AiRawProjectChange? Project { get; set; }

	[JsonPropertyName("employees")]
	public List<AiRawEmployeeChange> Employees { get; set; } = [];

	[JsonPropertyName("tasks")]
	public List<AiRawTaskChange> Tasks { get; set; } = [];

	[JsonPropertyName("dependencies")]
	public List<AiRawDependencyChange> Dependencies { get; set; } = [];
}

internal sealed class AiRawProjectChange
{
	public string? Id { get; set; }
	public string? TempId { get; set; }
	public string? Name { get; set; }
	public string? StartDate { get; set; }
	public string? EndDate { get; set; }
}

internal sealed class AiRawEmployeeChange
{
	public string Action { get; set; } = "create";
	public string? Id { get; set; }
	public string? TempId { get; set; }
	public string? Name { get; set; }
	public string? Phone { get; set; }
	public string? Email { get; set; }
}

internal sealed class AiRawTaskChange
{
	public string Action { get; set; } = "create";
	public string? Id { get; set; }
	public string? TempId { get; set; }
	public string? Name { get; set; }
	public string? StartDate { get; set; }
	public string? EndDate { get; set; }
	public string? Status { get; set; }
	public string? AssigneeId { get; set; }
	public string? AssigneeTempId { get; set; }
}

internal sealed class AiRawDependencyChange
{
	public string Action { get; set; } = "create";
	public string? PredecessorTaskId { get; set; }
	public string? SuccessorTaskId { get; set; }
	public string? PredecessorTempId { get; set; }
	public string? SuccessorTempId { get; set; }
}
