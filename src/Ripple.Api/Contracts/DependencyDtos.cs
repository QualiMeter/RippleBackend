namespace Ripple.Api.Contracts;

public sealed record CreateDependencyRequest(Guid PredecessorTaskId, Guid SuccessorTaskId);
public sealed record DependencyDto(Guid PredecessorTaskId, Guid SuccessorTaskId, Guid ProjectId, string PredecessorTaskName, string SuccessorTaskName);
public sealed record DependencyMutationResponse(DependencyDto Dependency, IReadOnlyList<AnalysisMessageDto> Analysis);
