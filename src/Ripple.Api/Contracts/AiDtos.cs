namespace Ripple.Api.Contracts;

public sealed record AiPlanRequest(string Prompt);

public sealed record AiPlanChangeDto(
	string Action,
	string EntityType,
	Guid? EntityId,
	string Label,
	string? Field,
	string? Before,
	string? After);

public sealed record AiPlanDto(
	Guid PlanId,
	Guid? ProjectId,
	string OperationType,
	string Status,
	string Summary,
	IReadOnlyList<AiPlanChangeDto> Changes,
	DateTimeOffset CreatedAt,
	DateTimeOffset? ConfirmedAt);
