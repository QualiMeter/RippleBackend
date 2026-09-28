namespace Ripple.Api.Contracts;

public sealed record CreateEmployeeRequest(string Name, string? Phone, string? Email);
public sealed record UpdateEmployeeRequest(string Name, string? Phone, string? Email);
public sealed record EmployeeDto(Guid Id, Guid ProjectId, string Name, string? Phone, string? Email, int TaskCount);
public sealed record EmployeeDetailsDto(Guid Id, Guid ProjectId, string Name, string? Phone, string? Email, IReadOnlyList<AssignedTaskDto> Tasks);
public sealed record AssignedTaskDto(Guid Id, string Name, DateOnly StartDate, DateOnly EndDate, string Status);
