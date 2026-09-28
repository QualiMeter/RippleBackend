using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ripple.Api.Data;
using Ripple.Api.Services;

namespace Ripple.Api.Controllers;

[ApiController]
[Route("api/v1/projects/{projectId:guid}/history")]
public sealed class HistoryController(AppDbContext db, ICurrentUserAccessor currentUser, ChangeHistoryService history, IRealtimeNotifier realtime) : ControllerBase
{
	[HttpGet]
	public async Task<ActionResult<IReadOnlyList<ChangeHistoryDto>>> Get(Guid projectId, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		return Ok(await history.GetAsync(projectId, ct));
	}

	[HttpPost("{historyId:guid}/undo")]
	public async Task<ActionResult<ChangeHistoryDto>> Undo(Guid projectId, Guid historyId, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		var result = await history.UndoAsync(projectId, historyId, ct);
		if (result is null) return NotFound();
		await realtime.PublishAsync(projectId, "history", "version-restored", result.Id, result, ct);
		await realtime.PublishAsync(projectId, "project", "version-restored", projectId, new
		{
			historyId = result.Id,
			message = "Project state restored to the selected history version."
		}, ct);
		return Ok(result);
	}

	private async Task EnsureProjectAsync(Guid projectId, CancellationToken ct)
	{
		var userId = await currentUser.GetUserIdAsync(ct);
		if (!await db.Projects.AnyAsync(x => x.Id == projectId && x.CreatorId == userId, ct))
			throw new KeyNotFoundException("Project not found.");
	}
}
