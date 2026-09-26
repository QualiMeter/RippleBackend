# ProjectManagementMvp

MVP backend for the project-management case: projects, employees, tasks, task dependencies, impact analysis and a preview/confirmation flow for shifting downstream tasks.

## Stack

- .NET 10 / ASP.NET Core Web API
- Entity Framework Core 10
- PostgreSQL via Npgsql
- Scalar API reference + OpenAPI
- xUnit tests for core dependency/shift logic

The MVP intentionally has no registration/authentication flow. A seeded demo manager is used by default. You can override the current manager with the `X-User-Id` header.

## Database

Default local PostgreSQL connection:

```text
Host=localhost;Port=5432;Database=project_mvp;Username=postgres;Password=postgres
```

You can override it with `ConnectionStrings__Postgres`.

## Run PostgreSQL

```bash
docker compose up -d postgres
```

Or use an existing PostgreSQL 15+ instance and update the connection string.

## Run API

Requires .NET 10 SDK.

```bash
dotnet restore
dotnet run --project src/ProjectManagement.Api
```

API: `http://localhost:5080`
Scalar: `http://localhost:5080/scalar`
OpenAPI JSON: `http://localhost:5080/openapi/v1.json`

On startup the database schema is created with EF Core `EnsureCreated` and the demonstration data is seeded if it is missing.

## Demo data

The seed creates:

- 1 manager
- 1 project
- 4 employees
- 8 tasks
- several dependencies, including a converging dependency graph
- completed and unfinished tasks
- a chain where changing one task's dates affects downstream work

## API overview

```text
GET    /api/v1/users
GET    /api/v1/users/{id}

GET    /api/v1/projects
POST   /api/v1/projects
GET    /api/v1/projects/{id}
PUT    /api/v1/projects/{id}

GET    /api/v1/projects/{projectId}/employees
POST   /api/v1/projects/{projectId}/employees
GET    /api/v1/projects/{projectId}/employees/{employeeId}
PUT    /api/v1/projects/{projectId}/employees/{employeeId}
DELETE /api/v1/projects/{projectId}/employees/{employeeId}

GET    /api/v1/projects/{projectId}/tasks
POST   /api/v1/projects/{projectId}/tasks
GET    /api/v1/projects/{projectId}/tasks/{taskId}
PUT    /api/v1/projects/{projectId}/tasks/{taskId}
DELETE /api/v1/projects/{projectId}/tasks/{taskId}

GET    /api/v1/projects/{projectId}/dependencies
POST   /api/v1/projects/{projectId}/dependencies
DELETE /api/v1/projects/{projectId}/dependencies/{predecessorId}/{successorId}

GET    /api/v1/projects/{projectId}/tasks/{taskId}/analysis
POST   /api/v1/projects/{projectId}/tasks/{taskId}/shift-preview
POST   /api/v1/projects/{projectId}/tasks/{taskId}/shift-confirm
```

## Important MVP rules

- Task start date must not be after end date.
- Project start date must not be after end date.
- A task always has exactly one employee/assignee.
- Employees belong to one project.
- Dependencies are directional: predecessor -> successor.
- Same-task, duplicate and cyclic dependencies are rejected.
- Dependency date conflicts generate warnings, not hard errors.
- Analysis never changes task status automatically.
- Shift preview is calculated without saving.
- Shift confirmation recalculates the proposal on the server and then persists it.
- Completed tasks are never shifted automatically; they are returned as manual-resolution items.
- Changing the project end date is separate from task shifts and can be explicitly confirmed.
- Weekends/holidays/work calendars are ignored; shifts use calendar days.

## Notes for production hardening

This is intentionally an MVP. Authentication/authorization, audit history, soft deletion, optimistic concurrency and migrations can be added later without changing the core domain model.
