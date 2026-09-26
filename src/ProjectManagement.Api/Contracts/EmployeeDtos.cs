namespace ProjectManagement.Api.Contracts;

public sealed record CreateEmployeeRequest(string Name);
public sealed record UpdateEmployeeRequest(string Name);
public sealed record EmployeeDto(Guid Id, Guid ProjectId, string Name, int TaskCount);
public sealed record EmployeeDetailsDto(Guid Id, Guid ProjectId, string Name, IReadOnlyList<AssignedTaskDto> Tasks);
public sealed record AssignedTaskDto(Guid Id, string Name, DateOnly StartDate, DateOnly EndDate, string Status);
