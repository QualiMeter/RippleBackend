namespace Ripple.Api.Contracts;

public sealed record ProjectDiagnosticsDto(
	DateTimeOffset GeneratedAt,
	ProjectDiagnosticsProjectDto Project,
	IReadOnlyList<ProjectDiagnosticsEmployeeDto> Employees,
	IReadOnlyList<ProjectDiagnosticsTaskDto> Tasks,
	IReadOnlyList<ProjectDiagnosticsDependencyDto> Dependencies,
	IReadOnlyList<AnalysisMessageDto> ProjectAnalysis,
	IReadOnlyDictionary<Guid, IReadOnlyList<AnalysisMessageDto>> TaskAnalysis,
	IReadOnlyList<ProjectDiagnosticsHistoryDto> History);

public sealed record ProjectDiagnosticsProjectDto(
	Guid Id,
	string Name,
	DateOnly StartDate,
	DateOnly EndDate,
	Guid CreatorId,
	int TaskCount,
	int EmployeeCount,
	int DependencyCount);

public sealed record ProjectDiagnosticsEmployeeDto(
	Guid Id,
	string Name,
	string? Phone,
	string? Email,
	IReadOnlyList<Guid> TaskIds);

public sealed record ProjectDiagnosticsTaskDto(
	Guid Id,
	string Name,
	DateOnly StartDate,
	DateOnly EndDate,
	int DurationCalendarDays,
	Guid AssigneeId,
	string AssigneeName,
	string Status,
	bool OutsideProjectBounds,
	bool Overdue,
	IReadOnlyList<Guid> PredecessorIds,
	IReadOnlyList<Guid> SuccessorIds);

public sealed record ProjectDiagnosticsDependencyDto(
	Guid PredecessorTaskId,
	string PredecessorTaskName,
	DateOnly PredecessorEndDate,
	Guid SuccessorTaskId,
	string SuccessorTaskName,
	DateOnly SuccessorStartDate,
	bool DateConflict);

public sealed record ProjectDiagnosticsHistoryDto(
	Guid Id,
	string OperationType,
	string Description,
	DateTimeOffset CreatedAt,
	DateTimeOffset? UndoneAt,
	bool CanUndo,
	IReadOnlyList<ChangeHistoryItemDto> Items);
