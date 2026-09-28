using Microsoft.EntityFrameworkCore;
using Ripple.Api.Contracts;
using Ripple.Api.Data;
using Ripple.Api.Domain;

namespace Ripple.Api.Services;

public sealed class ProjectDiagnosticsService(AppDbContext db, AnalysisService analysis, ChangeHistoryService history)
{
	public async Task<ProjectDiagnosticsDto> GetAsync(Guid projectId, CancellationToken ct)
	{
		var project = await db.Projects.AsNoTracking()
			.SingleOrDefaultAsync(x => x.Id == projectId, ct)
			?? throw new KeyNotFoundException("Project not found.");

		var employees = await db.Employees.AsNoTracking()
			.Where(x => x.ProjectId == projectId)
			.OrderBy(x => x.Name)
			.Select(x => new ProjectDiagnosticsEmployeeDto(
				x.Id,
				x.Name,
				x.Phone,
				x.Email,
				x.Tasks.OrderBy(t => t.StartDate).Select(t => t.Id).ToList()))
			.ToListAsync(ct);

		var tasks = await db.Tasks.AsNoTracking()
			.Where(x => x.ProjectId == projectId)
			.Include(x => x.Assignee)
			.Include(x => x.PredecessorLinks)
			.Include(x => x.SuccessorLinks)
			.OrderBy(x => x.StartDate)
			.ThenBy(x => x.Name)
			.ToListAsync(ct);

		var today = DateOnly.FromDateTime(DateTime.UtcNow);
		var taskDtos = tasks.Select(x => new ProjectDiagnosticsTaskDto(
			x.Id,
			x.Name,
			x.StartDate,
			x.EndDate,
			x.EndDate.DayNumber - x.StartDate.DayNumber,
			x.AssigneeId,
			x.Assignee.Name,
			x.Status.ToString(),
			x.StartDate < project.StartDate || x.EndDate > project.EndDate,
			AnalysisService.IsOverdue(x, today),
			x.PredecessorLinks.Select(d => d.PredecessorTaskId).ToList(),
			x.SuccessorLinks.Select(d => d.SuccessorTaskId).ToList())).ToList();

		var dependencies = await db.TaskDependencies.AsNoTracking()
			.Where(x => x.PredecessorTask.ProjectId == projectId)
			.Select(x => new ProjectDiagnosticsDependencyDto(
				x.PredecessorTaskId,
				x.PredecessorTask.Name,
				x.PredecessorTask.EndDate,
				x.SuccessorTaskId,
				x.SuccessorTask.Name,
				x.SuccessorTask.StartDate,
				DependencyScheduleRules.HasDateConflict(x.PredecessorTask.EndDate, x.SuccessorTask.StartDate)))
			.OrderBy(x => x.PredecessorTaskName)
			.ThenBy(x => x.SuccessorTaskName)
			.ToListAsync(ct);

		var projectAnalysis = await analysis.AnalyzeProjectAsync(projectId, ct);
		var taskAnalysis = new Dictionary<Guid, IReadOnlyList<AnalysisMessageDto>>();
		foreach (var task in tasks)
			taskAnalysis[task.Id] = await analysis.AnalyzeCurrentTaskAsync(projectId, task.Id, ct);

		var historyItems = new List<ProjectDiagnosticsHistoryDto>();
		var historyEntries = await db.ChangeOperations.AsNoTracking()
			.Where(x => x.ProjectId == projectId)
			.OrderByDescending(x => x.CreatedAt)
			.ToListAsync(ct);
		foreach (var entry in historyEntries)
		{
			historyItems.Add(new ProjectDiagnosticsHistoryDto(
				entry.Id,
				entry.OperationType,
				entry.Description,
				entry.CreatedAt,
				entry.UndoneAt,
				entry.UndoneAt is null,
				await history.GetItemsAsync(entry.Id, ct)));
		}

		return new ProjectDiagnosticsDto(
			DateTimeOffset.UtcNow,
			new ProjectDiagnosticsProjectDto(project.Id, project.Name, project.StartDate, project.EndDate, project.CreatorId, tasks.Count, employees.Count, dependencies.Count),
			employees,
			taskDtos,
			dependencies,
			projectAnalysis,
			taskAnalysis,
			historyItems);
	}
}
