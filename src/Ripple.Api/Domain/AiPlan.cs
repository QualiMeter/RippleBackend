namespace Ripple.Api.Domain;

public enum AiPlanStatus
{
	Pending = 0,
	Confirmed = 1,
	Rejected = 2
}

public sealed class AiPlan
{
	public Guid Id { get; set; }
	public Guid? ProjectId { get; set; }
	public Guid CreatorId { get; set; }
	public string Prompt { get; set; } = null!;
	public string OperationType { get; set; } = null!;
	public string Summary { get; set; } = null!;
	public string PlanJson { get; set; } = null!;
	public string? ContextHash { get; set; }
	public AiPlanStatus Status { get; set; }
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset? ConfirmedAt { get; set; }
}
