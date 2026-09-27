using Microsoft.AspNetCore.SignalR;
using Ripple.Api.Hubs;

namespace Ripple.Api.Services;

public sealed record RealtimeEvent(Guid EventId, Guid ProjectId, string Entity, string Action, Guid? EntityId, object? Data, DateTimeOffset OccurredAt);

public interface IRealtimeNotifier
{
	Task PublishAsync(Guid projectId, string entity, string action, Guid? entityId, object? data, CancellationToken ct = default);
}

public sealed class RealtimeNotifier(IHubContext<ProjectHub> hub) : IRealtimeNotifier
{
	public Task PublishAsync(Guid projectId, string entity, string action, Guid? entityId, object? data, CancellationToken ct = default)
	{
		var message = new RealtimeEvent(Guid.NewGuid(), projectId, entity, action, entityId, data, DateTimeOffset.UtcNow);
		return hub.Clients.Group(RealtimeGroups.Project(projectId)).SendAsync("projectChanged", message, ct);
	}
}

public static class RealtimeGroups
{
	public static string Project(Guid projectId) => $"project:{projectId:N}";
}
