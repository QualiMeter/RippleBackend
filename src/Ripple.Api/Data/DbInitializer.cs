using Microsoft.EntityFrameworkCore;
using Ripple.Api.Domain;

namespace Ripple.Api.Data;

public static class DbInitializer
{
	public static async Task InitializeAsync(AppDbContext db, CancellationToken cancellationToken = default)
	{
		await db.Database.EnsureCreatedAsync(cancellationToken);

		await db.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS change_operations (
	"Id" uuid PRIMARY KEY,
	project_id uuid NULL REFERENCES projects("Id") ON DELETE SET NULL,
	operation_type varchar(64) NOT NULL,
	description varchar(500) NOT NULL,
	created_at timestamptz NOT NULL,
	undone_at timestamptz NULL
);
ALTER TABLE change_operations ALTER COLUMN project_id DROP NOT NULL;
ALTER TABLE change_operations DROP CONSTRAINT IF EXISTS change_operations_project_id_fkey;
ALTER TABLE change_operations ADD CONSTRAINT change_operations_project_id_fkey FOREIGN KEY (project_id) REFERENCES projects("Id") ON DELETE SET NULL;
CREATE INDEX IF NOT EXISTS ix_change_operations_project_created ON change_operations(project_id, created_at);
CREATE TABLE IF NOT EXISTS change_items (
	"Id" uuid PRIMARY KEY,
	operation_id uuid NOT NULL REFERENCES change_operations("Id") ON DELETE CASCADE,
	entity_type varchar(64) NOT NULL,
	entity_id uuid NOT NULL,
	before_json text NULL,
	after_json text NULL
);
CREATE INDEX IF NOT EXISTS ix_change_items_operation ON change_items(operation_id);
""", cancellationToken);

		await db.Database.ExecuteSqlRawAsync("ALTER TABLE employees ADD COLUMN IF NOT EXISTS phone varchar(40)", cancellationToken);
		await db.Database.ExecuteSqlRawAsync("ALTER TABLE employees ADD COLUMN IF NOT EXISTS email varchar(254)", cancellationToken);

		var demoUser = await db.Users.SingleOrDefaultAsync(x => x.Email == "demo.manager@example.local", cancellationToken);
		if (demoUser is null)
		{
			demoUser = new User
			{
				Id = Guid.NewGuid(),
				Name = "Demo Manager",
				Email = "demo.manager@example.local",
				CreatedAt = DateTimeOffset.UtcNow
			};
			db.Users.Add(demoUser);
			await db.SaveChangesAsync(cancellationToken);
		}

		var hasDemoProject = await db.Projects.AnyAsync(x => x.CreatorId == demoUser.Id, cancellationToken);
		if (hasDemoProject)
		{
			return;
		}

		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Уфа - MVP управления проектом",
			StartDate = new DateOnly(2026, 9, 1),
			EndDate = new DateOnly(2026, 10, 5),
			CreatorId = demoUser.Id
		};

		var ana = new Employee { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "Анна Петрова", Phone = "+7 900 000-00-01", Email = "anna@example.local", Project = project };
		var boris = new Employee { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "Борис Смирнов", Phone = "+7 900 000-00-02", Email = "boris@example.local", Project = project };
		var nina = new Employee { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "Нина Волкова", Phone = "+7 900 000-00-03", Email = "nina@example.local", Project = project };
		var oleg = new Employee { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "Олег Иванов", Phone = "+7 900 000-00-04", Email = "oleg@example.local", Project = project };

		project.Employees.Add(ana);
		project.Employees.Add(boris);
		project.Employees.Add(nina);
		project.Employees.Add(oleg);

		ProjectTask T(string name, DateOnly start, DateOnly end, ProjectTaskStatus status, Guid assigneeId) => new()
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Name = name,
			StartDate = start,
			EndDate = end,
			Status = status,
			AssigneeId = assigneeId,
			Project = project
		};

		var t1 = T("Анализ требований", new(2026, 9, 1), new(2026, 9, 3), ProjectTaskStatus.Completed, ana.Id);
		var t2 = T("Архитектура", new(2026, 9, 4), new(2026, 9, 6), ProjectTaskStatus.Completed, boris.Id);
		var t3 = T("Дизайн интерфейса", new(2026, 9, 7), new(2026, 9, 10), ProjectTaskStatus.InProgress, nina.Id);
		var t4 = T("Backend", new(2026, 9, 7), new(2026, 9, 15), ProjectTaskStatus.InProgress, boris.Id);
		var t5 = T("Frontend", new(2026, 9, 11), new(2026, 9, 18), ProjectTaskStatus.NotStarted, nina.Id);
		var t6 = T("Интеграция", new(2026, 9, 19), new(2026, 9, 22), ProjectTaskStatus.NotStarted, oleg.Id);
		var t7 = T("Тестирование", new(2026, 9, 23), new(2026, 9, 28), ProjectTaskStatus.NotStarted, ana.Id);
		var t8 = T("Деплой", new(2026, 9, 29), new(2026, 10, 2), ProjectTaskStatus.NotStarted, oleg.Id);

		foreach (var task in new[] { t1, t2, t3, t4, t5, t6, t7, t8 })
		{
			project.Tasks.Add(task);
		}

		TaskDependency D(ProjectTask predecessor, ProjectTask successor) => new()
		{
			PredecessorTaskId = predecessor.Id,
			SuccessorTaskId = successor.Id,
			PredecessorTask = predecessor,
			SuccessorTask = successor,
			CreatedAt = DateTimeOffset.UtcNow
		};

		db.Projects.Add(project);
		db.Employees.AddRange(ana, boris, nina, oleg);
		db.Tasks.AddRange(t1, t2, t3, t4, t5, t6, t7, t8);
		db.TaskDependencies.AddRange(
			D(t1, t2),
			D(t2, t3),
			D(t2, t4),
			D(t3, t5),
			D(t4, t6),
			D(t5, t6),
			D(t6, t7),
			D(t7, t8));

		await db.SaveChangesAsync(cancellationToken);
	}
}
