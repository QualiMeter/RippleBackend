using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProjectManagement.Api.Data;
using ProjectManagement.Api.Services;

namespace ProjectManagement.Api.Controllers;

[ApiController]
[Route("api/v1/projects/{projectId:guid}/history")]
public sealed class HistoryController(AppDbContext db, ICurrentUserAccessor currentUser, ChangeHistoryService history) : ControllerBase
{
	[HttpGet]
	public async Task<ActionResult<IReadOnlyList<ChangeHistoryDto>>> Get(Guid projectId, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		return Ok(await history.GetAsync(projectId, ct));
	}

	[HttpPost("undo")]
	public async Task<ActionResult<ChangeHistoryDto>> Undo(Guid projectId, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		var result = await history.UndoAsync(projectId, ct);
		return result is null ? NoContent() : Ok(result);
	}

	private async Task EnsureProjectAsync(Guid projectId, CancellationToken ct)
	{
		var userId = await currentUser.GetUserIdAsync(ct);
		if (!await db.Projects.AnyAsync(x => x.Id == projectId && x.CreatorId == userId, ct))
			throw new KeyNotFoundException("Project not found.");
	}
}
