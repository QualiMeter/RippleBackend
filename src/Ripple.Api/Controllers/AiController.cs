using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Ripple.Api.Contracts;
using Ripple.Api.Services;

namespace Ripple.Api.Controllers;

[ApiController]
[Route("api/v1/ai")]
public sealed class AiController(AiPlanningService ai) : ControllerBase
{
	[HttpPost("projects/plan")]
	public async Task<ActionResult<AiPlanDto>> CreateProjectPlan(AiPlanRequest request, CancellationToken ct)
	{
		return Ok(await ai.CreatePlanAsync(null, request.Prompt, ct));
	}

	[HttpPost("projects/plan/stream")]
	public async Task CreateProjectPlanStream(AiPlanRequest request, CancellationToken ct)
	{
		await StreamPlanAsync(null, request, ct);
	}

	[HttpPost("plans/{planId:guid}/confirm")]
	public async Task<ActionResult<AiPlanDto>> ConfirmPlan(Guid planId, CancellationToken ct)
	{
		var result = await ai.ConfirmAsync(planId, null, ct);
		return result is null ? NotFound() : Ok(result);
	}

	[HttpGet("plans/{planId:guid}")]
	public async Task<ActionResult<AiPlanDto>> GetPlan(Guid planId, CancellationToken ct)
	{
		var result = await ai.GetPlanAsync(planId, ct);
		return result is null ? NotFound() : Ok(result);
	}

	private async Task StreamPlanAsync(Guid? projectId, AiPlanRequest request, CancellationToken ct)
	{
		Response.ContentType = "text/event-stream";
		Response.Headers.CacheControl = "no-cache";
		Response.Headers.Append("X-Accel-Buffering", "no");
		Response.Headers.ContentEncoding = "identity";

		async Task Send(string type, object data)
		{
			await Response.WriteAsync($"event: {type}\ndata: {JsonSerializer.Serialize(data)}\n\n", ct);
			await Response.Body.FlushAsync(ct);
		}

		try
		{
			var result = await ai.CreatePlanAsync(projectId, request.Prompt, ct, update => Send("progress", update));
			await Send("completed", result);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			await Send("error", new { message = ex.Message });
		}
	}
}

[ApiController]
[Route("api/v1/projects/{projectId:guid}/ai")]
public sealed class ProjectAiController(AiPlanningService ai) : ControllerBase
{
	[HttpPost("plan")]
	public async Task<ActionResult<AiPlanDto>> CreateProjectUpdatePlan(Guid projectId, AiPlanRequest request, CancellationToken ct)
	{
		return Ok(await ai.CreatePlanAsync(projectId, request.Prompt, ct));
	}

	[HttpPost("plan/stream")]
	public async Task CreateProjectUpdatePlanStream(Guid projectId, AiPlanRequest request, CancellationToken ct)
	{
		await StreamPlanAsync(projectId, request, ct);
	}

	private async Task StreamPlanAsync(Guid projectId, AiPlanRequest request, CancellationToken ct)
	{
		Response.ContentType = "text/event-stream";
		Response.Headers.CacheControl = "no-cache";
		Response.Headers.Append("X-Accel-Buffering", "no");
		Response.Headers.ContentEncoding = "identity";

		async Task Send(string type, object data)
		{
			await Response.WriteAsync($"event: {type}\ndata: {JsonSerializer.Serialize(data)}\n\n", ct);
			await Response.Body.FlushAsync(ct);
		}

		try
		{
			var result = await ai.CreatePlanAsync(projectId, request.Prompt, ct, update => Send("progress", update));
			await Send("completed", result);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
		catch (Exception ex) { await Send("error", new { message = ex.Message }); }
	}

	[HttpPost("plan/{planId:guid}/confirm")]
	public async Task<ActionResult<AiPlanDto>> ConfirmProjectUpdatePlan(Guid projectId, Guid planId, CancellationToken ct)
	{
		var result = await ai.ConfirmAsync(planId, projectId, ct);
		return result is null ? NotFound() : Ok(result);
	}
}
