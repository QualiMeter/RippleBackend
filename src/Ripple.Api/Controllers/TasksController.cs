using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ripple.Api.Contracts;
using Ripple.Api.Data;
using Ripple.Api.Domain;
using Ripple.Api.Services;

namespace Ripple.Api.Controllers;

[ApiController]
[Route("api/v1/projects/{projectId:guid}/tasks")]
public sealed class TasksController(AppDbContext db, ICurrentUserAccessor currentUser, AnalysisService analysis, ShiftService shift, ChangeHistoryService history, IRealtimeNotifier realtime) : ControllerBase
{
	[HttpGet]
	public async Task<ActionResult<IReadOnlyList<TaskListItemDto>>> GetAll(Guid projectId, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		var tasks = await db.Tasks.AsNoTracking()
			.Where(x => x.ProjectId == projectId)
			.OrderBy(x => x.StartDate)
			.Select(x => new TaskListItemDto(
				x.Id,
				x.ProjectId,
				x.Name,
				x.StartDate,
				x.EndDate,
				x.EndDate.DayNumber - x.StartDate.DayNumber,
				x.AssigneeId,
				x.Assignee.Name,
				x.Status.ToString()))
			.ToListAsync(ct);
		return Ok(tasks);
	}

	[HttpPost]
	public async Task<ActionResult<TaskMutationResponse>> Create(Guid projectId, CreateTaskRequest request, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		if (request.StartDate > request.EndDate) throw new ArgumentException("Task start date cannot be after task end date.");
		var status = ParseStatus(request.Status);
		var employee = await db.Employees.SingleOrDefaultAsync(x => x.Id == request.AssigneeId && x.ProjectId == projectId, ct);
		if (employee is null) throw new KeyNotFoundException("Assignee does not belong to project.");
		if (string.IsNullOrWhiteSpace(request.Name)) return ValidationProblem("Task name is required.");

		var task = new ProjectTask
		{
			Id = Guid.NewGuid(),
			ProjectId = projectId,
			Name = request.Name.Trim(),
			StartDate = request.StartDate,
			EndDate = request.EndDate,
			AssigneeId = request.AssigneeId,
			Status = status
		};
		var operation = history.Begin(projectId, "task.create", $"Добавлена задача: {task.Name}");
		history.Add(operation, "task", task.Id, null, ChangeHistoryService.Snapshot(task));
		db.Tasks.Add(task);
		db.ChangeOperations.Add(operation);
		await db.SaveChangesAsync(ct);
		var full = await LoadTaskAsync(projectId, task.Id, ct);
		var messages = await analysis.AnalyzeTaskChangeAsync(task, full, ct);
		await realtime.PublishAsync(projectId, "task", "created", task.Id, ProjectMapper.ToDetailsDto(full), ct);
		return CreatedAtAction(nameof(Get), new { projectId, taskId = task.Id }, new TaskMutationResponse(ProjectMapper.ToDetailsDto(full), messages));
	}

	[HttpGet("{taskId:guid}")]
	public async Task<ActionResult<TaskDetailsDto>> Get(Guid projectId, Guid taskId, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		var task = await LoadTaskAsync(projectId, taskId, ct);
		return Ok(ProjectMapper.ToDetailsDto(task));
	}

	[HttpPut("{taskId:guid}")]
	public async Task<ActionResult<TaskMutationResponse>> Update(Guid projectId, Guid taskId, UpdateTaskRequest request, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		if (request.StartDate > request.EndDate) throw new ArgumentException("Task start date cannot be after task end date.");
		if (string.IsNullOrWhiteSpace(request.Name)) return ValidationProblem("Task name is required.");
		var status = ParseStatus(request.Status);
		var task = await db.Tasks.SingleOrDefaultAsync(x => x.Id == taskId && x.ProjectId == projectId, ct);
		if (task is null) return NotFound();
		var before = new ProjectTask
		{
			Id = task.Id, ProjectId = task.ProjectId, AssigneeId = task.AssigneeId, Name = task.Name,
			StartDate = task.StartDate, EndDate = task.EndDate, Status = task.Status
		};
		if (!await db.Employees.AnyAsync(x => x.Id == request.AssigneeId && x.ProjectId == projectId, ct)) throw new KeyNotFoundException("Assignee does not belong to project.");

		task.Name = request.Name.Trim();
		task.StartDate = request.StartDate;
		task.EndDate = request.EndDate;
		task.AssigneeId = request.AssigneeId;
		task.Status = status;
		var operation = history.Begin(projectId, "task.update", $"Изменена задача: {task.Name}");
		history.Add(operation, "task", task.Id, ChangeHistoryService.Snapshot(before), ChangeHistoryService.Snapshot(task));
		db.ChangeOperations.Add(operation);
		await db.SaveChangesAsync(ct);
		var full = await LoadTaskAsync(projectId, taskId, ct);
		var messages = await analysis.AnalyzeTaskChangeAsync(before, full, ct);
		await realtime.PublishAsync(projectId, "task", "updated", task.Id, ProjectMapper.ToDetailsDto(full), ct);
		return Ok(new TaskMutationResponse(ProjectMapper.ToDetailsDto(full), messages));
	}

	[HttpDelete("{taskId:guid}")]
	public async Task<IActionResult> Delete(Guid projectId, Guid taskId, [FromQuery] bool confirm, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		var task = await db.Tasks.SingleOrDefaultAsync(x => x.Id == taskId && x.ProjectId == projectId, ct);
		if (task is null) return NotFound();

		var successors = await db.Tasks
			.Where(x => x.ProjectId == projectId && db.TaskDependencies.Any(d => d.PredecessorTaskId == taskId && d.SuccessorTaskId == x.Id))
			.AsNoTracking()
			.ToListAsync(ct);

		if (successors.Count > 0 && !confirm)
		{
			var messages = successors.Select(x => new AnalysisMessageDto(
				AnalysisSeverity.Warning,
				task.Id,
				task.Name,
				[x.Id],
				[x.Name],
				$"Удаление задачи {task.Name} изменит список предшественников этой задачи.",
				[new AnalysisActionDto("open-task", "Открыть задачу", x.Id)])).ToList();

			return Conflict(new { code = "confirmation_required", message = "Task has successor tasks. Repeat DELETE with confirm=true to delete.", analysis = messages });
		}

		var dependencies = await db.TaskDependencies
			.Where(x => x.PredecessorTaskId == taskId || x.SuccessorTaskId == taskId)
			.AsNoTracking()
			.ToListAsync(ct);
		var operation = history.Begin(projectId, "task.delete", $"Удалена задача: {task.Name}");
		history.Add(operation, "task", task.Id, ChangeHistoryService.Snapshot(task), null);
		foreach (var dependency in dependencies)
		{
			history.Add(operation, "task_dependency", dependency.PredecessorTaskId, ChangeHistoryService.Snapshot(dependency), null);
		}
		db.Tasks.Remove(task);
		db.ChangeOperations.Add(operation);
		await db.SaveChangesAsync(ct);
		await realtime.PublishAsync(projectId, "task", "deleted", taskId, null, ct);
		foreach (var dependency in dependencies)
			await realtime.PublishAsync(projectId, "task_dependency", "deleted", dependency.SuccessorTaskId, new { dependency.PredecessorTaskId, dependency.SuccessorTaskId }, ct);
		return NoContent();
	}

	[HttpGet("{taskId:guid}/analysis")]
	public async Task<ActionResult<IReadOnlyList<AnalysisMessageDto>>> Analyze(Guid projectId, Guid taskId, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		return Ok(await analysis.AnalyzeCurrentTaskAsync(projectId, taskId, ct));
	}

	[HttpPost("{taskId:guid}/shift-preview")]
	public async Task<ActionResult<ShiftPreviewDto>> ShiftPreview(Guid projectId, Guid taskId, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		return Ok(await shift.BuildPreviewAsync(projectId, taskId, ct));
	}

	[HttpPost("{taskId:guid}/shift-confirm")]
	public async Task<ActionResult<ShiftConfirmationResponse>> ShiftConfirm(Guid projectId, Guid taskId, ConfirmShiftRequest request, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		return Ok(await shift.ConfirmAsync(projectId, taskId, request.ConfirmProjectEndDate, ct));
	}

	private async Task<ProjectTask> LoadTaskAsync(Guid projectId, Guid taskId, CancellationToken ct)
	{
		var task = await db.Tasks
			.Include(x => x.Assignee)
			.Include(x => x.PredecessorLinks)
			.Include(x => x.SuccessorLinks)
			.SingleOrDefaultAsync(x => x.Id == taskId && x.ProjectId == projectId, ct);
		if (task is null) throw new KeyNotFoundException("Task not found.");
		return task;
	}

	private async Task EnsureProjectAsync(Guid projectId, CancellationToken ct)
	{
		var userId = await currentUser.GetUserIdAsync(ct);
		if (!await db.Projects.AnyAsync(x => x.Id == projectId && x.CreatorId == userId, ct)) throw new KeyNotFoundException("Project not found.");
	}

	private static ProjectTaskStatus ParseStatus(string status) => Enum.TryParse<ProjectTaskStatus>(status, true, out var value) ? value : throw new ArgumentException("Invalid task status.");
}
