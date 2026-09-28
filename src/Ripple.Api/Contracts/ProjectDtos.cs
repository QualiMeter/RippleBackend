namespace Ripple.Api.Contracts;

public sealed record CreateProjectRequest(string Name, DateOnly StartDate, DateOnly EndDate);
public sealed record UpdateProjectRequest(string Name, DateOnly StartDate, DateOnly EndDate);

public sealed record ProjectListItemDto(Guid Id, string Name, DateOnly StartDate, DateOnly EndDate, Guid CreatorId, int TaskCount, int EmployeeCount);

public sealed record ProjectDetailsDto(
	Guid Id,
	string Name,
	DateOnly StartDate,
	DateOnly EndDate,
	Guid CreatorId,
	IReadOnlyList<EmployeeDto> Employees,
	IReadOnlyList<TaskListItemDto> Tasks,
	IReadOnlyList<DependencyDto> Dependencies,
	IReadOnlyList<AnalysisMessageDto> BoundaryWarnings);
