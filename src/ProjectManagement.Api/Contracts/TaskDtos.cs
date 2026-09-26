namespace ProjectManagement.Api.Contracts;

public sealed record CreateTaskRequest(string Name, DateOnly StartDate, DateOnly EndDate, Guid AssigneeId, string Status = "NotStarted");
public sealed record UpdateTaskRequest(string Name, DateOnly StartDate, DateOnly EndDate, Guid AssigneeId, string Status);

public sealed record TaskListItemDto(
	Guid Id,
	Guid ProjectId,
	string Name,
	DateOnly StartDate,
	DateOnly EndDate,
	int DurationCalendarDays,
	Guid AssigneeId,
	string AssigneeName,
	string Status);

public sealed record TaskDetailsDto(
	Guid Id,
	Guid ProjectId,
	string Name,
	DateOnly StartDate,
	DateOnly EndDate,
	int DurationCalendarDays,
	Guid AssigneeId,
	string AssigneeName,
	string Status,
	IReadOnlyList<Guid> PredecessorIds,
	IReadOnlyList<Guid> SuccessorIds);

public sealed record TaskMutationResponse(TaskDetailsDto Task, IReadOnlyList<AnalysisMessageDto> Analysis);

public sealed record ShiftPreviewItemDto(
	Guid TaskId,
	string TaskName,
	DateOnly OriginalStartDate,
	DateOnly OriginalEndDate,
	DateOnly ProposedStartDate,
	DateOnly ProposedEndDate,
	int ShiftCalendarDays,
	bool CompletedRequiresManualResolution,
	string? Reason);

public sealed record ShiftPreviewDto(
	Guid RootTaskId,
	IReadOnlyList<ShiftPreviewItemDto> Items,
	DateOnly CurrentProjectEndDate,
	DateOnly ProposedProjectEndDate,
	int ProjectEndIncreaseCalendarDays,
	IReadOnlyList<AnalysisMessageDto> Analysis);

public sealed record ConfirmShiftRequest(bool ConfirmProjectEndDate);
public sealed record ShiftConfirmationResponse(ShiftPreviewDto Preview, bool ProjectEndDateChanged);
