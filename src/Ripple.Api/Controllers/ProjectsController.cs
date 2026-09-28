using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ripple.Api.Contracts;
using Ripple.Api.Data;
using Ripple.Api.Domain;
using Ripple.Api.Services;

namespace Ripple.Api.Controllers;

[ApiController]
[Route("api/v1/projects")]
public sealed class ProjectsController(AppDbContext db, ICurrentUserAccessor currentUser, ChangeHistoryService history, IRealtimeNotifier realtime) : ControllerBase
{
	[HttpGet]
	public async Task<ActionResult<IReadOnlyList<ProjectListItemDto>>> GetAll(CancellationToken ct)
	{
		var userId = await currentUser.GetUserIdAsync(ct);
		var projects = await db.Projects
			.AsNoTracking()
			.Where(x => x.CreatorId == userId)
			.OrderBy(x => x.StartDate)
			.Select(x => new ProjectListItemDto(
				x.Id,
				x.Name,
				x.StartDate,
				x.EndDate,
				x.CreatorId,
				x.Tasks.Count,
				x.Employees.Count))
			.ToListAsync(ct);
		return Ok(projects);
	}

	[HttpPost]
	public async Task<ActionResult<ProjectDetailsDto>> Create(CreateProjectRequest request, CancellationToken ct)
	{
		ValidateProjectDates(request.StartDate, request.EndDate);
		if (string.IsNullOrWhiteSpace(request.Name))
		{
			return ValidationProblem("Project name is required.");
		}

		var userId = await currentUser.GetUserIdAsync(ct);
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = request.Name.Trim(),
			StartDate = request.StartDate,
			EndDate = request.EndDate,
			CreatorId = userId
		};
		var operation = history.Begin(project.Id, "project.create", $"Создан проект: {project.Name}");
		history.Add(operation, "project", project.Id, null, ChangeHistoryService.Snapshot(project));
		db.Projects.Add(project);
		db.ChangeOperations.Add(operation);
		await db.SaveChangesAsync(ct);
		var createdDetails = await BuildDetailsAsync(project.Id, ct);
		await realtime.PublishAsync(project.Id, "project", "created", project.Id, createdDetails, ct);
		return CreatedAtAction(nameof(Get), new { id = project.Id }, await BuildDetailsAsync(project.Id, ct));
	}

	[HttpGet("{id:guid}")]
	public async Task<ActionResult<ProjectDetailsDto>> Get(Guid id, CancellationToken ct)
	{
		var userId = await currentUser.GetUserIdAsync(ct);
		var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.CreatorId == userId, ct);
		return project is null ? NotFound() : Ok(await BuildDetailsAsync(id, ct));
	}

	[HttpPut("{id:guid}")]
	public async Task<ActionResult<ProjectDetailsDto>> Update(Guid id, UpdateProjectRequest request, CancellationToken ct)
	{
		ValidateProjectDates(request.StartDate, request.EndDate);
		if (string.IsNullOrWhiteSpace(request.Name))
		{
			return ValidationProblem("Project name is required.");
		}

		var userId = await currentUser.GetUserIdAsync(ct);
		var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == id && x.CreatorId == userId, ct);
		if (project is null)
		{
			return NotFound();
		}

		var before = ChangeHistoryService.Snapshot(project);
		project.Name = request.Name.Trim();
		project.StartDate = request.StartDate;
		project.EndDate = request.EndDate;
		var operation = history.Begin(project.Id, "project.update", $"Изменён проект: {project.Name}");
		history.Add(operation, "project", project.Id, before, ChangeHistoryService.Snapshot(project));
		db.ChangeOperations.Add(operation);
		await db.SaveChangesAsync(ct);
		var updatedDetails = await BuildDetailsAsync(project.Id, ct);
		await realtime.PublishAsync(project.Id, "project", "updated", project.Id, updatedDetails, ct);
		return Ok(updatedDetails);
	}

	[HttpDelete("{id:guid}")]
	public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
	{
		var userId = await currentUser.GetUserIdAsync(ct);
		var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == id && x.CreatorId == userId, ct);
		if (project is null)
		{
			return NotFound();
		}

		// Удаление проекта является настоящим DELETE. FK с Cascade удаляет
		// сотрудников, задачи и зависимости этого проекта.
		var deleted = await db.Projects
			.Where(x => x.Id == id && x.CreatorId == userId)
			.ExecuteDeleteAsync(ct);

		if (deleted == 0) return NotFound();
		await realtime.PublishAsync(id, "project", "deleted", id, null, ct);
		return NoContent();
	}

	private async Task<ProjectDetailsDto> BuildDetailsAsync(Guid projectId, CancellationToken ct)
	{
		var project = await db.Projects.AsNoTracking()
			.SingleAsync(x => x.Id == projectId, ct);

		var employees = await db.Employees.AsNoTracking()
			.Where(x => x.ProjectId == projectId)
			.OrderBy(x => x.Name)
			.Select(x => new EmployeeDto(x.Id, x.ProjectId, x.Name, x.Phone, x.Email, x.Tasks.Count))
			.ToListAsync(ct);

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

		var dependencies = await db.TaskDependencies.AsNoTracking()
			.Where(x => x.PredecessorTask.ProjectId == projectId)
			.OrderBy(x => x.PredecessorTask.Name)
			.ThenBy(x => x.SuccessorTask.Name)
			.Select(x => new DependencyDto(
				x.PredecessorTaskId,
				x.SuccessorTaskId,
				projectId,
				x.PredecessorTask.Name,
				x.SuccessorTask.Name))
			.ToListAsync(ct);

		var today = DateOnly.FromDateTime(DateTime.UtcNow);
		var warnings = tasks
			.Where(x => x.StartDate < project.StartDate || x.EndDate > project.EndDate || (x.Status != ProjectTaskStatus.Completed.ToString() && x.EndDate < today))
			.Select(x => new AnalysisMessageDto(
				AnalysisSeverity.Warning,
				x.Id,
				x.Name,
				[x.Id],
				[x.Name],
				(x.Status != ProjectTaskStatus.Completed.ToString() && x.EndDate < today)
					? $"Задача просрочена: плановая дата окончания {x.EndDate:yyyy-MM-dd}, текущая дата {today:yyyy-MM-dd}."
					: $"Задача выходит за границы проекта: {x.StartDate:yyyy-MM-dd} - {x.EndDate:yyyy-MM-dd}, проект: {project.StartDate:yyyy-MM-dd} - {project.EndDate:yyyy-MM-dd}.",
				[new AnalysisActionDto("open-task", "Открыть задачу", x.Id)]))
			.ToList();

		return new ProjectDetailsDto(
			project.Id,
			project.Name,
			project.StartDate,
			project.EndDate,
			project.CreatorId,
			employees,
			tasks,
			dependencies,
			warnings);
	}

	private static void ValidateProjectDates(DateOnly start, DateOnly end)
	{
		if (start > end)
		{
			throw new ArgumentException("Project start date cannot be after project end date.");
		}
	}
}
