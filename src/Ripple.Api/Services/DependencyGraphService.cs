using Microsoft.EntityFrameworkCore;
using Ripple.Api.Data;

namespace Ripple.Api.Services;

public sealed class DependencyGraphService(AppDbContext db)
{
	public async Task<bool> WouldCreateCycleAsync(Guid predecessorId, Guid successorId, CancellationToken ct)
	{
		if (predecessorId == successorId)
		{
			return true;
		}

		var edges = await db.TaskDependencies.AsNoTracking().ToListAsync(ct);
		var adjacency = edges
			.GroupBy(x => x.PredecessorTaskId)
			.ToDictionary(x => x.Key, x => x.Select(y => y.SuccessorTaskId).ToList());

		var stack = new Stack<Guid>();
		var visited = new HashSet<Guid>();
		stack.Push(successorId);

		while (stack.Count > 0)
		{
			var current = stack.Pop();
			if (!visited.Add(current))
			{
				continue;
			}

			if (current == predecessorId)
			{
				return true;
			}

			if (adjacency.TryGetValue(current, out var next))
			{
				foreach (var item in next)
				{
					stack.Push(item);
				}
			}
		}

		return false;
	}

	public async Task<IReadOnlyDictionary<Guid, List<Guid>>> BuildPredecessorMapAsync(Guid projectId, CancellationToken ct)
	{
		var links = await db.TaskDependencies
			.Where(x => x.PredecessorTask.ProjectId == projectId)
			.AsNoTracking()
			.ToListAsync(ct);

		return links
			.GroupBy(x => x.SuccessorTaskId)
			.ToDictionary(x => x.Key, x => x.Select(v => v.PredecessorTaskId).ToList());
	}

	public async Task<IReadOnlyDictionary<Guid, List<Guid>>> BuildSuccessorMapAsync(Guid projectId, CancellationToken ct)
	{
		var links = await db.TaskDependencies
			.Where(x => x.PredecessorTask.ProjectId == projectId)
			.AsNoTracking()
			.ToListAsync(ct);

		return links
			.GroupBy(x => x.PredecessorTaskId)
			.ToDictionary(x => x.Key, x => x.Select(v => v.SuccessorTaskId).ToList());
	}
}
