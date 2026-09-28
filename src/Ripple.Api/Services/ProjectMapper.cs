using Ripple.Api.Contracts;
using Ripple.Api.Domain;

namespace Ripple.Api.Services;

public static class ProjectMapper
{
	public static TaskListItemDto ToListDto(ProjectTask task) => new(
		task.Id,
		task.ProjectId,
		task.Name,
		task.StartDate,
		task.EndDate,
		task.EndDate.DayNumber - task.StartDate.DayNumber,
		task.AssigneeId,
		task.Assignee.Name,
		task.Status.ToString());

	public static TaskDetailsDto ToDetailsDto(ProjectTask task) => new(
		task.Id,
		task.ProjectId,
		task.Name,
		task.StartDate,
		task.EndDate,
		task.EndDate.DayNumber - task.StartDate.DayNumber,
		task.AssigneeId,
		task.Assignee.Name,
		task.Status.ToString(),
		task.PredecessorLinks.Select(x => x.PredecessorTaskId).ToList(),
		task.SuccessorLinks.Select(x => x.SuccessorTaskId).ToList());
}
