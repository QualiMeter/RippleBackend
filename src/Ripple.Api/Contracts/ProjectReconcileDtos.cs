namespace Ripple.Api.Contracts;

public sealed record ProjectReconcileChangeDto(
	Guid TaskId,
	string TaskName,
	DateOnly OriginalStartDate,
	DateOnly OriginalEndDate,
	DateOnly NewStartDate,
	DateOnly NewEndDate,
	int ShiftCalendarDays,
	string Reason);

public sealed record ProjectReconcileResultDto(
	Guid ProjectId,
	DateOnly ProjectStartDate,
	DateOnly ProjectEndDate,
	IReadOnlyList<ProjectReconcileChangeDto> Changes,
	IReadOnlyList<AnalysisMessageDto> Problems,
	IReadOnlyList<AnalysisMessageDto> RemainingProblems);
