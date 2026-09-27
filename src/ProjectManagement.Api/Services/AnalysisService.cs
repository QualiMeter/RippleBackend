using Microsoft.EntityFrameworkCore;
using ProjectManagement.Api.Contracts;
using ProjectManagement.Api.Data;
using ProjectManagement.Api.Domain;

namespace ProjectManagement.Api.Services;

public sealed class AnalysisService(AppDbContext db)
{
	public async Task<IReadOnlyList<AnalysisMessageDto>> AnalyzeProjectBoundaryAsync(Project project, CancellationToken ct)
	{
		var tasks = await db.Tasks.Where(x => x.ProjectId == project.Id).AsNoTracking().ToListAsync(ct);
		return tasks
			.Where(x => x.StartDate < project.StartDate || x.EndDate > project.EndDate)
			.Select(x => new AnalysisMessageDto(
				AnalysisSeverity.Warning,
				x.Id,
				x.Name,
				[x.Id],
				[x.Name],
				$"Задача выходит за границы проекта: {x.StartDate:yyyy-MM-dd} - {x.EndDate:yyyy-MM-dd}, проект: {project.StartDate:yyyy-MM-dd} - {project.EndDate:yyyy-MM-dd}.",
				[new AnalysisActionDto("open-task", "Открыть задачу", x.Id)]))
			.ToList();
	}

	public async Task<IReadOnlyList<AnalysisMessageDto>> AnalyzeTaskChangeAsync(ProjectTask before, ProjectTask after, CancellationToken ct)
	{
		var result = new List<AnalysisMessageDto>();
		var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == after.ProjectId, ct);

		if (before.StartDate != after.StartDate || before.EndDate != after.EndDate)
		{
			if (after.StartDate < project.StartDate || after.EndDate > project.EndDate)
			{
				result.Add(new AnalysisMessageDto(
					AnalysisSeverity.Warning,
					after.Id,
					after.Name,
					[after.Id],
					[after.Name],
					"После изменения задача вышла за границы проекта.",
					[new AnalysisActionDto("open-project", "Открыть проект")]));
			}

			var successorIds = await db.TaskDependencies
				.Where(x => x.PredecessorTaskId == after.Id)
				.Select(x => x.SuccessorTaskId)
				.ToListAsync(ct);

			var successors = await db.Tasks.Where(x => successorIds.Contains(x.Id)).AsNoTracking().ToListAsync(ct);
			foreach (var successor in successors)
			{
				if (successor.StartDate < after.EndDate)
				{
					result.Add(new AnalysisMessageDto(
						AnalysisSeverity.Warning,
						after.Id,
						after.Name,
						[successor.Id],
						[successor.Name],
						$"Последующая задача {successor.Name} начинается {successor.StartDate:yyyy-MM-dd}, раньше окончания предшественника {after.EndDate:yyyy-MM-dd}.",
						[new AnalysisActionDto("shift-preview", "Рассчитать сдвиг", successor.Id)]));
				}
			}
		}

		if (before.Status != after.Status)
		{
			var predecessors = await db.TaskDependencies
				.Where(x => x.SuccessorTaskId == after.Id)
				.Join(db.Tasks, x => x.PredecessorTaskId, x => x.Id, (_, task) => task)
				.AsNoTracking()
				.ToListAsync(ct);

			var successors = await db.TaskDependencies
				.Where(x => x.PredecessorTaskId == after.Id)
				.Join(db.Tasks, x => x.SuccessorTaskId, x => x.Id, (_, task) => task)
				.AsNoTracking()
				.ToListAsync(ct);

			if (after.Status == ProjectTaskStatus.Completed)
			{
				foreach (var successor in successors)
				{
					var remaining = await HasUnfinishedPredecessorsAsync(successor.Id, after.Id, ct);
					if (!remaining)
					{
						var text = successor.StartDate <= DateOnly.FromDateTime(DateTime.UtcNow.Date)
							? $"Все предшественники задачи {successor.Name} завершены. Её можно начинать."
							: $"Все предшественники задачи {successor.Name} завершены. Запланированная дата начала - {successor.StartDate:yyyy-MM-dd}; работу потенциально можно начать раньше.";

						result.Add(new AnalysisMessageDto(
							AnalysisSeverity.Info,
							after.Id,
							after.Name,
							[successor.Id],
							[successor.Name],
							text,
							[new AnalysisActionDto("open-task", "Открыть задачу", successor.Id)]));
					}
				}
			}
			else if (before.Status == ProjectTaskStatus.Completed)
			{
				var affected = successors.Select(x => x.Id).ToList();
				if (affected.Count > 0)
				{
					result.Add(new AnalysisMessageDto(
						AnalysisSeverity.Warning,
						after.Id,
						after.Name,
						affected,
						successors.Select(x => x.Name).ToList(),
						"Предшественник больше не отмечен как завершённый. Возможность начать последующие задачи необходимо повторно проверить.",
						successors.Select(x => new AnalysisActionDto("open-task", "Открыть задачу", x.Id)).ToList()));
				}
			}

			if (after.Status == ProjectTaskStatus.Delayed || before.Status == ProjectTaskStatus.Delayed)
			{
				var unfinished = successors.Where(x => x.Status != ProjectTaskStatus.Completed).ToList();
				if (unfinished.Count > 0)
				{
					result.Add(new AnalysisMessageDto(
						AnalysisSeverity.Warning,
						after.Id,
						after.Name,
						unfinished.Select(x => x.Id).ToList(),
						unfinished.Select(x => x.Name).ToList(),
						"Изменение статуса на/с «Задерживается» может повлиять на незавершённые последующие задачи. Для задач «Не в работе» существует риск сдвига даты начала, для задач «В работе» это предупреждение не означает автоматическую остановку.",
						unfinished.Select(x => new AnalysisActionDto("open-task", "Открыть задачу", x.Id)).ToList()));
				}
			}

			if (after.Status == ProjectTaskStatus.InProgress)
			{
				var unfinishedPredecessors = predecessors.Where(x => x.Status != ProjectTaskStatus.Completed).ToList();
				if (unfinishedPredecessors.Count > 0)
				{
					result.Add(new AnalysisMessageDto(
						AnalysisSeverity.Warning,
						after.Id,
						after.Name,
						unfinishedPredecessors.Select(x => x.Id).ToList(),
						unfinishedPredecessors.Select(x => x.Name).ToList(),
						"У задачи есть незавершённые предшественники.",
						unfinishedPredecessors.Select(x => new AnalysisActionDto("open-task", "Открыть задачу", x.Id)).ToList()));
				}
			}
		}

		return result;
	}

	public static bool IsCompleted(ProjectTaskStatus status) => status == ProjectTaskStatus.Completed;

	private async Task<bool> HasUnfinishedPredecessorsAsync(Guid taskId, Guid excludedPredecessorId, CancellationToken ct)
	{
		var unfinishedCount = await db.TaskDependencies
			.Where(x => x.SuccessorTaskId == taskId && x.PredecessorTaskId != excludedPredecessorId)
			.Join(db.Tasks.Where(t => t.Status != ProjectTaskStatus.Completed), x => x.PredecessorTaskId, t => t.Id, (x, t) => t.Id)
			.CountAsync(ct);
		return unfinishedCount == 0;
	}
}
