using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ripple.Api.Data;
using Ripple.Api.Domain;

namespace Ripple.Api.Services;

public sealed record ChangeHistoryDto(Guid Id, string OperationType, string Description, DateTimeOffset CreatedAt, bool IsCurrent);
public sealed record ChangeHistoryItemDto(string EntityType, Guid EntityId, string? BeforeJson, string? AfterJson);

public sealed class ChangeHistoryService(AppDbContext db)
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	public ChangeOperation Begin(Guid projectId, string operationType, string description)
	{
		var now = DateTimeOffset.UtcNow;
		return new ChangeOperation
		{
			Id = Guid.NewGuid(),
			ProjectId = projectId,
			OperationType = operationType,
			Description = description,
			CreatedAt = now,
			// Existing column is used as a persistent marker of the version
			// that represents the current project state. It does not disable undo.
			UndoneAt = now
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
		var operations = await db.ChangeOperations.AsNoTracking()
			.Where(x => x.ProjectId == projectId)
			.OrderByDescending(x => x.CreatedAt)
			.ToListAsync(ct);

		var current = operations
			.Where(x => x.UndoneAt.HasValue)
			.OrderByDescending(x => x.UndoneAt)
			.FirstOrDefault();

		// Projects created before the current-version marker was introduced have
		// no marker yet. Their newest history entry is the current version.
		current ??= operations.FirstOrDefault();

		return operations
			.Select(x => new ChangeHistoryDto(
				x.Id,
				x.OperationType,
				x.Description,
				x.CreatedAt,
				x.Id == current?.Id))
			.ToList();
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

	public async Task<ChangeHistoryDto?> UndoAsync(Guid projectId, Guid historyId, CancellationToken ct)
	{
		var target = await db.ChangeOperations
			.AsNoTracking()
			.SingleOrDefaultAsync(x => x.Id == historyId && x.ProjectId == projectId, ct);
		if (target is null) return null;

		var operations = await db.ChangeOperations
			.AsNoTracking()
			.Include(x => x.Items)
			.Where(x => x.ProjectId == projectId)
			.OrderBy(x => x.CreatedAt)
			.ThenBy(x => x.Id)
			.ToListAsync(ct);

		var targetIndex = operations.FindIndex(x => x.Id == historyId);
		if (targetIndex < 0) return null;

		var strategy = db.Database.CreateExecutionStrategy();
		return await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await db.Database.BeginTransactionAsync(ct);

			await ClearProjectStateAsync(projectId, ct);

			for (var i = 0; i <= targetIndex; i++)
				await ApplyForwardAsync(operations[i], ct);

			target.UndoneAt = DateTimeOffset.UtcNow;
			db.ChangeOperations.Update(target);
			await db.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);

			return new ChangeHistoryDto(
				target.Id,
				target.OperationType,
				target.Description,
				target.CreatedAt,
				true);
		});
	}

	private async Task ClearProjectStateAsync(Guid projectId, CancellationToken ct)
	{
		var dependencies = await db.TaskDependencies
			.Where(x => x.PredecessorTask.ProjectId == projectId)
			.ToListAsync(ct);
		db.TaskDependencies.RemoveRange(dependencies);

		var tasks = await db.Tasks.Where(x => x.ProjectId == projectId).ToListAsync(ct);
		db.Tasks.RemoveRange(tasks);

		var employees = await db.Employees.Where(x => x.ProjectId == projectId).ToListAsync(ct);
		db.Employees.RemoveRange(employees);

		await db.SaveChangesAsync(ct);

		// The entities removed above remain tracked as Deleted. A later restore
		// may add new instances with the same keys, which causes EF Core's
		// identity-map conflict. Clear the tracker before rebuilding the version.
		db.ChangeTracker.Clear();
	}

	private async Task ApplyForwardAsync(ChangeOperation operation, CancellationToken ct)
	{
		foreach (var item in operation.Items.OrderBy(x => ForwardOrder(x.EntityType)))
		{
			if (item.AfterJson is null)
				await DeleteEntityAsync(item, ct, useAfterSnapshot: false);
			else
				await RestoreEntityAsync(item, item.AfterJson, ct);
		}

		// Persist one history operation at a time. This prevents an entity that was
		// deleted/recreated while replaying the version from remaining in the
		// identity map when the next operation is applied.
		await db.SaveChangesAsync(ct);
		db.ChangeTracker.Clear();
	}

	private static int ForwardOrder(string type) => type switch
	{
		"project" => 0,
		"employee" => 1,
		"task" => 2,
		"task_dependency" => 3,
		_ => 10
	};

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

	private async Task DeleteEntityAsync(ChangeItem item, CancellationToken ct, bool useAfterSnapshot = true)
	{
		switch (item.EntityType)
		{
			case "task_dependency":
				var dependencySnapshot = useAfterSnapshot ? item.AfterJson : item.BeforeJson;
				var dependency = dependencySnapshot is null ? null : await db.TaskDependencies.SingleOrDefaultAsync(x => x.PredecessorTaskId == ReadDependency(dependencySnapshot).PredecessorTaskId && x.SuccessorTaskId == ReadDependency(dependencySnapshot).SuccessorTaskId, ct);
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
				if (existingProject is null)
				{
					DetachTracked<Project>(project.Id);
					db.Projects.Add(project.ToEntity());
				}
				else
				{
					EnsureRestorable(existingProject);
					project.Apply(existingProject);
				}
				break;

			case "employee":
				var employee = JsonSerializer.Deserialize<EmployeeSnapshot>(json, JsonOptions)!;
				var existingEmployee = await db.Employees.SingleOrDefaultAsync(x => x.Id == employee.Id, ct);
				if (existingEmployee is null)
				{
					DetachTracked<Employee>(employee.Id);
					db.Employees.Add(employee.ToEntity());
				}
				else
				{
					EnsureRestorable(existingEmployee);
					employee.Apply(existingEmployee);
				}
				break;

			case "task":
				var task = JsonSerializer.Deserialize<TaskSnapshot>(json, JsonOptions)!;
				var existingTask = await db.Tasks.SingleOrDefaultAsync(x => x.Id == task.Id, ct);
				if (existingTask is null)
				{
					DetachTracked<ProjectTask>(task.Id);
					db.Tasks.Add(task.ToEntity());
				}
				else
				{
					EnsureRestorable(existingTask);
					task.Apply(existingTask);
				}
				break;

			case "task_dependency":
				var dependency = JsonSerializer.Deserialize<DependencySnapshot>(json, JsonOptions)!;
				var existingDependency = await db.TaskDependencies.SingleOrDefaultAsync(
					x => x.PredecessorTaskId == dependency.PredecessorTaskId && x.SuccessorTaskId == dependency.SuccessorTaskId, ct);
				if (existingDependency is null)
				{
					db.ChangeTracker.Entries<TaskDependency>()
						.Where(x => x.Entity.PredecessorTaskId == dependency.PredecessorTaskId && x.Entity.SuccessorTaskId == dependency.SuccessorTaskId)
						.ToList()
						.ForEach(x => x.State = EntityState.Detached);
					db.TaskDependencies.Add(dependency.ToEntity());
				}
				else
				{
					EnsureRestorable(existingDependency);
				}
				break;
		}
	}

	private void DetachTracked<TEntity>(Guid id) where TEntity : class
	{
		foreach (var entry in db.ChangeTracker.Entries<TEntity>().Where(x => GetEntityId(x.Entity) == id).ToList())
			entry.State = EntityState.Detached;
	}

	private static Guid GetEntityId<TEntity>(TEntity entity) where TEntity : class => entity switch
	{
		Project x => x.Id,
		Employee x => x.Id,
		ProjectTask x => x.Id,
		_ => Guid.Empty
	};

	private void EnsureRestorable<TEntity>(TEntity entity) where TEntity : class
	{
		var entry = db.Entry(entity);
		if (entry.State == EntityState.Deleted)
			entry.State = EntityState.Unchanged;
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
