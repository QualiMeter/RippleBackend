using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProjectManagement.Api.Data;
using ProjectManagement.Api.Domain;

namespace ProjectManagement.Api.Services;

public sealed record ChangeHistoryDto(Guid Id, string OperationType, string Description, DateTimeOffset CreatedAt, bool CanUndo);
public sealed record ChangeHistoryItemDto(string EntityType, Guid EntityId, string? BeforeJson, string? AfterJson);

public sealed class ChangeHistoryService(AppDbContext db)
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	public ChangeOperation Begin(Guid projectId, string operationType, string description)
	{
		return new ChangeOperation
		{
			Id = Guid.NewGuid(),
			ProjectId = projectId,
			OperationType = operationType,
			Description = description,
			CreatedAt = DateTimeOffset.UtcNow
		};
	}

	public void Add<T>(ChangeOperation operation, string entityType, Guid entityId, T? before, T? after)
	{
		operation.Items.Add(new ChangeItem
		{
			Id = Guid.NewGuid(),
			EntityType = entityType,
			EntityId = entityId,
			BeforeJson = before is null ? null : JsonSerializer.Serialize(before, JsonOptions),
			AfterJson = after is null ? null : JsonSerializer.Serialize(after, JsonOptions)
		});
	}

	public async Task<IReadOnlyList<ChangeHistoryDto>> GetAsync(Guid projectId, CancellationToken ct)
	{
		return await db.ChangeOperations.AsNoTracking()
			.Where(x => x.ProjectId == projectId)
			.OrderByDescending(x => x.CreatedAt)
			.Take(100)
			.Select(x => new ChangeHistoryDto(x.Id, x.OperationType, x.Description, x.CreatedAt, x.UndoneAt == null))
			.ToListAsync(ct);
	}

	public async Task<IReadOnlyList<ChangeHistoryItemDto>> GetItemsAsync(Guid operationId, CancellationToken ct)
	{
		return await db.ChangeItems.AsNoTracking()
			.Where(x => x.OperationId == operationId)
			.OrderBy(x => x.EntityType)
			.ThenBy(x => x.EntityId)
			.Select(x => new ChangeHistoryItemDto(x.EntityType, x.EntityId, x.BeforeJson, x.AfterJson))
			.ToListAsync(ct);
	}

	public async Task<ChangeHistoryDto?> UndoAsync(Guid projectId, CancellationToken ct)
	{
		var operation = await db.ChangeOperations
			.Include(x => x.Items)
			.Where(x => x.ProjectId == projectId && x.UndoneAt == null)
			.OrderByDescending(x => x.CreatedAt)
			.FirstOrDefaultAsync(ct);
		if (operation is null) return null;

		var strategy = db.Database.CreateExecutionStrategy();
		return await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await db.Database.BeginTransactionAsync(ct);
			await ApplyUndoAsync(operation, ct);
			operation.UndoneAt = DateTimeOffset.UtcNow;
			await db.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);
			return new ChangeHistoryDto(operation.Id, operation.OperationType, operation.Description, operation.CreatedAt, false);
		});
	}

	private async Task ApplyUndoAsync(ChangeOperation operation, CancellationToken ct)
	{
		var creates = operation.Items.Where(x => x.BeforeJson is null).OrderByDescending(x => RestoreOrder(x.EntityType)).ToList();
		var updates = operation.Items.Where(x => x.BeforeJson is not null && x.AfterJson is not null).ToList();
		var deletes = operation.Items.Where(x => x.BeforeJson is not null && x.AfterJson is null).OrderBy(x => RestoreOrder(x.EntityType)).ToList();

		foreach (var item in creates)
		{
			await DeleteEntityAsync(item, ct);
		}

		foreach (var item in updates)
		{
			await RestoreEntityAsync(item, item.BeforeJson!, ct);
		}

		foreach (var item in deletes)
		{
			await RestoreEntityAsync(item, item.BeforeJson!, ct);
		}
	}

	private async Task DeleteEntityAsync(ChangeItem item, CancellationToken ct)
	{
		switch (item.EntityType)
		{
			case "task_dependency":
				var dependency = await db.TaskDependencies.SingleOrDefaultAsync(x => x.PredecessorTaskId == ReadDependency(item.AfterJson!).PredecessorTaskId && x.SuccessorTaskId == ReadDependency(item.AfterJson!).SuccessorTaskId, ct);
				if (dependency is not null) db.TaskDependencies.Remove(dependency);
				break;
			case "task":
				var task = await db.Tasks.SingleOrDefaultAsync(x => x.Id == item.EntityId, ct);
				if (task is not null) db.Tasks.Remove(task);
				break;
			case "employee":
				var employee = await db.Employees.SingleOrDefaultAsync(x => x.Id == item.EntityId, ct);
				if (employee is not null) db.Employees.Remove(employee);
				break;
			case "project":
				var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == item.EntityId, ct);
				if (project is not null) db.Projects.Remove(project);
				break;
		}
	}

	private async Task RestoreEntityAsync(ChangeItem item, string json, CancellationToken ct)
	{
		switch (item.EntityType)
		{
			case "project":
				var project = JsonSerializer.Deserialize<ProjectSnapshot>(json, JsonOptions)!;
				var existingProject = await db.Projects.SingleOrDefaultAsync(x => x.Id == project.Id, ct);
				if (existingProject is null) db.Projects.Add(project.ToEntity()); else project.Apply(existingProject);
				break;
			case "employee":
				var employee = JsonSerializer.Deserialize<EmployeeSnapshot>(json, JsonOptions)!;
				var existingEmployee = await db.Employees.SingleOrDefaultAsync(x => x.Id == employee.Id, ct);
				if (existingEmployee is null) db.Employees.Add(employee.ToEntity()); else employee.Apply(existingEmployee);
				break;
			case "task":
				var task = JsonSerializer.Deserialize<TaskSnapshot>(json, JsonOptions)!;
				var existingTask = await db.Tasks.SingleOrDefaultAsync(x => x.Id == task.Id, ct);
				if (existingTask is null) db.Tasks.Add(task.ToEntity()); else task.Apply(existingTask);
				break;
			case "task_dependency":
				var dependency = JsonSerializer.Deserialize<DependencySnapshot>(json, JsonOptions)!;
				if (!await db.TaskDependencies.AnyAsync(x => x.PredecessorTaskId == dependency.PredecessorTaskId && x.SuccessorTaskId == dependency.SuccessorTaskId, ct))
					db.TaskDependencies.Add(dependency.ToEntity());
				break;
		}
	}

	private static int RestoreOrder(string type) => type switch
	{
		"project" => 0,
		"employee" => 1,
		"task" => 2,
		"task_dependency" => 3,
		_ => 10
	};

	private static DependencySnapshot ReadDependency(string json) => JsonSerializer.Deserialize<DependencySnapshot>(json, JsonOptions)!;

	public static ProjectSnapshot Snapshot(Project x) => new(x.Id, x.Name, x.StartDate, x.EndDate, x.CreatorId);
	public static EmployeeSnapshot Snapshot(Employee x) => new(x.Id, x.ProjectId, x.Name, x.Phone, x.Email);
	public static TaskSnapshot Snapshot(ProjectTask x) => new(x.Id, x.ProjectId, x.AssigneeId, x.Name, x.StartDate, x.EndDate, x.Status);
	public static DependencySnapshot Snapshot(TaskDependency x) => new(x.PredecessorTaskId, x.SuccessorTaskId, x.CreatedAt);

	public sealed record ProjectSnapshot(Guid Id, string Name, DateOnly StartDate, DateOnly EndDate, Guid CreatorId)
	{
		public Project ToEntity() => new() { Id = Id, Name = Name, StartDate = StartDate, EndDate = EndDate, CreatorId = CreatorId };
		public void Apply(Project x) { x.Name = Name; x.StartDate = StartDate; x.EndDate = EndDate; x.CreatorId = CreatorId; }
	}

	public sealed record EmployeeSnapshot(Guid Id, Guid ProjectId, string Name, string? Phone, string? Email)
	{
		public Employee ToEntity() => new() { Id = Id, ProjectId = ProjectId, Name = Name, Phone = Phone, Email = Email };
		public void Apply(Employee x) { x.ProjectId = ProjectId; x.Name = Name; x.Phone = Phone; x.Email = Email; }
	}

	public sealed record TaskSnapshot(Guid Id, Guid ProjectId, Guid AssigneeId, string Name, DateOnly StartDate, DateOnly EndDate, ProjectTaskStatus Status)
	{
		public ProjectTask ToEntity() => new() { Id = Id, ProjectId = ProjectId, AssigneeId = AssigneeId, Name = Name, StartDate = StartDate, EndDate = EndDate, Status = Status };
		public void Apply(ProjectTask x) { x.ProjectId = ProjectId; x.AssigneeId = AssigneeId; x.Name = Name; x.StartDate = StartDate; x.EndDate = EndDate; x.Status = Status; }
	}

	public sealed record DependencySnapshot(Guid PredecessorTaskId, Guid SuccessorTaskId, DateTimeOffset CreatedAt)
	{
		public TaskDependency ToEntity() => new() { PredecessorTaskId = PredecessorTaskId, SuccessorTaskId = SuccessorTaskId, CreatedAt = CreatedAt };
	}
}
