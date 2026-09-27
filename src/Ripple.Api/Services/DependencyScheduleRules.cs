namespace Ripple.Api.Services;

public static class DependencyScheduleRules
{
	public static DateOnly RequiredSuccessorStart(DateOnly predecessorEndDate) => predecessorEndDate.AddDays(1);

	public static bool HasDateConflict(DateOnly predecessorEndDate, DateOnly successorStartDate) =>
		successorStartDate <= predecessorEndDate;
}
