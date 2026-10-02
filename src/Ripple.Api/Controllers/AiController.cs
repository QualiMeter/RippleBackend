using Microsoft.AspNetCore.Mvc;
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

	[HttpPost("plan/{planId:guid}/confirm")]
	public async Task<ActionResult<AiPlanDto>> ConfirmProjectUpdatePlan(Guid projectId, Guid planId, CancellationToken ct)
	{
		var result = await ai.ConfirmAsync(planId, projectId, ct);
		return result is null ? NotFound() : Ok(result);
	}
}
