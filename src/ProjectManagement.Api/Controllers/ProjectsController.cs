using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProjectManagement.Api.Contracts;
using ProjectManagement.Api.Data;
using ProjectManagement.Api.Domain;
using ProjectManagement.Api.Services;

namespace ProjectManagement.Api.Controllers;

[ApiController]
[Route("api/v1/projects")]
public sealed class ProjectsController(AppDbContext db, ICurrentUserAccessor currentUser, AnalysisService analysis) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectListItemDto>>> GetAll(CancellationToken ct)
    {
        var userId = await currentUser.GetUserIdAsync(ct);
        var projects = await db.Projects
            .AsNoTracking()
            .Where(x => x.CreatorId == userId)
            .OrderBy(x => x.StartDate)
            .Select(x => new ProjectListItemDto(
                x.Id,
                x.Name,
                x.StartDate,
                x.EndDate,
                x.CreatorId,
                x.Tasks.Count,
                x.Employees.Count))
            .ToListAsync(ct);
        return Ok(projects);
    }

    [HttpPost]
    public async Task<ActionResult<ProjectDetailsDto>> Create(CreateProjectRequest request, CancellationToken ct)
    {
        ValidateProjectDates(request.StartDate, request.EndDate);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ValidationProblem("Project name is required.");
        }

        var userId = await currentUser.GetUserIdAsync(ct);
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            CreatorId = userId
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = project.Id }, await BuildDetailsAsync(project.Id, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProjectDetailsDto>> Get(Guid id, CancellationToken ct)
    {
        var userId = await currentUser.GetUserIdAsync(ct);
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.CreatorId == userId, ct);
        return project is null ? NotFound() : Ok(await BuildDetailsAsync(id, ct));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ProjectDetailsDto>> Update(Guid id, UpdateProjectRequest request, CancellationToken ct)
    {
        ValidateProjectDates(request.StartDate, request.EndDate);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ValidationProblem("Project name is required.");
        }

        var userId = await currentUser.GetUserIdAsync(ct);
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == id && x.CreatorId == userId, ct);
        if (project is null)
        {
            return NotFound();
        }

        project.Name = request.Name.Trim();
        project.StartDate = request.StartDate;
        project.EndDate = request.EndDate;
        await db.SaveChangesAsync(ct);
        return Ok(await BuildDetailsAsync(project.Id, ct));
    }

    private async Task<ProjectDetailsDto> BuildDetailsAsync(Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects
            .AsNoTracking()
            .Include(x => x.Employees).ThenInclude(e => e.Tasks)
            .Include(x => x.Tasks).ThenInclude(t => t.Assignee)
            .Include(x => x.Tasks).ThenInclude(t => t.PredecessorLinks)
            .Include(x => x.Tasks).ThenInclude(t => t.SuccessorLinks)
            .SingleAsync(x => x.Id == projectId, ct);

        var links = await db.TaskDependencies
            .AsNoTracking()
            .Where(x => x.PredecessorTask.ProjectId == projectId)
            .Include(x => x.PredecessorTask)
            .Include(x => x.SuccessorTask)
            .ToListAsync(ct);

        var employees = project.Employees.OrderBy(x => x.Name).Select(x => new EmployeeDto(x.Id, x.ProjectId, x.Name, x.Tasks.Count)).ToList();
        var tasks = project.Tasks.OrderBy(x => x.StartDate).Select(ProjectMapper.ToListDto).ToList();
        var dependencies = links.Select(x => new DependencyDto(x.PredecessorTaskId, x.SuccessorTaskId, projectId, x.PredecessorTask.Name, x.SuccessorTask.Name)).ToList();
        var warnings = await analysis.AnalyzeProjectBoundaryAsync(project, ct);

        return new ProjectDetailsDto(project.Id, project.Name, project.StartDate, project.EndDate, project.CreatorId, employees, tasks, dependencies, warnings);
    }

    private static void ValidateProjectDates(DateOnly start, DateOnly end)
    {
        if (start > end)
        {
            throw new ArgumentException("Project start date cannot be after project end date.");
        }
    }
}
