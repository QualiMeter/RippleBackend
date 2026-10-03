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
		ValidateDocument(document, projectId);
		await NormalizeAiScheduleAsync(document, projectId, context, ct);
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
		ValidateDocument(document, plan.ProjectId);
		if (plan.ProjectId.HasValue)
		{
			var currentContext = await BuildContextAsync(plan.ProjectId.Value, ct);
			var currentHash = ComputeContextHash(currentContext);
			if (!string.Equals(plan.ContextHash, currentHash, StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("Project changed after the AI preview was created. Generate a new AI plan before confirming.");
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

	private async Task NormalizeAiScheduleAsync(AiPlanDocument document, Guid? projectId, AiPlanContext context, CancellationToken ct)
	{
		var tasks = new Dictionary<string, ScheduleTask>(StringComparer.OrdinalIgnoreCase);
		var dependencies = new List<(string Predecessor, string Successor)>();

		if (projectId.HasValue)
		{
			foreach (var raw in context.Tasks)
			{
				var json = JsonSerializer.Serialize(raw, JsonOptions);
				using var node = JsonDocument.Parse(json);
				var root = node.RootElement;
				var id = root.GetProperty("id").GetGuid();
				var name = root.GetProperty("name").GetString() ?? id.ToString();
				var start = root.GetProperty("startDate").GetDateTime();
				var end = root.GetProperty("endDate").GetDateTime();
				tasks[id.ToString()] = new ScheduleTask(id.ToString(), name, DateOnly.FromDateTime(start), DateOnly.FromDateTime(end), false, id.ToString());
			}

			foreach (var raw in context.Dependencies)
			{
				var json = JsonSerializer.Serialize(raw, JsonOptions);
				using var node = JsonDocument.Parse(json);
				var root = node.RootElement;
				dependencies.Add((root.GetProperty("predecessorTaskId").GetGuid().ToString(), root.GetProperty("successorTaskId").GetGuid().ToString()));
			}
		}

		foreach (var item in document.Tasks)
		{
			if (!string.Equals(item.Action, "create", StringComparison.OrdinalIgnoreCase) &&
				!string.Equals(item.Action, "update", StringComparison.OrdinalIgnoreCase))
				continue;

			var key = item.Id?.ToString() ?? item.TempId;
			if (string.IsNullOrWhiteSpace(key))
				continue;

			if (!tasks.TryGetValue(key, out var task))
			{
				if (string.Equals(item.Action, "create", StringComparison.OrdinalIgnoreCase))
				{
					if (string.IsNullOrWhiteSpace(item.StartDate) || string.IsNullOrWhiteSpace(item.EndDate))
						continue;
					task = new ScheduleTask(key, item.Name ?? key, ParseDate(item.StartDate, $"task {key}.startDate"), ParseDate(item.EndDate, $"task {key}.endDate"), true, key);
					tasks[key] = task;
				}
				continue;
			}

			if (item.StartDate is not null)
				task.Start = ParseDate(item.StartDate, $"task {key}.startDate");
			if (item.EndDate is not null)
				task.End = ParseDate(item.EndDate, $"task {key}.endDate");
			if (item.Name is not null)
				task.Name = item.Name;
		}

		var deletedIds = document.Tasks
			.Where(x => string.Equals(x.Action, "delete", StringComparison.OrdinalIgnoreCase) && x.Id.HasValue)
			.Select(x => x.Id!.Value.ToString())
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var id in deletedIds)
			tasks.Remove(id);

		if (projectId.HasValue)
		{
			foreach (var raw in context.Dependencies)
			{
				var json = JsonSerializer.Serialize(raw, JsonOptions);
				using var node = JsonDocument.Parse(json);
				var root = node.RootElement;
				var pair = (root.GetProperty("predecessorTaskId").GetGuid().ToString(), root.GetProperty("successorTaskId").GetGuid().ToString());
				dependencies.Add(pair);
			}

			foreach (var item in document.Dependencies.Where(x => string.Equals(x.Action, "delete", StringComparison.OrdinalIgnoreCase)))
			{
				var pred = item.PredecessorTaskId?.ToString() ?? item.PredecessorTempId;
				var succ = item.SuccessorTaskId?.ToString() ?? item.SuccessorTempId;
				if (pred is not null && succ is not null)
					dependencies.RemoveAll(x => string.Equals(x.Predecessor, pred, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Successor, succ, StringComparison.OrdinalIgnoreCase));
			}
		}

		foreach (var item in document.Dependencies.Where(x => string.Equals(x.Action, "create", StringComparison.OrdinalIgnoreCase)))
		{
			var pred = item.PredecessorTaskId?.ToString() ?? item.PredecessorTempId;
			var succ = item.SuccessorTaskId?.ToString() ?? item.SuccessorTempId;
			if (!string.IsNullOrWhiteSpace(pred) && !string.IsNullOrWhiteSpace(succ) && !dependencies.Any(x => x.Predecessor.Equals(pred, StringComparison.OrdinalIgnoreCase) && x.Successor.Equals(succ, StringComparison.OrdinalIgnoreCase)))
				dependencies.Add((pred!, succ!));
		}

		if (tasks.Count == 0)
		{
			if (document.Project is null)
				document.Project = new AiProjectChange();
			if (projectId is null)
			{
				var today = DateOnly.FromDateTime(DateTime.UtcNow);
				if (document.Project.StartDate is null) document.Project.StartDate = today.ToString("yyyy-MM-dd");
				if (document.Project.EndDate is null) document.Project.EndDate = document.Project.StartDate;
			}
			return;
		}

		var adjacency = tasks.Keys.ToDictionary(x => x, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
		var indegree = tasks.Keys.ToDictionary(x => x, _ => 0, StringComparer.OrdinalIgnoreCase);
		foreach (var (pred, succ) in dependencies.Distinct())
		{
			if (!tasks.ContainsKey(pred) || !tasks.ContainsKey(succ) || pred.Equals(succ, StringComparison.OrdinalIgnoreCase))
				continue;
			adjacency[pred].Add(succ);
			indegree[succ]++;
		}

		var queue = new Queue<string>(indegree.Where(x => x.Value == 0).Select(x => x.Key));
		var ordered = new List<string>(tasks.Count);
		while (queue.Count > 0)
		{
			var current = queue.Dequeue();
			ordered.Add(current);
			foreach (var next in adjacency[current])
				if (--indegree[next] == 0)
					queue.Enqueue(next);
		}

		if (ordered.Count != tasks.Count)
			throw new InvalidOperationException("AI plan contains a dependency cycle.");

		DateOnly projectStart;
		DateOnly projectEnd;
		if (projectId.HasValue)
		{
			var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == projectId.Value, ct);
			projectStart = project.StartDate;
			projectEnd = project.EndDate;
			if (document.Project?.StartDate is not null) projectStart = ParseDate(document.Project.StartDate, "project.startDate");
			if (document.Project?.EndDate is not null) projectEnd = ParseDate(document.Project.EndDate, "project.endDate");
		}
		else
		{
			var dates = tasks.Values.ToList();
			projectStart = dates.Min(x => x.Start);
			projectEnd = dates.Max(x => x.End);
			if (document.Project?.StartDate is not null) projectStart = ParseDate(document.Project.StartDate, "project.startDate");
			if (document.Project?.EndDate is not null) projectEnd = ParseDate(document.Project.EndDate, "project.endDate");
		}

		if (projectStart > projectEnd)
			throw new InvalidOperationException("AI plan has invalid project dates.");

		foreach (var key in ordered)
		{
			var task = tasks[key];
			if (task.Start < projectStart)
			{
				var delta = projectStart.DayNumber - task.Start.DayNumber;
				task.Start = projectStart;
				task.End = task.End.AddDays(delta);
			}

			foreach (var predecessor in dependencies.Where(x => x.Successor.Equals(key, StringComparison.OrdinalIgnoreCase)))
			{
				if (!tasks.TryGetValue(predecessor.Predecessor, out var parent))
					continue;
				var required = DependencyScheduleRules.RequiredSuccessorStart(parent.End);
				if (task.Start < required)
				{
					var delta = required.DayNumber - task.Start.DayNumber;
					task.Start = required;
					task.End = task.End.AddDays(delta);
				}
			}

			if (task.End > projectEnd)
				projectEnd = task.End;
		}

		if (document.Project is null)
			document.Project = new AiProjectChange();
		document.Project.StartDate = projectStart.ToString("yyyy-MM-dd");
		document.Project.EndDate = projectEnd.ToString("yyyy-MM-dd");

		foreach (var item in document.Tasks)
		{
			var key = item.Id?.ToString() ?? item.TempId;
			if (key is null || !tasks.TryGetValue(key, out var task))
				continue;
			if (!string.Equals(item.Action, "create", StringComparison.OrdinalIgnoreCase) && !string.Equals(item.Action, "update", StringComparison.OrdinalIgnoreCase))
				continue;
			item.StartDate = task.Start.ToString("yyyy-MM-dd");
			item.EndDate = task.End.ToString("yyyy-MM-dd");
		}
	}

	private sealed class ScheduleTask(string key, string name, DateOnly start, DateOnly end, bool isNew, string reference)
	{
		public string Key { get; } = key;
		public string Name { get; set; } = name;
		public DateOnly Start { get; set; } = start;
		public DateOnly End { get; set; } = end;
		public bool IsNew { get; } = isNew;
		public string Reference { get; } = reference;
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

	private static void ValidateDocument(AiPlanDocument document, Guid? projectId)
	{
		if (document.Operation is not "create_project" and not "update_project") throw new InvalidOperationException("AI returned an unsupported operation.");
		if (projectId.HasValue && document.Operation != "update_project") throw new InvalidOperationException("AI plan operation does not match the endpoint.");
		if (!projectId.HasValue && document.Operation != "create_project") throw new InvalidOperationException("AI plan operation does not match the endpoint.");
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
