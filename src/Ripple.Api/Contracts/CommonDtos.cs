using Ripple.Api.Domain;

namespace Ripple.Api.Contracts;

public sealed record AnalysisActionDto(string Code, string Label, Guid? TargetTaskId = null);

public sealed record AnalysisMessageDto(
	AnalysisSeverity Severity,
	Guid TriggerTaskId,
	string TriggerTaskName,
	IReadOnlyList<Guid> AffectedTaskIds,
	IReadOnlyList<string> AffectedTaskNames,
	string Description,
	IReadOnlyList<AnalysisActionDto> Actions);

public sealed record ApiErrorDto(string Code, string Message, IReadOnlyDictionary<string, string[]>? Errors = null);
