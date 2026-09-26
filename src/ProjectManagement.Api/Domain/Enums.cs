namespace ProjectManagement.Api.Domain;

public enum TaskStatus
{
    NotStarted = 0,
    InProgress = 1,
    Completed = 2,
    Delayed = 3
}

public enum AnalysisSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}
