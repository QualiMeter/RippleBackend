using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ripple.Api.Contracts;
using Ripple.Api.Data;
using Ripple.Api.Domain;
using Ripple.Api.Services;

namespace Ripple.Api.Controllers;

[ApiController]
[Route("api/v1/projects/{projectId:guid}/employees")]
public sealed class EmployeesController(AppDbContext db, ICurrentUserAccessor currentUser, ChangeHistoryService history, IRealtimeNotifier realtime) : ControllerBase
{
	[HttpGet]
	public async Task<ActionResult<IReadOnlyList<EmployeeDto>>> GetAll(Guid projectId, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		var result = await db.Employees.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Name).Select(x => new EmployeeDto(x.Id, x.ProjectId, x.Name, x.Phone, x.Email, x.Tasks.Count)).ToListAsync(ct);
		return Ok(result);
	}

	[HttpPost]
	public async Task<ActionResult<EmployeeDto>> Create(Guid projectId, CreateEmployeeRequest request, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		if (string.IsNullOrWhiteSpace(request.Name)) return ValidationProblem("Employee name is required.");
		var employee = new Employee
		{
			Id = Guid.NewGuid(),
			ProjectId = projectId,
			Name = request.Name.Trim(),
			Phone = NormalizeOptional(request.Phone),
			Email = NormalizeOptional(request.Email)
		};
		var operation = history.Begin(projectId, "employee.create", $"Добавлен сотрудник: {employee.Name}");
		history.Add(operation, "employee", employee.Id, null, ChangeHistoryService.Snapshot(employee));
		db.Employees.Add(employee);
		db.ChangeOperations.Add(operation);
		await db.SaveChangesAsync(ct);
		await realtime.PublishAsync(projectId, "employee", "created", employee.Id, new EmployeeDto(employee.Id, employee.ProjectId, employee.Name, employee.Phone, employee.Email, 0), ct);
		return Created($"/api/v1/projects/{projectId}/employees/{employee.Id}", new EmployeeDto(employee.Id, employee.ProjectId, employee.Name, employee.Phone, employee.Email, 0));
	}

	[HttpGet("{employeeId:guid}")]
	public async Task<ActionResult<EmployeeDetailsDto>> Get(Guid projectId, Guid employeeId, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		var employee = await db.Employees.AsNoTracking().Include(x => x.Tasks).SingleOrDefaultAsync(x => x.Id == employeeId && x.ProjectId == projectId, ct);
		if (employee is null) return NotFound();
		return Ok(new EmployeeDetailsDto(employee.Id, employee.ProjectId, employee.Name, employee.Phone, employee.Email, employee.Tasks.OrderBy(x => x.StartDate).Select(x => new AssignedTaskDto(x.Id, x.Name, x.StartDate, x.EndDate, x.Status.ToString())).ToList()));
	}

	[HttpPut("{employeeId:guid}")]
	public async Task<ActionResult<EmployeeDto>> Update(Guid projectId, Guid employeeId, UpdateEmployeeRequest request, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		if (string.IsNullOrWhiteSpace(request.Name)) return ValidationProblem("Employee name is required.");
		var employee = await db.Employees.SingleOrDefaultAsync(x => x.Id == employeeId && x.ProjectId == projectId, ct);
		if (employee is null) return NotFound();
		var before = ChangeHistoryService.Snapshot(employee);
		employee.Name = request.Name.Trim();
		employee.Phone = NormalizeOptional(request.Phone);
		employee.Email = NormalizeOptional(request.Email);
		var operation = history.Begin(projectId, "employee.update", $"Изменён сотрудник: {employee.Name}");
		history.Add(operation, "employee", employee.Id, before, ChangeHistoryService.Snapshot(employee));
		db.ChangeOperations.Add(operation);
		await db.SaveChangesAsync(ct);
		var count = await db.Tasks.CountAsync(x => x.AssigneeId == employeeId, ct);
		await realtime.PublishAsync(projectId, "employee", "updated", employee.Id, new EmployeeDto(employee.Id, employee.ProjectId, employee.Name, employee.Phone, employee.Email, count), ct);
		return Ok(new EmployeeDto(employee.Id, employee.ProjectId, employee.Name, employee.Phone, employee.Email, count));
	}

	[HttpDelete("{employeeId:guid}")]
	public async Task<IActionResult> Delete(Guid projectId, Guid employeeId, CancellationToken ct)
	{
		await EnsureProjectAsync(projectId, ct);
		var employee = await db.Employees.SingleOrDefaultAsync(x => x.Id == employeeId && x.ProjectId == projectId, ct);
		if (employee is null) return NotFound();
		if (await db.Tasks.AnyAsync(x => x.AssigneeId == employeeId, ct)) return Conflict(new ApiErrorDto("employee_in_use", "Employee cannot be deleted while assigned to tasks."));
		var operation = history.Begin(projectId, "employee.delete", $"Удалён сотрудник: {employee.Name}");
		history.Add(operation, "employee", employee.Id, ChangeHistoryService.Snapshot(employee), null);
		db.Employees.Remove(employee);
		db.ChangeOperations.Add(operation);
		await db.SaveChangesAsync(ct);
		await realtime.PublishAsync(projectId, "employee", "deleted", employeeId, null, ct);
		return NoContent();
	}

	private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

	private async Task EnsureProjectAsync(Guid projectId, CancellationToken ct)
	{
		var userId = await currentUser.GetUserIdAsync(ct);
		if (!await db.Projects.AnyAsync(x => x.Id == projectId && x.CreatorId == userId, ct)) throw new KeyNotFoundException("Project not found.");
	}
}
