namespace Ripple.Api.Domain;

public sealed class ChangeOperation
{
	public Guid Id { get; set; }
	public Guid? ProjectId { get; set; }
	public string OperationType { get; set; } = null!;
	public string Description { get; set; } = null!;
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset? UndoneAt { get; set; }
	public Project? Project { get; set; }
	public ICollection<ChangeItem> Items { get; set; } = [];
}

public sealed class ChangeItem
{
	public Guid Id { get; set; }
	public Guid OperationId { get; set; }
	public string EntityType { get; set; } = null!;
	public Guid EntityId { get; set; }
	public string? BeforeJson { get; set; }
	public string? AfterJson { get; set; }
	public ChangeOperation Operation { get; set; } = null!;
}
