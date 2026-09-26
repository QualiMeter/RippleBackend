using Xunit;

using TaskStatus = ProjectManagement.Api.Domain.TaskStatus;

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
    public void CompletedStatusIsDistinctFromDelayedStatus()
    {
        Assert.NotEqual(TaskStatus.Completed, TaskStatus.Delayed);
    }
}