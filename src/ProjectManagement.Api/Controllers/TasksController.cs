using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProjectManagement.Api.Contracts;
using ProjectManagement.Api.Data;
using ProjectManagement.Api.Domain;
using ProjectManagement.Api.Services;

using TaskStatus = ProjectManagement.Api.Domain.TaskStatus;

namespace ProjectManagement.Api.Controllers;

[ApiController]
[Route("api/v1/projects/{projectId:guid}/tasks")]
public sealed class TasksController(AppDbContext db, ICurrentUserAccessor currentUser, AnalysisService analysis, ShiftService shift) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TaskListItemDto>>> GetAll(Guid projectId, CancellationToken ct)
    {
        await EnsureProjectAsync(projectId, ct);
        var tasks = await db.Tasks.AsNoTracking().Include(x => x.Assignee).Where(x => x.ProjectId == projectId).OrderBy(x => x.StartDate).ToListAsync(ct);
        return Ok(tasks.Select(ProjectMapper.ToListDto).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<TaskMutationResponse>> Create(Guid projectId, CreateTaskRequest request, CancellationToken ct)
    {
        await EnsureProjectAsync(projectId, ct);
        if (request.StartDate > request.EndDate) throw new ArgumentException("Task start date cannot be after task end date.");
        var status = ParseStatus(request.Status);
		var employee = await db.Employees.SingleOrDefaultAsync(x => x.Id == request.AssigneeId && x.ProjectId == projectId, ct) ?? throw new KeyNotFoundException("Assignee does not belong to project.");
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
        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct);
        var full = await LoadTaskAsync(projectId, task.Id, ct);
        var messages = await analysis.AnalyzeTaskChangeAsync(task, full, ct);
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
        await db.SaveChangesAsync(ct);
        var full = await LoadTaskAsync(projectId, taskId, ct);
        var messages = await analysis.AnalyzeTaskChangeAsync(before, full, ct);
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

        db.Tasks.Remove(task);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{taskId:guid}/analysis")]
    public async Task<ActionResult<IReadOnlyList<AnalysisMessageDto>>> Analyze(Guid projectId, Guid taskId, CancellationToken ct)
    {
        await EnsureProjectAsync(projectId, ct);
        var task = await LoadTaskAsync(projectId, taskId, ct);
        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == projectId, ct);
        var messages = new List<AnalysisMessageDto>();

        if (task.StartDate < project.StartDate || task.EndDate > project.EndDate)
        {
            messages.Add(new AnalysisMessageDto(AnalysisSeverity.Warning, task.Id, task.Name, [task.Id], [task.Name], 
                "Задача выходит за границы проекта.", [new AnalysisActionDto("open-project", "Открыть проект")]));
        }

        var successors = await db.TaskDependencies
            .Where(x => x.PredecessorTaskId == task.Id)
            .Join(db.Tasks, x => x.SuccessorTaskId, x => x.Id, (_, t) => t)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var successor in successors)
        {
            if (successor.StartDate < task.EndDate)
            {
                messages.Add(new AnalysisMessageDto(AnalysisSeverity.Warning, task.Id, task.Name, [successor.Id], [successor.Name],
                    $"Последующая задача начинается {successor.StartDate:yyyy-MM-dd}, раньше окончания предшественника {task.EndDate:yyyy-MM-dd}.",
                    [new AnalysisActionDto("shift-preview", "Рассчитать сдвиг", successor.Id)]));
            }
        }

        if (task.Status == TaskStatus.InProgress)
        {
            var predecessors = await db.TaskDependencies
                .Where(x => x.SuccessorTaskId == task.Id)
                .Join(db.Tasks, x => x.PredecessorTaskId, x => x.Id, (_, t) => t)
                .AsNoTracking()
                .ToListAsync(ct);
            var unfinished = predecessors.Where(x => x.Status != TaskStatus.Completed).ToList();
            if (unfinished.Count > 0)
            {
                messages.Add(new AnalysisMessageDto(AnalysisSeverity.Warning, task.Id, task.Name, [.. unfinished.Select(x => x.Id)], [.. unfinished.Select(x => x.Name)],
                    "У задачи есть незавершённые предшественники.", [.. unfinished.Select(x => new AnalysisActionDto("open-task", "Открыть задачу", x.Id))]));
            }
        }

        return Ok(messages);
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
		return task is null ? throw new KeyNotFoundException("Task not found.") : task;
	}

	private async Task EnsureProjectAsync(Guid projectId, CancellationToken ct)
    {
        var userId = await currentUser.GetUserIdAsync(ct);
        if (!await db.Projects.AnyAsync(x => x.Id == projectId && x.CreatorId == userId, ct)) throw new KeyNotFoundException("Project not found.");
    }

    private static TaskStatus ParseStatus(string status) => Enum.TryParse<TaskStatus>(status, true, out var value) ? value : throw new ArgumentException("Invalid task status.");
}
