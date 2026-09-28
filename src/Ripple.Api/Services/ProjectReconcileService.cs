using Microsoft.EntityFrameworkCore;
using Ripple.Api.Contracts;
using Ripple.Api.Data;
using Ripple.Api.Domain;

namespace Ripple.Api.Services;

/// <summary>
/// Проверяет весь проект целиком и автоматически исправляет безопасные конфликты дат.
/// Завершённые задачи не изменяются. Длительность незавершённых задач сохраняется.
/// Дату окончания проекта сервис не меняет автоматически.
/// </summary>
public sealed class ProjectReconcileService(
	AppDbContext db,
	DependencyGraphService graph,
	ChangeHistoryService history,
	IRealtimeNotifier realtime,
	AnalysisService analysis)
{
	public async Task<ProjectReconcileResultDto> ReconcileAsync(Guid projectId, CancellationToken ct)
	{
		var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId, ct)
			?? throw new KeyNotFoundException("Project not found.");

		var tasks = await db.Tasks
			.Where(x => x.ProjectId == projectId)
			.ToListAsync(ct);

		var successorMap = await graph.BuildSuccessorMapAsync(projectId, ct);
		var predecessorMap = await graph.BuildPredecessorMapAsync(projectId, ct);
		var order = TopologicalOrder(tasks.Select(x => x.Id), successorMap);
		var taskMap = tasks.ToDictionary(x => x.Id);
		var before = tasks.ToDictionary(x => x.Id, ChangeHistoryService.Snapshot);
		var changes = new List<ProjectReconcileChangeDto>();

		// Сначала исправляем выход за начало проекта.
		foreach (var id in order)
		{
			var task = taskMap[id];
			if (task.Status == ProjectTaskStatus.Completed || task.StartDate >= project.StartDate)
				continue;

			var shift = project.StartDate.DayNumber - task.StartDate.DayNumber;
			Move(task, shift);
		}

		// Затем проходим граф сверху вниз. Каждая незавершённая задача должна
		// начинаться строго после всех своих предшественников.
		foreach (var id in order)
		{
			var task = taskMap[id];
			if (!predecessorMap.TryGetValue(id, out var predecessors))
				continue;

			var predecessorEnds = predecessors
				.Where(taskMap.ContainsKey)
				.Select(x => taskMap[x])
			.Select(x => x.EndDate)
			.ToList();
			if (predecessorEnds.Count == 0)
				continue;

			var requiredStart = DependencyScheduleRules.RequiredSuccessorStart(predecessorEnds.Max());
			if (requiredStart <= task.StartDate)
				continue;

			if (task.Status == ProjectTaskStatus.Completed)
				continue;

			Move(task, requiredStart.DayNumber - task.StartDate.DayNumber);
		}

		// После разрешения зависимостей ещё раз проверяем границу проекта.
		// Если задачу нельзя безопасно сдвинуть назад из-за зависимостей,
		// оставляем её как есть и возвращаем проблему для ручного решения.
		foreach (var id in order.AsEnumerable().Reverse())
		{
			var task = taskMap[id];
			if (task.Status == ProjectTaskStatus.Completed || task.EndDate <= project.EndDate)
				continue;

			var overflow = task.EndDate.DayNumber - project.EndDate.DayNumber;
			var proposedStart = task.StartDate.AddDays(-overflow);
			if (proposedStart < project.StartDate)
				continue;

			// Назад сдвигаем только если это не нарушает предшественников.
			var valid = !predecessorMap.TryGetValue(id, out var predecessors)
				|| predecessors.All(x => !taskMap.ContainsKey(x) || !DependencyScheduleRules.HasDateConflict(taskMap[x].EndDate, proposedStart));
			if (valid)
			{
				task.StartDate = proposedStart;
				task.EndDate = project.EndDate;
			}
		}

		foreach (var task in tasks)
		{
			var old = before[task.Id];
			if (old == ChangeHistoryService.Snapshot(task))
				continue;

			changes.Add(new ProjectReconcileChangeDto(
				task.Id,
				task.Name,
				old.StartDate,
				old.EndDate,
				task.StartDate,
				task.EndDate,
				task.StartDate.DayNumber - old.StartDate.DayNumber,
				"Автоматическое приведение дат задачи в соответствие с границами проекта и зависимостями."));
		}

		var operation = history.Begin(projectId, "project.reconcile", "Автоматическая проверка и приведение проекта в порядок");
		foreach (var task in tasks)
		{
			var old = before[task.Id];
			var current = ChangeHistoryService.Snapshot(task);
			if (old != current)
				history.Add(operation, "task", task.Id, old, current);
		}
		if (operation.Items.Count > 0)
			db.ChangeOperations.Add(operation);

		await db.SaveChangesAsync(ct);

		foreach (var change in changes)
		{
			var dto = await db.Tasks.AsNoTracking()
				.Include(x => x.Assignee)
				.Include(x => x.PredecessorLinks)
				.Include(x => x.SuccessorLinks)
				.SingleAsync(x => x.Id == change.TaskId, ct);
			await realtime.PublishAsync(projectId, "task", "updated", change.TaskId, ProjectMapper.ToDetailsDto(dto), ct);
		}

		var remaining = await analysis.AnalyzeProjectAsync(projectId, ct);
		var problems = changes
			.Select(x => new AnalysisMessageDto(
				AnalysisSeverity.Info,
				x.TaskId,
				x.TaskName,
				[x.TaskId],
				[x.TaskName],
				$"Задача автоматически скорректирована: {x.OriginalStartDate:yyyy-MM-dd} - {x.OriginalEndDate:yyyy-MM-dd} → {x.NewStartDate:yyyy-MM-dd} - {x.NewEndDate:yyyy-MM-dd}.",
				[new AnalysisActionDto("open-task", "Открыть задачу", x.TaskId)]))
			.ToList();

		return new ProjectReconcileResultDto(
			project.Id,
			project.StartDate,
			project.EndDate,
			changes,
			problems,
			remaining);
	}

	private static void Move(ProjectTask task, int days)
	{
		if (days == 0) return;
		task.StartDate = task.StartDate.AddDays(days);
		task.EndDate = task.EndDate.AddDays(days);
	}

	private static List<Guid> TopologicalOrder(IEnumerable<Guid> ids, IReadOnlyDictionary<Guid, List<Guid>> successors)
	{
		var nodes = ids.ToHashSet();
		var indegree = nodes.ToDictionary(x => x, _ => 0);
		foreach (var source in nodes)
		{
			if (!successors.TryGetValue(source, out var next)) continue;
			foreach (var target in next.Where(nodes.Contains)) indegree[target]++;
		}

		var queue = new Queue<Guid>(indegree.Where(x => x.Value == 0).Select(x => x.Key));
		var result = new List<Guid>(nodes.Count);
		while (queue.Count > 0)
		{
			var current = queue.Dequeue();
			result.Add(current);
			if (!successors.TryGetValue(current, out var next)) continue;
			foreach (var target in next.Where(nodes.Contains))
			{
				if (--indegree[target] == 0) queue.Enqueue(target);
			}
		}

		if (result.Count != nodes.Count)
			throw new InvalidOperationException("Dependency graph contains a cycle.");
		return result;
	}
}
