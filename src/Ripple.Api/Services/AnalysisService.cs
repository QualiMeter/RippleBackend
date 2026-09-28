using Microsoft.EntityFrameworkCore;
using Ripple.Api.Contracts;
using Ripple.Api.Data;
using Ripple.Api.Domain;

namespace Ripple.Api.Services;

public sealed class AnalysisService(AppDbContext db)
{
	public async Task<IReadOnlyList<AnalysisMessageDto>> AnalyzeProjectBoundaryAsync(Project project, CancellationToken ct)
	{
		var tasks = await db.Tasks.Where(x => x.ProjectId == project.Id).AsNoTracking().ToListAsync(ct);
		return tasks
			.Where(x => x.StartDate < project.StartDate || x.EndDate > project.EndDate || IsOverdue(x, Today))
			.Select(x => new AnalysisMessageDto(
				AnalysisSeverity.Warning,
				x.Id,
				x.Name,
				[x.Id],
				[x.Name],
				IsOverdue(x, Today)
					? $"Задача просрочена: плановая дата окончания {x.EndDate:yyyy-MM-dd}, текущая дата {Today:yyyy-MM-dd}."
					: $"Задача выходит за границы проекта: {x.StartDate:yyyy-MM-dd} - {x.EndDate:yyyy-MM-dd}, проект: {project.StartDate:yyyy-MM-dd} - {project.EndDate:yyyy-MM-dd}.",
				[new AnalysisActionDto("open-task", "Открыть задачу", x.Id)]))
			.ToList();
	}

	public async Task<IReadOnlyList<AnalysisMessageDto>> AnalyzeTaskChangeAsync(ProjectTask before, ProjectTask after, CancellationToken ct)
	{
		var result = new List<AnalysisMessageDto>();
		var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == after.ProjectId, ct);

		AddOverdueWarning(result, after);

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
				if (DependencyScheduleRules.HasDateConflict(after.EndDate, successor.StartDate))
				{
					result.Add(new AnalysisMessageDto(
						AnalysisSeverity.Warning,
						after.Id,
						after.Name,
						[successor.Id],
						[successor.Name],
						$"Последующая задача {successor.Name} начинается {successor.StartDate:yyyy-MM-dd}, раньше либо в тот же день, что и окончание предшественника {after.EndDate:yyyy-MM-dd}.",
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

	public async Task<IReadOnlyList<AnalysisMessageDto>> AnalyzeCurrentTaskAsync(Guid projectId, Guid taskId, CancellationToken ct)
	{
		var task = await db.Tasks.AsNoTracking().SingleAsync(x => x.Id == taskId && x.ProjectId == projectId, ct);
		var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == projectId, ct);
		var result = new List<AnalysisMessageDto>();

		AddOverdueWarning(result, task);

		if (task.StartDate < project.StartDate || task.EndDate > project.EndDate)
		{
			result.Add(new AnalysisMessageDto(
				AnalysisSeverity.Warning,
				task.Id,
				task.Name,
				[task.Id],
				[task.Name],
				$"Задача выходит за границы проекта: {task.StartDate:yyyy-MM-dd} - {task.EndDate:yyyy-MM-dd}, проект: {project.StartDate:yyyy-MM-dd} - {project.EndDate:yyyy-MM-dd}.",
				[new AnalysisActionDto("open-project", "Открыть проект")]));
		}

		var predecessors = await db.TaskDependencies
			.Where(x => x.SuccessorTaskId == task.Id)
			.Join(db.Tasks, x => x.PredecessorTaskId, x => x.Id, (_, t) => t)
			.AsNoTracking()
			.ToListAsync(ct);

		var successors = await db.TaskDependencies
			.Where(x => x.PredecessorTaskId == task.Id)
			.Join(db.Tasks, x => x.SuccessorTaskId, x => x.Id, (_, t) => t)
			.AsNoTracking()
			.ToListAsync(ct);

<<<<<<< HEAD
		foreach (var predecessor in predecessors)
		{
			if (DependencyScheduleRules.HasDateConflict(predecessor.EndDate, task.StartDate))
			{
				result.Add(new AnalysisMessageDto(
					AnalysisSeverity.Warning,
					task.Id,
					task.Name,
					[predecessor.Id],
					[predecessor.Name],
					$"Задача {task.Name} начинается {task.StartDate:yyyy-MM-dd}, раньше либо в тот же день, что и окончание предшественника {predecessor.Name} ({predecessor.EndDate:yyyy-MM-dd}). Между задачами есть конфликт дат.",
					[new AnalysisActionDto("open-task", "Открыть предшественника", predecessor.Id), new AnalysisActionDto("shift-preview", "Рассчитать сдвиг", task.Id)]));
			}
		}

=======
>>>>>>> ca19c44d5c1ec2f61918f445263a5954b49e58d3
		foreach (var successor in successors)
		{
			if (DependencyScheduleRules.HasDateConflict(task.EndDate, successor.StartDate))
			{
				result.Add(new AnalysisMessageDto(
					AnalysisSeverity.Warning,
					task.Id,
					task.Name,
					[successor.Id],
					[successor.Name],
					$"Последующая задача {successor.Name} начинается {successor.StartDate:yyyy-MM-dd}, раньше либо в тот же день, что и окончание предшественника {task.EndDate:yyyy-MM-dd}.",
					[new AnalysisActionDto("shift-preview", "Рассчитать сдвиг", successor.Id)]));
			}
		}

		if (task.Status == ProjectTaskStatus.InProgress)
		{
			var unfinishedPredecessors = predecessors.Where(x => x.Status != ProjectTaskStatus.Completed).ToList();
			if (unfinishedPredecessors.Count > 0)
			{
				result.Add(new AnalysisMessageDto(
					AnalysisSeverity.Warning,
					task.Id,
					task.Name,
					unfinishedPredecessors.Select(x => x.Id).ToList(),
					unfinishedPredecessors.Select(x => x.Name).ToList(),
					"У задачи есть незавершённые предшественники.",
					unfinishedPredecessors.Select(x => new AnalysisActionDto("open-task", "Открыть задачу", x.Id)).ToList()));
			}
		}

		if (task.Status == ProjectTaskStatus.Delayed)
		{
			var unfinishedSuccessors = successors.Where(x => x.Status != ProjectTaskStatus.Completed).ToList();
			if (unfinishedSuccessors.Count > 0)
			{
				result.Add(new AnalysisMessageDto(
					AnalysisSeverity.Warning,
					task.Id,
					task.Name,
					unfinishedSuccessors.Select(x => x.Id).ToList(),
					unfinishedSuccessors.Select(x => x.Name).ToList(),
					"Задача отмечена как задерживающаяся. Это создаёт риск для незавершённых последующих задач: для задач «Не в работе» возможен сдвиг даты начала, для задач «В работе» требуется ручная оценка.",
					unfinishedSuccessors.Select(x => new AnalysisActionDto("open-task", "Открыть задачу", x.Id)).ToList()));
			}
		}

		if (task.Status == ProjectTaskStatus.Completed)
		{
			foreach (var successor in successors)
			{
				var hasUnfinishedPredecessors = await HasUnfinishedPredecessorsAsync(successor.Id, task.Id, ct);
				if (!hasUnfinishedPredecessors)
				{
					var text = successor.StartDate <= Today
						? $"Все предшественники задачи {successor.Name} завершены. Её можно начинать."
						: $"Все предшественники задачи {successor.Name} завершены. Запланированная дата начала — {successor.StartDate:yyyy-MM-dd}; работу потенциально можно начать раньше.";
					result.Add(new AnalysisMessageDto(AnalysisSeverity.Info, task.Id, task.Name, [successor.Id], [successor.Name], text,
						[new AnalysisActionDto("open-task", "Открыть задачу", successor.Id)]));
				}
			}
		}

		return result;
	}

	private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

	public static bool IsOverdue(ProjectTask task, DateOnly today) =>
		task.Status != ProjectTaskStatus.Completed && task.EndDate < today;

	private static void AddOverdueWarning(List<AnalysisMessageDto> result, ProjectTask task)
	{
		if (!IsOverdue(task, Today)) return;

		result.Add(new AnalysisMessageDto(
			AnalysisSeverity.Warning,
			task.Id,
			task.Name,
			[task.Id],
			[task.Name],
			$"Задача просрочена: плановая дата окончания {task.EndDate:yyyy-MM-dd}, текущая дата {Today:yyyy-MM-dd}. Задача не завершена и требует внимания.",
			[new AnalysisActionDto("open-task", "Открыть задачу", task.Id)]));
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
