namespace Ripple.Api.Contracts;

public sealed record ProjectExportDto(
	int FormatVersion,
	DateTimeOffset ExportedAt,
	ProjectExportProjectDto Project,
	IReadOnlyList<ProjectExportEmployeeDto> Employees,
	IReadOnlyList<ProjectExportTaskDto> Tasks,
	IReadOnlyList<ProjectExportDependencyDto> Dependencies);

public sealed record ProjectExportProjectDto(
	Guid Id,
	string Name,
	DateOnly StartDate,
	DateOnly EndDate);

public sealed record ProjectExportEmployeeDto(
	Guid Id,
	string Name,
	string? Phone,
	string? Email);

public sealed record ProjectExportTaskDto(
	Guid Id,
	string Name,
	DateOnly StartDate,
	DateOnly EndDate,
	Guid AssigneeId,
	ProjectTaskStatus Status);

public sealed record ProjectExportDependencyDto(
	Guid PredecessorTaskId,
	Guid SuccessorTaskId,
	DateTimeOffset CreatedAt);

public sealed record ProjectImportResponse(
	Guid ProjectId,
	string Name,
	int EmployeeCount,
	int TaskCount,
	int DependencyCount);
