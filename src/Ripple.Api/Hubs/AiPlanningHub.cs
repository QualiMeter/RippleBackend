using Microsoft.AspNetCore.SignalR;
using Ripple.Api.Contracts;
using Ripple.Api.Services;

namespace Ripple.Api.Hubs;

public sealed class AiPlanningHub(AiPlanningService ai) : Hub
{
	public async Task<AiPlanDto> CreateProjectPlan(string prompt)
	{
		return await CreatePlanAsync(null, prompt);
	}

	public async Task<AiPlanDto> CreateProjectUpdatePlan(Guid projectId, string prompt)
	{
		return await CreatePlanAsync(projectId, prompt);
	}

	private async Task<AiPlanDto> CreatePlanAsync(Guid? projectId, string prompt)
	{
		var connectionId = Context.ConnectionId;
		var cancellationToken = Context.ConnectionAborted;

		async Task SendProgress(AiProgressUpdate progress)
		{
			if (cancellationToken.IsCancellationRequested) return;
			await Clients.Client(connectionId).SendAsync("aiPlanProgress", progress, cancellationToken);
		}

		var result = await ai.CreatePlanAsync(projectId, prompt, cancellationToken, SendProgress);
		await Clients.Client(connectionId).SendAsync("aiPlanCompleted", result, cancellationToken);
		return result;
	}
}
