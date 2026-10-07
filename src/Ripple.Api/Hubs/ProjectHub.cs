using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Ripple.Api.Services;

namespace Ripple.Api.Hubs;

public sealed class ProjectHub(AppDbContextAccessor accessor, ICurrentUserAccessor currentUser) : Hub
{
	public async Task JoinProject(Guid projectId)
	{
		var cancellationToken = Context.ConnectionAborted;

		try
		{
			var userId = await currentUser.GetUserIdAsync(cancellationToken);
			if (!await accessor.Db.Projects.AnyAsync(x => x.Id == projectId && x.CreatorId == userId, cancellationToken))
				throw new HubException("Project not found.");

			await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Project(projectId), cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// SignalR cancels the token when the client disconnects or reconnects.
			// This is an expected condition and must not be reported as a failed hub invocation.
		}
	}

	public async Task LeaveProject(Guid projectId)
	{
		var cancellationToken = Context.ConnectionAborted;

		try
		{
			await Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeGroups.Project(projectId), cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// The connection is already gone; there is nothing left to remove.
		}
	}
}
