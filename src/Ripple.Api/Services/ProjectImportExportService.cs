using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ripple.Api.Contracts;
using Ripple.Api.Data;
using Ripple.Api.Domain;

namespace Ripple.Api.Services;

public sealed class ProjectImportExportService(
	AppDbContext db,
	ICurrentUserAccessor currentUser,
	ChangeHistoryService history)
{
	private const int CurrentFormatVersion = 1;
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true
	};

	public async Task<ProjectExportDto?> ExportAsync(Guid projectId, CancellationToken ct)
	{
		var userId = await currentUser.GetUserIdAsync(ct);
		var project = await db.Projects.AsNoTracking()
			.SingleOrDefaultAsync(x => x.Id == projectId && x.CreatorId == userId, ct);
		if (project is null)
			return null;

		var employees = await db.Employees.AsNoTracking()
			.Where(x => x.ProjectId == projectId)
			.OrderBy(x => x.Name)
			.Select(x => new ProjectExportEmployeeDto(x.Id, x.Name, x.Phone, x.Email))
			.ToListAsync(ct);

		var tasks = await db.Tasks.AsNoTracking()
			.Where(x => x.ProjectId == projectId)
			.OrderBy(x => x.StartDate)
			.ThenBy(x => x.Name)
			.Select(x => new ProjectExportTaskDto(x.Id, x.Name, x.StartDate, x.EndDate, x.AssigneeId, x.Status))
			.ToListAsync(ct);

		var dependencies = await db.TaskDependencies.AsNoTracking()
			.Where(x => x.PredecessorTask.ProjectId == projectId)
			.OrderBy(x => x.CreatedAt)
			.Select(x => new ProjectExportDependencyDto(x.PredecessorTaskId, x.SuccessorTaskId, x.CreatedAt))
			.ToListAsync(ct);

		return new ProjectExportDto(
			CurrentFormatVersion,
			DateTimeOffset.UtcNow,
			new ProjectExportProjectDto(project.Id, project.Name, project.StartDate, project.EndDate),
			employees,
			tasks,
			dependencies);
	}

	public async Task<ProjectImportResponse> ImportAsync(ProjectExportDto document, CancellationToken ct)
	{
		Validate(document);
		var userId = await currentUser.GetUserIdAsync(ct);

		var strategy = db.Database.CreateExecutionStrategy();
		return await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await db.Database.BeginTransactionAsync(ct);

			var projectId = Guid.NewGuid();
			var employeeIds = document.Employees.ToDictionary(x => x.Id, _ => Guid.NewGuid());
			var taskIds = document.Tasks.ToDictionary(x => x.Id, _ => Guid.NewGuid());

			var project = new Project
			{
				Id = projectId,
				Name = document.Project.Name.Trim(),
				StartDate = document.Project.StartDate,
				EndDate = document.Project.EndDate,
				CreatorId = userId
			};

			var operation = history.Begin(projectId, "project.import", $"Импортирован проект: {project.Name}");
			history.Add(operation, "project", project.Id, null, ChangeHistoryService.Snapshot(project));

			db.Projects.Add(project);

			foreach (var sourceEmployee in document.Employees)
			{
				var employee = new Employee
				{
					Id = employeeIds[sourceEmployee.Id],
					ProjectId = projectId,
					Name = sourceEmployee.Name.Trim(),
					Phone = sourceEmployee.Phone,
					Email = sourceEmployee.Email
				};
				db.Employees.Add(employee);
				history.Add(operation, "employee", employee.Id, null, ChangeHistoryService.Snapshot(employee));
			}

			foreach (var sourceTask in document.Tasks)
			{
				var task = new ProjectTask
				{
					Id = taskIds[sourceTask.Id],
					ProjectId = projectId,
					AssigneeId = employeeIds[sourceTask.AssigneeId],
					Name = sourceTask.Name.Trim(),
					StartDate = sourceTask.StartDate,
					EndDate = sourceTask.EndDate,
					Status = sourceTask.Status
				};
				db.Tasks.Add(task);
				history.Add(operation, "task", task.Id, null, ChangeHistoryService.Snapshot(task));
			}

			foreach (var sourceDependency in document.Dependencies)
			{
				var dependency = new TaskDependency
				{
					PredecessorTaskId = taskIds[sourceDependency.PredecessorTaskId],
					SuccessorTaskId = taskIds[sourceDependency.SuccessorTaskId],
					CreatedAt = sourceDependency.CreatedAt
				};
				db.TaskDependencies.Add(dependency);
				history.Add(operation, "task_dependency", dependency.PredecessorTaskId, null, ChangeHistoryService.Snapshot(dependency));
			}

			db.ChangeOperations.Add(operation);
			await db.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);

			return new ProjectImportResponse(
			projectId,
			project.Name,
			document.Employees.Count,
			document.Tasks.Count,
			document.Dependencies.Count);
		});
	}

	public static byte[] Serialize(ProjectExportDto document) =>
		JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);

	public static ProjectExportDto Deserialize(Stream stream)
	{
		var document = JsonSerializer.Deserialize<ProjectExportDto>(stream, JsonOptions);
		return document ?? throw new InvalidDataException("Import file is empty or has an invalid JSON format.");
	}

	private static void Validate(ProjectExportDto document)
	{
		if (document.FormatVersion != CurrentFormatVersion)
			throw new InvalidDataException($"Unsupported project format version: {document.FormatVersion}.");

		if (string.IsNullOrWhiteSpace(document.Project.Name))
			throw new InvalidDataException("Project name is required.");
		if (document.Project.StartDate > document.Project.EndDate)
			throw new InvalidDataException("Project start date cannot be after project end date.");

		var employeeIds = document.Employees.Select(x => x.Id).ToList();
		if (employeeIds.Count != employeeIds.Distinct().Count())
			throw new InvalidDataException("Import contains duplicate employee IDs.");

		var taskIds = document.Tasks.Select(x => x.Id).ToList();
		if (taskIds.Count != taskIds.Distinct().Count())
			throw new InvalidDataException("Import contains duplicate task IDs.");

		var employees = document.Employees.ToDictionary(x => x.Id);
		foreach (var employee in document.Employees)
		{
			if (string.IsNullOrWhiteSpace(employee.Name))
				throw new InvalidDataException("Employee name is required.");
		}

		foreach (var task in document.Tasks)
		{
			if (string.IsNullOrWhiteSpace(task.Name))
				throw new InvalidDataException("Task name is required.");
			if (task.StartDate > task.EndDate)
				throw new InvalidDataException($"Task '{task.Name}' has an invalid date range.");
			if (!employees.ContainsKey(task.AssigneeId))
				throw new InvalidDataException($"Task '{task.Name}' references an unknown employee.");
		}

		var tasks = taskIds.ToHashSet();
		var dependencyKeys = new HashSet<(Guid Predecessor, Guid Successor)>();
		foreach (var dependency in document.Dependencies)
		{
			if (dependency.PredecessorTaskId == dependency.SuccessorTaskId)
				throw new InvalidDataException("A task cannot depend on itself.");
			if (!tasks.Contains(dependency.PredecessorTaskId) || !tasks.Contains(dependency.SuccessorTaskId))
				throw new InvalidDataException("Import contains a dependency referencing an unknown task.");
			if (!dependencyKeys.Add((dependency.PredecessorTaskId, dependency.SuccessorTaskId)))
				throw new InvalidDataException("Import contains duplicate dependencies.");
		}
	}
}
