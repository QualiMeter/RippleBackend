namespace ProjectManagement.Api.Contracts;

public sealed record UserDto(Guid Id, string Name, string? Email);
