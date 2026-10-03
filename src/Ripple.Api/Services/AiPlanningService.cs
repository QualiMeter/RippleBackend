using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ripple.Api.Contracts;
using Ripple.Api.Data;
using Ripple.Api.Domain;

namespace Ripple.Api.Services;

public sealed class AiPlanningService(
	AppDbContext db,
	ICurrentUserAccessor currentUser,
	IOllamaClient ollama,
	ChangeHistoryService history,
	DependencyGraphService graph,
	AnalysisService analysis,
	IRealtimeNotifier realtime)
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		PropertyNameCaseInsensitive = true
	};

	public async Task<AiPlanDto> CreatePlanAsync(Guid? projectId, string prompt, CancellationToken ct)
	{
		var userId = await currentUser.GetUserIdAsync(ct);
		AiPlanContext context;

		if (projectId.HasValue)
		{
			await EnsureProjectOwnerAsync(projectId.Value, userId, ct);
			context = await BuildContextAsync(projectId.Value, ct);
		}
		else
		{
			context = new AiPlanContext(null, [], [], []);
		}

		var document = await ollama.CreatePlanAsync(prompt, context, ct);
		await ValidateDocumentAsync(document, projectId, context, ct);
		var contextHash = projectId.HasValue ? ComputeContextHash(context) : null;
		var changes = await BuildPreviewChangesAsync(document, projectId, ct);

		if (projectId.HasValue && !string.Equals(document.Operation, "update_project", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("An existing project can only receive an update_project AI plan.");
		if (!projectId.HasValue && !string.Equals(document.Operation, "create_project", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("A new AI plan must create a project.");

		var entity = new AiPlan
		{
			Id = Guid.NewGuid(),
			ProjectId = projectId,
			CreatorId = userId,
			Prompt = prompt.Trim(),
			OperationType = document.Operation,
			Summary = string.IsNullOrWhiteSpace(document.Summary) ? "Предложение изменений проекта." : document.Summary.Trim(),
			PlanJson = JsonSerializer.Serialize(document, JsonOptions),
			ContextHash = contextHash,
			Status = AiPlanStatus.Pending,
			CreatedAt = DateTimeOffset.UtcNow
		};

		db.AiPlans.Add(entity);
		await db.SaveChangesAsync(ct);
		return ToDto(entity, changes);
	}

	public async Task<AiPlanDto?> GetPlanAsync(Guid planId, CancellationToken ct)
	{
		var userId = await currentUser.GetUserIdAsync(ct);
		var plan = await db.AiPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == planId && x.CreatorId == userId, ct);
		if (plan is null) return null;

		var document = Deserialize(plan.PlanJson);
		var changes = await BuildPreviewChangesAsync(document, plan.ProjectId, ct);
		return ToDto(plan, changes);
	}

	public async Task<AiPlanDto?> ConfirmAsync(Guid planId, Guid? expectedProjectId, CancellationToken ct)
	{
		var userId = await currentUser.GetUserIdAsync(ct);
		var plan = await db.AiPlans.SingleOrDefaultAsync(x => x.Id == planId && x.CreatorId == userId, ct);
		if (plan is null) return null;
		if (plan.Status != AiPlanStatus.Pending)
			throw new InvalidOperationException("AI plan is no longer pending.");
		if (expectedProjectId.HasValue && plan.ProjectId != expectedProjectId)
			throw new InvalidOperationException("AI plan does not belong to this project.");

		var document = Deserialize(plan.PlanJson);
		AiPlanContext? currentContext = null;
		if (plan.ProjectId.HasValue)
		{
			currentContext = await BuildContextAsync(plan.ProjectId.Value, ct);
			await ValidateDocumentAsync(document, plan.ProjectId, currentContext, ct);
			var currentHash = ComputeContextHash(currentContext);
			if (!string.Equals(plan.ContextHash, currentHash, StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("Project changed after the AI preview was created. Generate a new AI plan before confirming.");
		}
		else
		{
			await ValidateDocumentAsync(document, null, new AiPlanContext(null, [], [], []), ct);
		}
		var strategy = db.Database.CreateExecutionStrategy();

		return await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await db.Database.BeginTransactionAsync(ct);
			var changes = await ApplyAsync(plan, document, userId, ct);
			plan.Status = AiPlanStatus.Confirmed;
			plan.ConfirmedAt = DateTimeOffset.UtcNow;
			await db.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);

			if (plan.ProjectId.HasValue)
			{
				foreach (var change in changes.Where(x => x.EntityId.HasValue).GroupBy(x => (x.EntityType, x.EntityId)))
				{
					await realtime.PublishAsync(plan.ProjectId.Value, change.Key.EntityType, "updated", change.Key.EntityId, new { aiPlanId = plan.Id }, ct);
				}
				await realtime.PublishAsync(plan.ProjectId.Value, "history", "created", null, new { aiPlanId = plan.Id }, ct);
				await analysis.AnalyzeProjectAsync(plan.ProjectId.Value, ct);
			}

			return ToDto(plan, changes);
		});
	}

	private async Task<List<AiPlanChangeDto>> ApplyAsync(AiPlan plan, AiPlanDocument document, Guid userId, CancellationToken ct)
	{
		if (document.Operation == "create_project")
			return await ApplyCreateAsync(plan, document, userId, ct);
		if (document.Operation == "update_project")
			return await ApplyUpdateAsync(plan, document, ct);
		throw new InvalidOperationException($"Unsupported AI operation: {document.Operation}");
	}

	private async Task<List<AiPlanChangeDto>> ApplyCreateAsync(AiPlan plan, AiPlanDocument document, Guid userId, CancellationToken ct)
	{
		var p = document.Project ?? throw new InvalidOperationException("AI plan does not contain project data.");
		var start = ParseDate(p.StartDate, "project.startDate");
		var end = ParseDate(p.EndDate, "project.endDate");
		if (string.IsNullOrWhiteSpace(p.Name)) throw new InvalidOperationException("AI plan does not contain a project name.");
		if (start > end) throw new InvalidOperationException("AI plan has invalid project dates.");

		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = p.Name.Trim(),
			StartDate = start,
			EndDate = end,
			CreatorId = userId
		};
		var operation = history.Begin(project.Id, "ai.project.create", $"ИИ создала проект: {project.Name}");
		history.Add(operation, "project", project.Id, null, ChangeHistoryService.Snapshot(project));
		db.Projects.Add(project);

		var employeeMap = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in document.Employees.Where(x => string.Equals(x.Action, "create", StringComparison.OrdinalIgnoreCase)))
		{
			if (string.IsNullOrWhiteSpace(item.TempId) || string.IsNullOrWhiteSpace(item.Name))
				throw new InvalidOperationException("Every AI-created employee must have tempId and name.");
			var employee = new Employee { Id = Guid.NewGuid(), ProjectId = project.Id, Name = item.Name.Trim(), Phone = item.Phone, Email = item.Email };
			employeeMap[item.TempId] = employee.Id;
			history.Add(operation, "employee", employee.Id, null, ChangeHistoryService.Snapshot(employee));
			db.Employees.Add(employee);
		}

		var taskMap = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
		var unassignedTasks = document.Tasks.Where(x => string.Equals(x.Action, "create", StringComparison.OrdinalIgnoreCase) && x.AssigneeId is null && string.IsNullOrWhiteSpace(x.AssigneeTempId)).ToList();
		if (unassignedTasks.Count > 0)
		{
			var placeholder = new Employee { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "Не назначен" };
			employeeMap["__unassigned"] = placeholder.Id;
			history.Add(operation, "employee", placeholder.Id, null, ChangeHistoryService.Snapshot(placeholder));
			db.Employees.Add(placeholder);
		}

		foreach (var item in document.Tasks.Where(x => string.Equals(x.Action, "create", StringComparison.OrdinalIgnoreCase)))
		{
			if (string.IsNullOrWhiteSpace(item.TempId) || string.IsNullOrWhiteSpace(item.Name))
				throw new InvalidOperationException("Every AI-created task must have tempId and name.");
			var taskStart = ParseDate(item.StartDate, $"task {item.TempId}.startDate");
			var taskEnd = ParseDate(item.EndDate, $"task {item.TempId}.endDate");
			if (taskStart > taskEnd) throw new InvalidOperationException($"Task '{item.Name}' has invalid dates.");
			if (taskStart < project.StartDate || taskEnd > project.EndDate) throw new InvalidOperationException($"Task '{item.Name}' is outside project boundaries.");
			var assigneeId = ResolveAssignee(item, employeeMap, allowExisting: false);
			var task = new ProjectTask
			{
				Id = Guid.NewGuid(), ProjectId = project.Id, Name = item.Name.Trim(), StartDate = taskStart, EndDate = taskEnd,
				AssigneeId = assigneeId, Status = ParseStatus(item.Status)
			};
			taskMap[item.TempId] = task.Id;
			history.Add(operation, "task", task.Id, null, ChangeHistoryService.Snapshot(task));
			db.Tasks.Add(task);
		}

		foreach (var item in document.Dependencies.Where(x => string.Equals(x.Action, "create", StringComparison.OrdinalIgnoreCase)))
		{
			var predecessorId = ResolveTask(item.PredecessorTaskId, item.PredecessorTempId, taskMap, project.Id);
			var successorId = ResolveTask(item.SuccessorTaskId, item.SuccessorTempId, taskMap, project.Id);
			if (predecessorId == successorId) throw new InvalidOperationException("AI plan contains a self dependency.");
			var dependency = new TaskDependency { PredecessorTaskId = predecessorId, SuccessorTaskId = successorId, CreatedAt = DateTimeOffset.UtcNow };
			history.Add(operation, "task_dependency", predecessorId, null, ChangeHistoryService.Snapshot(dependency));
			db.TaskDependencies.Add(dependency);
		}

		db.ChangeOperations.Add(operation);
		await db.SaveChangesAsync(ct);
		plan.ProjectId = project.Id;

		return await BuildPreviewChangesFromOperationAsync(operation, ct);
	}

	private async Task<List<AiPlanChangeDto>> ApplyUpdateAsync(AiPlan plan, AiPlanDocument document, CancellationToken ct)
	{
		var projectId = plan.ProjectId ?? throw new InvalidOperationException("Update AI plan has no project.");
		var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId, ct) ?? throw new KeyNotFoundException("Project not found.");
		var operation = history.Begin(projectId, "ai.project.update", string.IsNullOrWhiteSpace(document.Summary) ? "ИИ изменила проект" : $"ИИ: {document.Summary.Trim()}");

		if (document.Project is not null)
		{
			var before = ChangeHistoryService.Snapshot(project);
			if (document.Project.Name is not null) project.Name = document.Project.Name.Trim();
			if (document.Project.StartDate is not null) project.StartDate = ParseDate(document.Project.StartDate, "project.startDate");
			if (document.Project.EndDate is not null) project.EndDate = ParseDate(document.Project.EndDate, "project.endDate");
			if (project.StartDate > project.EndDate) throw new InvalidOperationException("AI plan would make project dates invalid.");
			history.Add(operation, "project", project.Id, before, ChangeHistoryService.Snapshot(project));
		}

		var employees = await db.Employees.Where(x => x.ProjectId == projectId).ToDictionaryAsync(x => x.Id, ct);
		var employeeMap = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in document.Employees.Where(x => string.Equals(x.Action, "create", StringComparison.OrdinalIgnoreCase)))
		{
			if (string.IsNullOrWhiteSpace(item.TempId) || string.IsNullOrWhiteSpace(item.Name)) throw new InvalidOperationException("AI-created employee requires tempId and name.");
			var employee = new Employee { Id = Guid.NewGuid(), ProjectId = projectId, Name = item.Name.Trim(), Phone = item.Phone, Email = item.Email };
			employeeMap[item.TempId] = employee.Id;
			employees[employee.Id] = employee;
			db.Employees.Add(employee);
			history.Add(operation, "employee", employee.Id, null, ChangeHistoryService.Snapshot(employee));
		}

		foreach (var item in document.Employees.Where(x => string.Equals(x.Action, "update", StringComparison.OrdinalIgnoreCase)))
		{
			if (!item.Id.HasValue || !employees.TryGetValue(item.Id.Value, out var employee)) throw new InvalidOperationException("AI plan references an unknown employee.");
			var before = ChangeHistoryService.Snapshot(employee);
			if (item.Name is not null) employee.Name = item.Name.Trim();
			if (item.Phone is not null) employee.Phone = item.Phone;
			if (item.Email is not null) employee.Email = item.Email;
			history.Add(operation, "employee", employee.Id, before, ChangeHistoryService.Snapshot(employee));
		}

		foreach (var item in document.Employees.Where(x => string.Equals(x.Action, "delete", StringComparison.OrdinalIgnoreCase)))
		{
			if (!item.Id.HasValue || !employees.TryGetValue(item.Id.Value, out var employee)) throw new InvalidOperationException("AI plan references an unknown employee.");
			if (await db.Tasks.AnyAsync(x => x.ProjectId == projectId && x.AssigneeId == employee.Id, ct)) throw new InvalidOperationException($"Employee '{employee.Name}' cannot be deleted while assigned to tasks.");
			history.Add(operation, "employee", employee.Id, ChangeHistoryService.Snapshot(employee), null);
			db.Employees.Remove(employee);
		}

		var tasks = await db.Tasks.Where(x => x.ProjectId == projectId).ToDictionaryAsync(x => x.Id, ct);
		var taskMap = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in document.Tasks.Where(x => string.Equals(x.Action, "create", StringComparison.OrdinalIgnoreCase)))
		{
			if (string.IsNullOrWhiteSpace(item.TempId) || string.IsNullOrWhiteSpace(item.Name)) throw new InvalidOperationException("AI-created task requires tempId and name.");
			var start = ParseDate(item.StartDate, $"task {item.TempId}.startDate");
			var end = ParseDate(item.EndDate, $"task {item.TempId}.endDate");
			if (start > end) throw new InvalidOperationException($"Task '{item.Name}' has invalid dates.");
			var assignee = ResolveAssignee(item, employeeMap, allowExisting: true);
			if (!employees.ContainsKey(assignee)) throw new InvalidOperationException($"Task '{item.Name}' references an unknown employee.");
			var task = new ProjectTask { Id = Guid.NewGuid(), ProjectId = projectId, Name = item.Name.Trim(), StartDate = start, EndDate = end, AssigneeId = assignee, Status = ParseStatus(item.Status) };
			taskMap[item.TempId] = task.Id;
			tasks[task.Id] = task;
			db.Tasks.Add(task);
			history.Add(operation, "task", task.Id, null, ChangeHistoryService.Snapshot(task));
		}

		foreach (var item in document.Tasks.Where(x => string.Equals(x.Action, "update", StringComparison.OrdinalIgnoreCase)))
		{
			if (!item.Id.HasValue || !tasks.TryGetValue(item.Id.Value, out var task)) throw new InvalidOperationException("AI plan references an unknown task.");
			var before = ChangeHistoryService.Snapshot(task);
			if (item.Name is not null) task.Name = item.Name.Trim();
			if (item.StartDate is not null) task.StartDate = ParseDate(item.StartDate, $"task {task.Id}.startDate");
			if (item.EndDate is not null) task.EndDate = ParseDate(item.EndDate, $"task {task.Id}.endDate");
			if (item.Status is not null) task.Status = ParseStatus(item.Status);
			if (item.AssigneeId.HasValue) task.AssigneeId = item.AssigneeId.Value;
			if (item.AssigneeTempId is not null)
				task.AssigneeId = employeeMap.TryGetValue(item.AssigneeTempId, out var mapped) ? mapped : throw new InvalidOperationException("AI plan references an unknown temporary employee.");
			if (task.StartDate > task.EndDate) throw new InvalidOperationException($"Task '{task.Name}' has invalid dates.");
			if (!employees.ContainsKey(task.AssigneeId)) throw new InvalidOperationException($"Task '{task.Name}' references an unknown employee.");
			history.Add(operation, "task", task.Id, before, ChangeHistoryService.Snapshot(task));
		}

		foreach (var item in document.Tasks.Where(x => string.Equals(x.Action, "delete", StringComparison.OrdinalIgnoreCase)))
		{
			if (!item.Id.HasValue || !tasks.TryGetValue(item.Id.Value, out var task)) throw new InvalidOperationException("AI plan references an unknown task.");
			var dependencies = await db.TaskDependencies.AsNoTracking().Where(x => x.PredecessorTaskId == task.Id || x.SuccessorTaskId == task.Id).ToListAsync(ct);
			foreach (var dependency in dependencies)
				history.Add(operation, "task_dependency", dependency.PredecessorTaskId, ChangeHistoryService.Snapshot(dependency), null);
			history.Add(operation, "task", task.Id, ChangeHistoryService.Snapshot(task), null);
			db.Tasks.Remove(task);
		}

		foreach (var item in document.Dependencies)
		{
			if (string.Equals(item.Action, "create", StringComparison.OrdinalIgnoreCase))
			{
				var predecessor = ResolveTask(item.PredecessorTaskId, item.PredecessorTempId, taskMap, projectId);
				var successor = ResolveTask(item.SuccessorTaskId, item.SuccessorTempId, taskMap, projectId);
				if (predecessor == successor) throw new InvalidOperationException("AI plan contains a self dependency.");
				if (await db.TaskDependencies.AnyAsync(x => x.PredecessorTaskId == predecessor && x.SuccessorTaskId == successor, ct)) continue;
				if (await graph.WouldCreateCycleAsync(predecessor, successor, ct)) throw new InvalidOperationException("AI plan would create a dependency cycle.");
				var dependency = new TaskDependency { PredecessorTaskId = predecessor, SuccessorTaskId = successor, CreatedAt = DateTimeOffset.UtcNow };
				history.Add(operation, "task_dependency", predecessor, null, ChangeHistoryService.Snapshot(dependency));
				db.TaskDependencies.Add(dependency);
			}
			else if (string.Equals(item.Action, "delete", StringComparison.OrdinalIgnoreCase))
			{
				var predecessor = ResolveTask(item.PredecessorTaskId, item.PredecessorTempId, taskMap, projectId);
				var successor = ResolveTask(item.SuccessorTaskId, item.SuccessorTempId, taskMap, projectId);
				var dependency = await db.TaskDependencies.SingleOrDefaultAsync(x => x.PredecessorTaskId == predecessor && x.SuccessorTaskId == successor, ct);
				if (dependency is not null)
				{
					history.Add(operation, "task_dependency", predecessor, ChangeHistoryService.Snapshot(dependency), null);
					db.TaskDependencies.Remove(dependency);
				}
			}
		}

		var allTasks = tasks.Values.Where(x => db.Entry(x).State != EntityState.Deleted).ToList();
		foreach (var task in allTasks)
		{
			if (task.StartDate < project.StartDate || task.EndDate > project.EndDate)
				throw new InvalidOperationException($"Task '{task.Name}' would be outside project boundaries.");
		}

		db.ChangeOperations.Add(operation);
		await db.SaveChangesAsync(ct);
		return await BuildPreviewChangesFromOperationAsync(operation, ct);
	}

	private async Task<AiPlanContext> BuildContextAsync(Guid projectId, CancellationToken ct)
	{
		var project = await db.Projects.AsNoTracking().Where(x => x.Id == projectId).Select(x => new { x.Id, x.Name, x.StartDate, x.EndDate }).SingleAsync(ct);
		var employeeRows = await db.Employees.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Id).Select(x => new { x.Id, x.Name, x.Phone, x.Email }).ToListAsync(ct);
		var taskRows = await db.Tasks.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Id).Select(x => new { x.Id, x.Name, x.StartDate, x.EndDate, x.Status, x.AssigneeId }).ToListAsync(ct);
		var dependencyRows = await db.TaskDependencies.AsNoTracking().Where(x => x.PredecessorTask.ProjectId == projectId).OrderBy(x => x.PredecessorTaskId).ThenBy(x => x.SuccessorTaskId).Select(x => new { x.PredecessorTaskId, x.SuccessorTaskId }).ToListAsync(ct);
		return new AiPlanContext(project, employeeRows.Cast<object>().ToList(), taskRows.Cast<object>().ToList(), dependencyRows.Cast<object>().ToList());
	}

	private static string ComputeContextHash(AiPlanContext context)
	{
		var json = JsonSerializer.Serialize(context, JsonOptions);
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
	}

	private async Task<List<AiPlanChangeDto>> BuildPreviewChangesAsync(AiPlanDocument document, Guid? projectId, CancellationToken ct)
	{
		var changes = new List<AiPlanChangeDto>();
		if (document.Project is not null)
		{
			if (projectId.HasValue)
			{
				var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == projectId, ct);
				if (document.Project.Name is not null && project.Name != document.Project.Name) changes.Add(new("update", "project", project.Id, "Название проекта", "name", project.Name, document.Project.Name));
				if (document.Project.StartDate is not null && project.StartDate != ParseDate(document.Project.StartDate, "project.startDate")) changes.Add(new("update", "project", project.Id, "Дата начала проекта", "startDate", project.StartDate.ToString("yyyy-MM-dd"), document.Project.StartDate));
				if (document.Project.EndDate is not null && project.EndDate != ParseDate(document.Project.EndDate, "project.endDate")) changes.Add(new("update", "project", project.Id, "Дата окончания проекта", "endDate", project.EndDate.ToString("yyyy-MM-dd"), document.Project.EndDate));
			}
			else
			{
				changes.Add(new("create", "project", null, document.Project.Name ?? "Новый проект", null, null, $"{document.Project.StartDate} — {document.Project.EndDate}"));
			}
		}

		var employees = projectId.HasValue ? await db.Employees.AsNoTracking().Where(x => x.ProjectId == projectId).ToDictionaryAsync(x => x.Id, ct) : new Dictionary<Guid, Employee>();
		foreach (var item in document.Employees)
		{
			if (item.Action == "create") changes.Add(new("create", "employee", item.Id, item.Name ?? "Сотрудник", null, null, item.Name));
			else if (item.Id.HasValue && employees.TryGetValue(item.Id.Value, out var employee))
			{
				if (item.Action == "delete") changes.Add(new("delete", "employee", employee.Id, employee.Name, null, employee.Name, null));
				else
				{
					if (item.Name is not null && item.Name != employee.Name) changes.Add(new("update", "employee", employee.Id, employee.Name, "name", employee.Name, item.Name));
					if (item.Phone is not null && item.Phone != employee.Phone) changes.Add(new("update", "employee", employee.Id, employee.Name, "phone", employee.Phone, item.Phone));
					if (item.Email is not null && item.Email != employee.Email) changes.Add(new("update", "employee", employee.Id, employee.Name, "email", employee.Email, item.Email));
				}
			}
		}

		var tasks = projectId.HasValue ? await db.Tasks.AsNoTracking().Where(x => x.ProjectId == projectId).ToDictionaryAsync(x => x.Id, ct) : new Dictionary<Guid, ProjectTask>();
		foreach (var item in document.Tasks)
		{
			if (item.Action == "create") changes.Add(new("create", "task", item.Id, item.Name ?? "Задача", null, null, $"{item.StartDate} — {item.EndDate}"));
			else if (item.Id.HasValue && tasks.TryGetValue(item.Id.Value, out var task))
			{
				if (item.Action == "delete") changes.Add(new("delete", "task", task.Id, task.Name, null, task.Name, null));
				else
				{
					if (item.Name is not null && item.Name != task.Name) changes.Add(new("update", "task", task.Id, task.Name, "name", task.Name, item.Name));
					if (item.StartDate is not null && item.StartDate != task.StartDate.ToString("yyyy-MM-dd")) changes.Add(new("update", "task", task.Id, task.Name, "startDate", task.StartDate.ToString("yyyy-MM-dd"), item.StartDate));
					if (item.EndDate is not null && item.EndDate != task.EndDate.ToString("yyyy-MM-dd")) changes.Add(new("update", "task", task.Id, task.Name, "endDate", task.EndDate.ToString("yyyy-MM-dd"), item.EndDate));
					if (item.Status is not null && item.Status != task.Status.ToString()) changes.Add(new("update", "task", task.Id, task.Name, "status", task.Status.ToString(), item.Status));
					if (item.AssigneeId.HasValue && item.AssigneeId != task.AssigneeId) changes.Add(new("update", "task", task.Id, task.Name, "assigneeId", task.AssigneeId.ToString(), item.AssigneeId.ToString()));
				}
			}
		}

		foreach (var item in document.Dependencies)
		{
			var pred = item.PredecessorTaskId?.ToString() ?? item.PredecessorTempId ?? "?";
			var succ = item.SuccessorTaskId?.ToString() ?? item.SuccessorTempId ?? "?";
			changes.Add(new(item.Action, "task_dependency", null, $"{pred} → {succ}", null, item.Action == "create" ? null : "существует", item.Action == "create" ? "создать" : null));
		}

		return changes;
	}

	private static AiPlanDto ToDto(AiPlan plan, IReadOnlyList<AiPlanChangeDto> changes) => new(
		plan.Id, plan.ProjectId, plan.OperationType, plan.Status.ToString(), plan.Summary, changes, plan.CreatedAt, plan.ConfirmedAt);

	private static AiPlanDocument Deserialize(string json) => JsonSerializer.Deserialize<AiPlanDocument>(json, JsonOptions) ?? throw new InvalidOperationException("Stored AI plan is invalid.");

	private async Task ValidateDocumentAsync(AiPlanDocument document, Guid? projectId, AiPlanContext context, CancellationToken ct)
	{
		if (document.Operation is not "create_project" and not "update_project")
			throw new InvalidOperationException("AI returned an unsupported operation.");
		if (projectId.HasValue && document.Operation != "update_project")
			throw new InvalidOperationException("AI plan operation does not match the endpoint.");
		if (!projectId.HasValue && document.Operation != "create_project")
			throw new InvalidOperationException("AI plan operation does not match the endpoint.");

		var project = projectId.HasValue
			? await db.Projects.AsNoTracking().SingleAsync(x => x.Id == projectId.Value, ct)
			: null;

		var employeeIds = projectId.HasValue
			? await db.Employees.AsNoTracking().Where(x => x.ProjectId == projectId.Value).Select(x => x.Id).ToHashSetAsync(ct)
			: [];
		var taskDates = projectId.HasValue
			? await db.Tasks.AsNoTracking().Where(x => x.ProjectId == projectId.Value).Select(x => new { x.Id, x.StartDate, x.EndDate }).ToDictionaryAsync(x => x.Id, x => (x.StartDate, x.EndDate), ct)
			: new Dictionary<Guid, (DateOnly StartDate, DateOnly EndDate)>();

		var tempIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		void AddTemp(string? tempId, string field)
		{
			if (string.IsNullOrWhiteSpace(tempId)) return;
			if (!tempIds.Add(tempId)) throw new InvalidOperationException($"AI plan contains duplicate tempId '{tempId}' ({field}).");
		}

		if (document.Operation == "create_project")
		{
			if (document.Project is null) throw new InvalidOperationException("AI create plan must contain project data.");
			if (document.Project.Id.HasValue) throw new InvalidOperationException("A newly created project must not contain an id.");
			AddTemp(document.Project.TempId, "project.tempId");
			if (string.IsNullOrWhiteSpace(document.Project.Name)) throw new InvalidOperationException("AI create plan must contain a project name.");
			var createProjectStart = ParseDate(document.Project.StartDate, "project.startDate");
			var createProjectEnd = ParseDate(document.Project.EndDate, "project.endDate");
			if (createProjectStart > createProjectEnd) throw new InvalidOperationException("AI plan has invalid project dates.");
		}
		else if (document.Project is not null && document.Project.Id.HasValue && projectId != document.Project.Id)
		{
			throw new InvalidOperationException("AI plan project.id does not match the requested project.");
		}

		foreach (var item in document.Employees)
		{
			var action = item.Action.ToLowerInvariant();
			if (action == "create")
			{
				if (item.Id.HasValue) throw new InvalidOperationException("AI-created employees must have id=null.");
				if (string.IsNullOrWhiteSpace(item.TempId) || string.IsNullOrWhiteSpace(item.Name)) throw new InvalidOperationException("Every AI-created employee must have a unique tempId and name.");
				AddTemp(item.TempId, "employee.tempId");
			}
			else if (action is "update" or "delete")
			{
				if (!projectId.HasValue || !item.Id.HasValue || !employeeIds.Contains(item.Id.Value)) throw new InvalidOperationException("AI plan references an unknown employee ID.");
			}
			else throw new InvalidOperationException($"Unsupported employee action: {item.Action}");
		}

		var createdTaskDates = new Dictionary<string, (DateOnly StartDate, DateOnly EndDate)>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in document.Tasks)
		{
			var action = item.Action.ToLowerInvariant();
			if (action == "create")
			{
				if (item.Id.HasValue) throw new InvalidOperationException("AI-created tasks must have id=null.");
				if (string.IsNullOrWhiteSpace(item.TempId) || string.IsNullOrWhiteSpace(item.Name)) throw new InvalidOperationException("Every AI-created task must have a unique tempId and name.");
				AddTemp(item.TempId, "task.tempId");
				var start = ParseDate(item.StartDate, $"task {item.TempId}.startDate");
				var end = ParseDate(item.EndDate, $"task {item.TempId}.endDate");
				if (start > end) throw new InvalidOperationException($"Task '{item.Name}' has invalid dates.");
				createdTaskDates[item.TempId!] = (start, end);
				if (item.AssigneeId.HasValue) throw new InvalidOperationException("New tasks must reference new employees with assigneeTempId, not assigneeId.");
				if (item.AssigneeTempId is not null && !document.Employees.Any(x => x.Action.Equals("create", StringComparison.OrdinalIgnoreCase) && string.Equals(x.TempId, item.AssigneeTempId, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException($"Task '{item.Name}' references an unknown employee tempId.");
				if (!string.IsNullOrWhiteSpace(item.Status)) _ = ParseStatus(item.Status);
			}
			else if (action is "update" or "delete")
			{
				if (!projectId.HasValue || !item.Id.HasValue || !taskDates.ContainsKey(item.Id.Value)) throw new InvalidOperationException("AI plan references an unknown task ID.");
				if (action == "update")
				{
					if (!string.IsNullOrWhiteSpace(item.Status)) _ = ParseStatus(item.Status);
					if (item.AssigneeId.HasValue && !employeeIds.Contains(item.AssigneeId.Value)) throw new InvalidOperationException("AI plan references an unknown assignee ID.");
					if (item.AssigneeTempId is not null && !document.Employees.Any(x => x.Action.Equals("create", StringComparison.OrdinalIgnoreCase) && string.Equals(x.TempId, item.AssigneeTempId, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("AI plan references an unknown temporary assignee.");
				}
			}
			else throw new InvalidOperationException($"Unsupported task action: {item.Action}");
		}

		if (project is not null)
		{
			var projectStart = project.StartDate;
			var projectEnd = project.EndDate;
			if (document.Project?.StartDate is not null) projectStart = ParseDate(document.Project.StartDate, "project.startDate");
			if (document.Project?.EndDate is not null) projectEnd = ParseDate(document.Project.EndDate, "project.endDate");
			if (projectStart > projectEnd) throw new InvalidOperationException("AI plan has invalid project dates.");
			foreach (var item in document.Tasks.Where(x => x.Action.Equals("create", StringComparison.OrdinalIgnoreCase)))
			{
				var dates = createdTaskDates[item.TempId!];
				if (dates.StartDate < projectStart || dates.EndDate > projectEnd) throw new InvalidOperationException($"Task '{item.Name}' is outside project boundaries.");
			}
		}

		var effectiveTaskDates = taskDates.ToDictionary(x => x.Key, x => x.Value);
		var taskTempMap = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in document.Tasks.Where(x => x.Action.Equals("create", StringComparison.OrdinalIgnoreCase)))
		{
			var id = Guid.NewGuid();
			taskTempMap[item.TempId!] = id;
			effectiveTaskDates[id] = createdTaskDates[item.TempId!];
		}
		foreach (var item in document.Tasks.Where(x => x.Action.Equals("delete", StringComparison.OrdinalIgnoreCase)))
			if (item.Id.HasValue) effectiveTaskDates.Remove(item.Id.Value);
		foreach (var item in document.Tasks.Where(x => x.Action.Equals("update", StringComparison.OrdinalIgnoreCase)))
		{
			if (!item.Id.HasValue || !effectiveTaskDates.TryGetValue(item.Id.Value, out var current)) continue;
			var start = item.StartDate is null ? current.StartDate : ParseDate(item.StartDate, $"task {item.Id}.startDate");
			var end = item.EndDate is null ? current.EndDate : ParseDate(item.EndDate, $"task {item.Id}.endDate");
			if (start > end) throw new InvalidOperationException($"Task '{item.Id}' has invalid dates.");
			effectiveTaskDates[item.Id.Value] = (start, end);
		}

		if (project is not null)
		{
			var projectStart = document.Project?.StartDate is null ? project.StartDate : ParseDate(document.Project.StartDate, "project.startDate");
			var projectEnd = document.Project?.EndDate is null ? project.EndDate : ParseDate(document.Project.EndDate, "project.endDate");
			foreach (var task in effectiveTaskDates.Values)
			{
				if (task.StartDate < projectStart || task.EndDate > projectEnd) throw new InvalidOperationException("AI plan contains a task outside project boundaries.");
			}
		}

		Guid? ResolveValidationTask(Guid? id, string? tempId)
		{
			if (id.HasValue) return id.Value;
			if (!string.IsNullOrWhiteSpace(tempId) && taskTempMap.TryGetValue(tempId, out var mapped)) return mapped;
			return null;
		}

		var resultingDependencies = new HashSet<(Guid PredecessorTaskId, Guid SuccessorTaskId)>();
		if (projectId.HasValue)
		{
			var existingDependencies = await db.TaskDependencies.AsNoTracking()
				.Where(x => x.PredecessorTask.ProjectId == projectId.Value)
				.Select(x => new { x.PredecessorTaskId, x.SuccessorTaskId })
				.ToListAsync(ct);
			foreach (var dependency in existingDependencies)
				resultingDependencies.Add((dependency.PredecessorTaskId, dependency.SuccessorTaskId));
		}

		foreach (var item in document.Dependencies)
		{
			var action = item.Action.ToLowerInvariant();
			if (action is not ("create" or "delete")) throw new InvalidOperationException($"Unsupported dependency action: {item.Action}");
			var predecessor = ResolveValidationTask(item.PredecessorTaskId, item.PredecessorTempId);
			var successor = ResolveValidationTask(item.SuccessorTaskId, item.SuccessorTempId);
			if (!predecessor.HasValue || !successor.HasValue) throw new InvalidOperationException("AI dependency must reference both predecessor and successor.");
			if (!effectiveTaskDates.ContainsKey(predecessor.Value)) throw new InvalidOperationException("AI dependency references an unknown predecessor task.");
			if (!effectiveTaskDates.ContainsKey(successor.Value)) throw new InvalidOperationException("AI dependency references an unknown successor task.");
			if (predecessor == successor) throw new InvalidOperationException("AI plan contains a self dependency.");
			if (action == "create") resultingDependencies.Add((predecessor.Value, successor.Value));
			else resultingDependencies.Remove((predecessor.Value, successor.Value));
		}

		// Normalize the resulting schedule before accepting the AI plan.
		// The model can understand the dependency rule but may still return overlapping
		// dates. The backend is authoritative: move a conflicting successor to the
		// first valid day and preserve its duration. The same logic is then propagated
		// through the dependency graph, so chains are repaired as a whole.
		var outgoing = effectiveTaskDates.Keys.ToDictionary(x => x, _ => new List<Guid>());
		var indegree = effectiveTaskDates.Keys.ToDictionary(x => x, _ => 0);

		foreach (var dependency in resultingDependencies)
		{
			if (!effectiveTaskDates.ContainsKey(dependency.PredecessorTaskId) || !effectiveTaskDates.ContainsKey(dependency.SuccessorTaskId))
				continue;

			if (!outgoing[dependency.PredecessorTaskId].Contains(dependency.SuccessorTaskId))
			{
				outgoing[dependency.PredecessorTaskId].Add(dependency.SuccessorTaskId);
				indegree[dependency.SuccessorTaskId]++;
			}
		}

		var queue = new Queue<Guid>(indegree.Where(x => x.Value == 0).Select(x => x.Key));
		var topologicalOrder = new List<Guid>(effectiveTaskDates.Count);
		while (queue.Count > 0)
		{
			var taskId = queue.Dequeue();
			topologicalOrder.Add(taskId);
			foreach (var successorId in outgoing[taskId])
			{
				if (--indegree[successorId] == 0)
					queue.Enqueue(successorId);
			}
		}

		if (topologicalOrder.Count != effectiveTaskDates.Count)
			throw new InvalidOperationException("AI plan contains a dependency cycle; the schedule cannot be normalized.");

		var taskChangesById = document.Tasks
			.Where(x => string.Equals(x.Action, "update", StringComparison.OrdinalIgnoreCase) && x.Id.HasValue)
			.ToDictionary(x => x.Id!.Value);

		foreach (var taskId in topologicalOrder)
		{
			foreach (var successorId in outgoing[taskId])
			{
				var predecessorDates = effectiveTaskDates[taskId];
				var successorDates = effectiveTaskDates[successorId];
				var requiredStart = DependencyScheduleRules.RequiredSuccessorStart(predecessorDates.EndDate);

				if (successorDates.StartDate >= requiredStart)
					continue;

				var durationDays = successorDates.EndDate.DayNumber - successorDates.StartDate.DayNumber;
				var newStart = requiredStart;
				var newEnd = newStart.AddDays(durationDays);
				effectiveTaskDates[successorId] = (newStart, newEnd);

				// For an existing task in an update plan, make the backend-generated
				// schedule repair part of the plan so ApplyUpdateAsync persists it.
				if (taskDates.ContainsKey(successorId))
				{
					if (!taskChangesById.TryGetValue(successorId, out var change))
					{
						change = new AiTaskChange
						{ Action = "update", Id = successorId };
						document.Tasks.Add(change);
						taskChangesById[successorId] = change;
					}

					change.StartDate = newStart.ToString("yyyy-MM-dd");
					change.EndDate = newEnd.ToString("yyyy-MM-dd");
				}
				else
				{
					var created = document.Tasks.FirstOrDefault(x =>
						string.Equals(x.Action, "create", StringComparison.OrdinalIgnoreCase) &&
						!string.IsNullOrWhiteSpace(x.TempId) &&
						taskTempMap.TryGetValue(x.TempId!, out var mappedId) &&
						mappedId == successorId);
					if (created is not null)
					{
						created.StartDate = newStart.ToString("yyyy-MM-dd");
						created.EndDate = newEnd.ToString("yyyy-MM-dd");
					}
				}
			}
		}

		if (project is not null)
		{
			var projectStart = document.Project?.StartDate is null ? project.StartDate : ParseDate(document.Project.StartDate, "project.startDate");
			var projectEnd = document.Project?.EndDate is null ? project.EndDate : ParseDate(document.Project.EndDate, "project.endDate");
			foreach (var task in effectiveTaskDates.Values)
			{
				if (task.StartDate < projectStart || task.EndDate > projectEnd)
					throw new InvalidOperationException("AI plan cannot satisfy dependency dates without exceeding project boundaries.");
			}
		}

	}

	private async Task EnsureProjectOwnerAsync(Guid projectId, Guid userId, CancellationToken ct)
	{
		if (!await db.Projects.AnyAsync(x => x.Id == projectId && x.CreatorId == userId, ct)) throw new KeyNotFoundException("Project not found.");
	}

	private static DateOnly ParseDate(string? value, string field)
	{
		if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", out var date)) throw new InvalidOperationException($"AI returned invalid date for {field}: {value}");
		return date;
	}

	private static ProjectTaskStatus ParseStatus(string? value)
	{
		if (Enum.TryParse<ProjectTaskStatus>(value, true, out var status)) return status;
		throw new InvalidOperationException($"AI returned invalid task status: {value}");
	}

	private static Guid ResolveAssignee(AiTaskChange item, IReadOnlyDictionary<string, Guid> employeeMap, bool allowExisting)
	{
		if (item.AssigneeTempId is not null && employeeMap.TryGetValue(item.AssigneeTempId, out var tempId)) return tempId;
		if (item.AssigneeId.HasValue && allowExisting) return item.AssigneeId.Value;
		if (employeeMap.TryGetValue("__unassigned", out var placeholder)) return placeholder;
		throw new InvalidOperationException($"Task '{item.Name}' has no valid assignee.");
	}

	private static Guid ResolveTask(Guid? id, string? tempId, IReadOnlyDictionary<string, Guid> taskMap, Guid projectId)
	{
		if (id.HasValue) return id.Value;
		if (!string.IsNullOrWhiteSpace(tempId) && taskMap.TryGetValue(tempId, out var mapped)) return mapped;
		throw new InvalidOperationException($"AI plan contains an unresolved task reference for project {projectId}.");
	}

	private async Task<List<AiPlanChangeDto>> BuildPreviewChangesFromOperationAsync(ChangeOperation operation, CancellationToken ct)
	{
		return operation.Items.Select(item => new AiPlanChangeDto(
			item.AfterJson is null ? "delete" : item.BeforeJson is null ? "create" : "update",
			item.EntityType,
			item.EntityId,
			$"{item.EntityType} {item.EntityId}",
			null,
			item.BeforeJson,
			item.AfterJson)).ToList();
	}
}
