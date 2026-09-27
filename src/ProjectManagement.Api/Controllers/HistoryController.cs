using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProjectManagement.Api.Data;
using ProjectManagement.Api.Services;

namespace ProjectManagement.Api.Controllers;

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

	[HttpPost("undo")]
	public async Task<ActionResult<ChangeHistoryDto>> Undo(Guid projectId, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		var result = await history.UndoAsync(projectId, ct);
		if (result is null) return NoContent();
		await realtime.PublishAsync(projectId, "history", "undone", result.Id, result, ct);
		foreach (var item in await history.GetItemsAsync(result.Id, ct))
		{
			var action = item.BeforeJson is null ? "deleted" : item.AfterJson is null ? "restored" : "updated";
			var json = item.BeforeJson ?? item.AfterJson;
			object? data = json is null ? null : System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
			await realtime.PublishAsync(projectId, item.EntityType, action, item.EntityId, data, ct);
		}
		return Ok(result);
	}

	private async Task EnsureProjectAsync(Guid projectId, CancellationToken ct)
	{
		var userId = await currentUser.GetUserIdAsync(ct);
		if (!await db.Projects.AnyAsync(x => x.Id == projectId && x.CreatorId == userId, ct))
			throw new KeyNotFoundException("Project not found.");
	}
}
