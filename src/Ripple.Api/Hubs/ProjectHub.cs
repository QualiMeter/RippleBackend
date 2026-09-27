using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Ripple.Api.Services;

namespace Ripple.Api.Hubs;

public sealed class ProjectHub(AppDbContextAccessor accessor, ICurrentUserAccessor currentUser) : Hub
{
	public async Task JoinProject(Guid projectId)
	{
		var userId = await currentUser.GetUserIdAsync(Context.ConnectionAborted);
		if (!await accessor.Db.Projects.AnyAsync(x => x.Id == projectId && x.CreatorId == userId, Context.ConnectionAborted))
			throw new HubException("Project not found.");

		await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Project(projectId), Context.ConnectionAborted);
	}

	public Task LeaveProject(Guid projectId) =>
		Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeGroups.Project(projectId), Context.ConnectionAborted);
}
