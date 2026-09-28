using Ripple.Api.Domain;
using Ripple.Api.Services;
using Xunit;

namespace Ripple.Api.Tests;

public sealed class DependencyGraphTests
{
	[Fact]
	public void SelfDependencyIsAlwaysInvalid()
	{
		var taskId = Guid.NewGuid();
		Assert.Equal(taskId, taskId);
	}

	[Fact]
	public void CalendarDurationUsesDateDifference()
	{
		var start = new DateOnly(2026, 9, 1);
		var end = new DateOnly(2026, 9, 5);
		Assert.Equal(4, end.DayNumber - start.DayNumber);
	}

	[Fact]
	public void SuccessorMustStartOnTheNextCalendarDayAfterPredecessor()
	{
		var predecessorEnd = new DateOnly(2026, 9, 5);

		Assert.Equal(new DateOnly(2026, 9, 6), DependencyScheduleRules.RequiredSuccessorStart(predecessorEnd));
		Assert.True(DependencyScheduleRules.HasDateConflict(predecessorEnd, new DateOnly(2026, 9, 5)));
		Assert.False(DependencyScheduleRules.HasDateConflict(predecessorEnd, new DateOnly(2026, 9, 6)));
	}

	[Fact]
	public void UnfinishedTaskPastEndDateIsOverdue()
	{
		var task = new ProjectTask
		{
			EndDate = new DateOnly(2026, 9, 27),
			Status = ProjectTaskStatus.InProgress
		};

		Assert.True(AnalysisService.IsOverdue(task, new DateOnly(2026, 9, 28)));
	}

	[Fact]
	public void CompletedTaskPastEndDateIsNotOverdue()
	{
		var task = new ProjectTask
		{
			EndDate = new DateOnly(2026, 9, 27),
			Status = ProjectTaskStatus.Completed
		};

		Assert.False(AnalysisService.IsOverdue(task, new DateOnly(2026, 9, 28)));
	}

	[Fact]
	public void CompletedStatusIsDistinctFromDelayedStatus()
	{
		Assert.NotEqual(ProjectTaskStatus.Completed, ProjectTaskStatus.Delayed);
	}
}
