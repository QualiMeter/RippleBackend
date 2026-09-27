# Ripple

MVP backend for the project-management case: projects, employees, tasks, task dependencies, impact analysis and a preview/confirmation flow for shifting downstream tasks.

## Stack

- .NET 10 / ASP.NET Core Web API
- Entity Framework Core 10
- PostgreSQL via Npgsql
- Scalar API reference + OpenAPI
- Brotli/Gzip response compression + compact UTF-8 JSON
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

API: `https://localhost:7080`
Scalar: `https://localhost:7080/scalar`
OpenAPI JSON: `https://localhost:7080/openapi/v1.json`

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
DELETE /api/v1/projects/{id}

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
- DELETE endpoints physically delete entities; they do not convert them to the `Completed` task status.
- Deleting a project cascades to its employees, tasks and task dependencies.
- Deleting a task cascades to its dependency rows after the required confirmation for tasks with successors.
- Completed tasks are never shifted automatically; they are returned as manual-resolution items.
- Changing the project end date is separate from task shifts and can be explicitly confirmed.
- Weekends/holidays/work calendars are ignored; shifts use calendar days.

## Notes for production hardening

This is intentionally an MVP. Authentication/authorization, audit history, soft deletion, optimistic concurrency and migrations can be added later without changing the core domain model.

## Faster transport

API responses use compact UTF-8 JSON and automatic Brotli/Gzip compression when the client sends `Accept-Encoding`. This is transparent to the frontend. Large collection queries also use direct projections instead of loading full EF entity graphs, which reduces database work and allocations.

## Deletion semantics

`DELETE` is a real deletion operation:

```text
DELETE /api/v1/projects/{id}
DELETE /api/v1/projects/{projectId}/tasks/{taskId}
DELETE /api/v1/projects/{projectId}/employees/{employeeId}
DELETE /api/v1/projects/{projectId}/dependencies/{predecessorId}/{successorId}
```

For a task that has successors, the first DELETE returns `409 confirmation_required`; repeat it with `?confirm=true`. The task is then physically removed, and PostgreSQL cascade rules remove its dependency rows.

> Для локального HTTPS используйте dev-сертификат ASP.NET Core: `dotnet dev-certs https --trust`.


## Undo / история изменений

Для каждого изменения проекта создаётся атомарная операция истории. История хранится в PostgreSQL и сохраняется после перезапуска API.

- `GET /api/v1/projects/{projectId}/history` — последние изменения проекта.
- `POST /api/v1/projects/{projectId}/history/undo` — отменить последнее ещё не отменённое изменение.

Поддерживаются создание/изменение/удаление задач, создание/изменение/удаление сотрудников, добавление/удаление связей, изменение проекта и подтверждённый автоматический сдвиг задач.

Удаление задачи восстанавливает также все её зависимости. Одно подтверждённое действие с несколькими затронутыми задачами хранится как одна операция Undo.


## Realtime / SignalR

The API exposes SignalR at `/hubs/projects`. REST remains the source of truth for CRUD and initial loading; SignalR is used for incremental UI updates.

Client flow:

1. Connect to `/hubs/projects`.
2. Invoke `JoinProject(projectId)`.
3. Subscribe to `projectChanged`.
4. Update only the affected entity in the local state using `entity`, `action`, `entityId` and `data`.

Example event:

```json
{
  "eventId": "...",
  "projectId": "...",
  "entity": "task",
  "action": "updated",
  "entityId": "...",
  "data": { "id": "...", "name": "..." },
  "occurredAt": "..."
}
```

Supported entity events include `project`, `task`, `employee`, `task_dependency` and `history`. Actions include `created`, `updated`, `deleted`, `restored`, `undone` and `refresh`.
