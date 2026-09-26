namespace ProjectManagement.Api.Domain;

public sealed class User
{
	public Guid Id { get; set; }
	public string Name { get; set; } = null!;
	public string? Email { get; set; }
	public DateTimeOffset CreatedAt { get; set; }
	public ICollection<Project> Projects { get; set; } = new List<Project>();
}

public sealed class Project
{
	public Guid Id { get; set; }
	public string Name { get; set; } = null!;
	public DateOnly StartDate { get; set; }
	public DateOnly EndDate { get; set; }
	public Guid CreatorId { get; set; }
	public User Creator { get; set; } = null!;
	public ICollection<Employee> Employees { get; set; } = new List<Employee>();
	public ICollection<ProjectTask> Tasks { get; set; } = new List<ProjectTask>();
}

public sealed class Employee
{
	public Guid Id { get; set; }
	public Guid ProjectId { get; set; }
	public string Name { get; set; } = null!;
	public Project Project { get; set; } = null!;
	public ICollection<ProjectTask> Tasks { get; set; } = new List<ProjectTask>();
}

public sealed class ProjectTask
{
	public Guid Id { get; set; }
	public Guid ProjectId { get; set; }
	public Guid AssigneeId { get; set; }
	public string Name { get; set; } = null!;
	public DateOnly StartDate { get; set; }
	public DateOnly EndDate { get; set; }
	public TaskStatus Status { get; set; }
	public Project Project { get; set; } = null!;
	public Employee Assignee { get; set; } = null!;
	public ICollection<TaskDependency> PredecessorLinks { get; set; } = new List<TaskDependency>();
	public ICollection<TaskDependency> SuccessorLinks { get; set; } = new List<TaskDependency>();
}

public sealed class TaskDependency
{
	public Guid PredecessorTaskId { get; set; }
	public Guid SuccessorTaskId { get; set; }
	public ProjectTask PredecessorTask { get; set; } = null!;
	public ProjectTask SuccessorTask { get; set; } = null!;
	public DateTimeOffset CreatedAt { get; set; }
}