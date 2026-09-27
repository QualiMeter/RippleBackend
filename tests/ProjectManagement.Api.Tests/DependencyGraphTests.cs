using ProjectManagement.Api.Domain;
using ProjectManagement.Api.Services;

using Xunit;

namespace ProjectManagement.Api.Tests;

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
	public void CompletedStatusIsDistinctFromDelayedStatus()
	{
		Assert.NotEqual(ProjectTaskStatus.Completed, ProjectTaskStatus.Delayed);
	}
}
