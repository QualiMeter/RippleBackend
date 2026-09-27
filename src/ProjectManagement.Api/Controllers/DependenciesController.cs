using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProjectManagement.Api.Contracts;
using ProjectManagement.Api.Data;
using ProjectManagement.Api.Domain;
using ProjectManagement.Api.Services;

namespace ProjectManagement.Api.Controllers;

[ApiController]
[Route("api/v1/projects/{projectId:guid}/dependencies")]
public sealed class DependenciesController(AppDbContext db, ICurrentUserAccessor currentUser, DependencyGraphService graph, ChangeHistoryService history) : ControllerBase
{
	[HttpGet]
	public async Task<ActionResult<IReadOnlyList<DependencyDto>>> GetAll(Guid projectId, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		var links = await db.TaskDependencies.AsNoTracking()
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
		return Ok(links);
	}

	[HttpPost]
	public async Task<ActionResult<DependencyMutationResponse>> Create(Guid projectId, CreateDependencyRequest request, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		var tasks = await db.Tasks.Where(x => x.ProjectId == projectId && (x.Id == request.PredecessorTaskId || x.Id == request.SuccessorTaskId)).ToListAsync(ct);
		if (tasks.Count != 2) throw new ArgumentException("Both tasks must belong to the project.");
		if (request.PredecessorTaskId == request.SuccessorTaskId) throw new ArgumentException("A task cannot depend on itself.");
		if (await db.TaskDependencies.AnyAsync(x => x.PredecessorTaskId == request.PredecessorTaskId && x.SuccessorTaskId == request.SuccessorTaskId, ct)) throw new InvalidOperationException("Duplicate dependency.");
		if (await graph.WouldCreateCycleAsync(request.PredecessorTaskId, request.SuccessorTaskId, ct)) throw new InvalidOperationException("Dependency would create a cycle.");

		var predecessor = tasks.Single(x => x.Id == request.PredecessorTaskId);
		var successor = tasks.Single(x => x.Id == request.SuccessorTaskId);
		var link = new TaskDependency { PredecessorTaskId = predecessor.Id, SuccessorTaskId = successor.Id, CreatedAt = DateTimeOffset.UtcNow };
		var operation = history.Begin(projectId, "dependency.create", $"Добавлена связь: {predecessor.Name} -> {successor.Name}");
		history.Add(operation, "task_dependency", predecessor.Id, null, ChangeHistoryService.Snapshot(link));
		db.TaskDependencies.Add(link);
		db.ChangeOperations.Add(operation);
		await db.SaveChangesAsync(ct);

		var dependency = new DependencyDto(link.PredecessorTaskId, link.SuccessorTaskId, projectId, predecessor.Name, successor.Name);
		var analysis = successor.StartDate < predecessor.EndDate
			? [new AnalysisMessageDto(AnalysisSeverity.Warning, predecessor.Id, predecessor.Name, [successor.Id], [successor.Name], $"Последующая задача начинается {successor.StartDate:yyyy-MM-dd}, раньше окончания предшественника {predecessor.EndDate:yyyy-MM-dd}.", [new AnalysisActionDto("shift-preview", "Рассчитать сдвиг", successor.Id)])]
			: Array.Empty<AnalysisMessageDto>();
		return Created($"/api/v1/projects/{projectId}/dependencies/{link.PredecessorTaskId}/{link.SuccessorTaskId}", new DependencyMutationResponse(dependency, analysis));
	}

	[HttpDelete("{predecessorId:guid}/{successorId:guid}")]
	public async Task<ActionResult<IReadOnlyList<AnalysisMessageDto>>> Delete(Guid projectId, Guid predecessorId, Guid successorId, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		var link = await db.TaskDependencies.Include(x => x.SuccessorTask).SingleOrDefaultAsync(x => x.PredecessorTaskId == predecessorId && x.SuccessorTaskId == successorId && x.SuccessorTask.ProjectId == projectId, ct);
		if (link is null) return NotFound();
		var operation = history.Begin(projectId, "dependency.delete", $"Удалена связь: {link.PredecessorTaskId} -> {link.SuccessorTaskId}");
		history.Add(operation, "task_dependency", link.PredecessorTaskId, ChangeHistoryService.Snapshot(link), null);
		db.TaskDependencies.Remove(link);
		db.ChangeOperations.Add(operation);
		await db.SaveChangesAsync(ct);

		var hasOtherPredecessors = await db.TaskDependencies.AnyAsync(x => x.SuccessorTaskId == successorId, ct);
		if (!hasOtherPredecessors)
		{
			return Ok(new[] { new AnalysisMessageDto(AnalysisSeverity.Info, predecessorId, $"Task {predecessorId}", [successorId], [link.SuccessorTask.Name], "У последующей задачи больше не осталось предшественников.", [new AnalysisActionDto("open-task", "Открыть задачу", successorId)]) });
		}

		return Ok(Array.Empty<AnalysisMessageDto>());
	}

	private async Task EnsureProjectAsync(Guid projectId, CancellationToken ct)
	{
		var userId = await currentUser.GetUserIdAsync(ct);
		if (!await db.Projects.AnyAsync(x => x.Id == projectId && x.CreatorId == userId, ct)) throw new KeyNotFoundException("Project not found.");
	}
}
