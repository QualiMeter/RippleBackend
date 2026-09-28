using Microsoft.EntityFrameworkCore;
using Ripple.Api.Contracts;
using Ripple.Api.Data;
using Ripple.Api.Domain;

namespace Ripple.Api.Services;

public sealed class ShiftService(AppDbContext db, DependencyGraphService graph, ChangeHistoryService history, IRealtimeNotifier realtime)
{
	public async Task<ShiftPreviewDto> BuildPreviewAsync(Guid projectId, Guid rootTaskId, CancellationToken ct)
	{
		var project = await db.Projects.SingleAsync(x => x.Id == projectId, ct);
		var tasks = await db.Tasks.Where(x => x.ProjectId == projectId).AsNoTracking().ToListAsync(ct);
		var taskMap = tasks.ToDictionary(x => x.Id);
		if (!taskMap.ContainsKey(rootTaskId))
		{
			throw new KeyNotFoundException("Task does not belong to project.");
		}

		var successorMap = await graph.BuildSuccessorMapAsync(projectId, ct);
		var predecessorMap = await graph.BuildPredecessorMapAsync(projectId, ct);
		var reachable = CollectReachable(rootTaskId, successorMap);
		var proposedStart = tasks.ToDictionary(x => x.Id, x => x.StartDate);
		var proposedEnd = tasks.ToDictionary(x => x.Id, x => x.EndDate);
		var duration = tasks.ToDictionary(x => x.Id, x => x.EndDate.DayNumber - x.StartDate.DayNumber);
		var items = new List<ShiftPreviewItemDto>();

		var order = TopologicalOrder(reachable, successorMap);
		foreach (var id in order)
		{
			if (id == rootTaskId)
			{
				continue;
			}

			var task = taskMap[id];
			var predecessorIds = predecessorMap.TryGetValue(id, out var preds) ? preds : [];
			var predecessorEnds = predecessorIds.Select(x => proposedEnd[x]).ToList();
			var requiredStart = predecessorEnds.Count == 0
				? proposedStart[id]
				: DependencyScheduleRules.RequiredSuccessorStart(predecessorEnds.Max());

			if (requiredStart <= proposedStart[id])
			{
				continue;
			}

			if (task.Status == ProjectTaskStatus.Completed)
			{
				items.Add(new ShiftPreviewItemDto(
					task.Id,
					task.Name,
					task.StartDate,
					task.EndDate,
					task.StartDate,
					task.EndDate,
					0,
					true,
					"Завершённую задачу нельзя сдвигать автоматически."));
				continue;
			}

			var shift = requiredStart.DayNumber - proposedStart[id].DayNumber;
			proposedStart[id] = requiredStart;
			proposedEnd[id] = requiredStart.AddDays(duration[id]);
			items.Add(new ShiftPreviewItemDto(
				task.Id,
				task.Name,
				task.StartDate,
				task.EndDate,
				proposedStart[id],
				proposedEnd[id],
				shift,
				false,
				null));
		}

		var proposedProjectEnd = proposedEnd.Values.Max();
		if (proposedProjectEnd < project.EndDate)
		{
			proposedProjectEnd = project.EndDate;
		}

		var analysis = items
			.Select(x => new AnalysisMessageDto(
				x.CompletedRequiresManualResolution ? AnalysisSeverity.Warning : AnalysisSeverity.Info,
				rootTaskId,
				taskMap[rootTaskId].Name,
				[x.TaskId],
				[x.TaskName],
				x.CompletedRequiresManualResolution
					? $"Задача {x.TaskName} требует ручного решения из-за зависимости от изменившегося предшественника."
					: $"Предварительный сдвиг задачи {x.TaskName}: +{x.ShiftCalendarDays} календарных дн. ({x.OriginalStartDate:yyyy-MM-dd} - {x.OriginalEndDate:yyyy-MM-dd} -> {x.ProposedStartDate:yyyy-MM-dd} - {x.ProposedEndDate:yyyy-MM-dd}).",
				[new AnalysisActionDto("open-task", "Открыть задачу", x.TaskId)]))
			.ToList();

		if (proposedProjectEnd > project.EndDate)
		{
			analysis.Add(new AnalysisMessageDto(
				AnalysisSeverity.Warning,
				rootTaskId,
				taskMap[rootTaskId].Name,
				items.Where(x => !x.CompletedRequiresManualResolution).Select(x => x.TaskId).ToList(),
				items.Where(x => !x.CompletedRequiresManualResolution).Select(x => x.TaskName).ToList(),
				$"Предварительная дата окончания проекта увеличивается с {project.EndDate:yyyy-MM-dd} до {proposedProjectEnd:yyyy-MM-dd}. Изменение даты окончания проекта требует отдельного подтверждения.",
				[new AnalysisActionDto("confirm-project-end", "Подтвердить новую дату окончания проекта")]));
		}

		return new ShiftPreviewDto(
			rootTaskId,
			items,
			project.EndDate,
			proposedProjectEnd,
			Math.Max(0, proposedProjectEnd.DayNumber - project.EndDate.DayNumber),
			analysis);
	}

	public async Task<ShiftConfirmationResponse> ConfirmAsync(Guid projectId, Guid rootTaskId, bool confirmProjectEnd, CancellationToken ct)
	{
		var executionStrategy = db.Database.CreateExecutionStrategy();

		return await executionStrategy.ExecuteAsync(async () =>
		{
			await using var transaction = await db.Database.BeginTransactionAsync(ct);
			var preview = await BuildPreviewAsync(projectId, rootTaskId, ct);
			var changedTaskIds = preview.Items.Where(x => !x.CompletedRequiresManualResolution && x.ShiftCalendarDays != 0).Select(x => x.TaskId).ToList();
			var beforeTasks = await db.Tasks.Where(x => changedTaskIds.Contains(x.Id)).ToListAsync(ct);
			var projectBefore = await db.Projects.SingleAsync(x => x.Id == projectId, ct);

			foreach (var item in preview.Items.Where(x => !x.CompletedRequiresManualResolution && x.ShiftCalendarDays != 0))
			{
				var task = await db.Tasks.SingleAsync(x => x.Id == item.TaskId && x.ProjectId == projectId, ct);
				task.StartDate = item.ProposedStartDate;
				task.EndDate = item.ProposedEndDate;
			}

			var projectEndChanged = confirmProjectEnd && preview.ProposedProjectEndDate != preview.CurrentProjectEndDate;
			if (projectEndChanged)
			{
				var project = await db.Projects.SingleAsync(x => x.Id == projectId, ct);
				project.EndDate = preview.ProposedProjectEndDate;
			}

			var operation = history.Begin(projectId, "task.shift", "Подтверждён автоматический сдвиг зависимых задач");
			foreach (var before in beforeTasks)
			{
				var after = await db.Tasks.AsNoTracking().SingleAsync(x => x.Id == before.Id, ct);
				history.Add(operation, "task", before.Id, ChangeHistoryService.Snapshot(before), ChangeHistoryService.Snapshot(after));
			}
			if (projectEndChanged)
				history.Add(operation, "project", projectId, ChangeHistoryService.Snapshot(projectBefore), ChangeHistoryService.Snapshot(await db.Projects.AsNoTracking().SingleAsync(x => x.Id == projectId, ct)));
			if (operation.Items.Count > 0)
				db.ChangeOperations.Add(operation);
			await db.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);

			foreach (var item in preview.Items.Where(x => !x.CompletedRequiresManualResolution && x.ShiftCalendarDays != 0))
			{
				var dto = await db.Tasks.AsNoTracking().Include(x => x.Assignee).Include(x => x.PredecessorLinks).Include(x => x.SuccessorLinks).SingleAsync(x => x.Id == item.TaskId, ct);
				await realtime.PublishAsync(projectId, "task", "updated", item.TaskId, ProjectMapper.ToDetailsDto(dto), ct);
			}
			if (projectEndChanged)
				await realtime.PublishAsync(projectId, "project", "updated", projectId, new { endDate = preview.ProposedProjectEndDate }, ct);

			var refreshed = await BuildPreviewAsync(projectId, rootTaskId, ct);
			if (!projectEndChanged)
			{
				refreshed = refreshed with
				{
					ProposedProjectEndDate = refreshed.CurrentProjectEndDate,
					ProjectEndIncreaseCalendarDays = 0,
					Analysis = refreshed.Analysis
						.Where(x => x.Actions.All(a => a.Code != "confirm-project-end"))
						.ToList()
				};
			}
			return new ShiftConfirmationResponse(refreshed, projectEndChanged);
		});
	}

	private static HashSet<Guid> CollectReachable(Guid root, IReadOnlyDictionary<Guid, List<Guid>> successors)
	{
		var result = new HashSet<Guid> { root };
		var stack = new Stack<Guid>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			var current = stack.Pop();
			if (!successors.TryGetValue(current, out var next))
			{
				continue;
			}
			foreach (var id in next)
			{
				if (result.Add(id))
				{
					stack.Push(id);
				}
			}
		}
		return result;
	}

	private static List<Guid> TopologicalOrder(HashSet<Guid> nodes, IReadOnlyDictionary<Guid, List<Guid>> successors)
	{
		var indegree = nodes.ToDictionary(x => x, _ => 0);
		foreach (var source in nodes)
		{
			if (!successors.TryGetValue(source, out var next))
			{
				continue;
			}
			foreach (var target in next.Where(nodes.Contains))
			{
				indegree[target]++;
			}
		}

		var queue = new Queue<Guid>(indegree.Where(x => x.Value == 0).Select(x => x.Key));
		var result = new List<Guid>();
		while (queue.Count > 0)
		{
			var current = queue.Dequeue();
			result.Add(current);
			if (!successors.TryGetValue(current, out var next))
			{
				continue;
			}
			foreach (var target in next.Where(nodes.Contains))
			{
				indegree[target]--;
				if (indegree[target] == 0)
				{
					queue.Enqueue(target);
				}
			}
		}

		if (result.Count != nodes.Count)
		{
			throw new InvalidOperationException("Dependency graph contains a cycle.");
		}

		return result;
	}
}