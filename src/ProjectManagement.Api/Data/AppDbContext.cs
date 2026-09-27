using Microsoft.EntityFrameworkCore;
using ProjectManagement.Api.Domain;

namespace ProjectManagement.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
	public DbSet<User> Users => Set<User>();
	public DbSet<Project> Projects => Set<Project>();
	public DbSet<Employee> Employees => Set<Employee>();
	public DbSet<ProjectTask> Tasks => Set<ProjectTask>();
	public DbSet<TaskDependency> TaskDependencies => Set<TaskDependency>();

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		modelBuilder.Entity<User>(b =>
		{
			b.ToTable("users");
			b.HasKey(x => x.Id);
			b.Property(x => x.Name).HasMaxLength(200).IsRequired();
			b.Property(x => x.Email).HasMaxLength(320);
			b.Property(x => x.CreatedAt).IsRequired();
			b.HasIndex(x => x.Email).IsUnique();
		});

		modelBuilder.Entity<Project>(b =>
		{
			b.ToTable("projects", table => table.HasCheckConstraint("ck_projects_dates", "start_date <= end_date"));
			b.HasKey(x => x.Id);
			b.Property(x => x.Name).HasMaxLength(250).IsRequired();
			b.Property(x => x.StartDate).HasColumnName("start_date").IsRequired();
			b.Property(x => x.EndDate).HasColumnName("end_date").IsRequired();
			b.Property(x => x.CreatorId).HasColumnName("creator_id").IsRequired();
			b.HasOne(x => x.Creator).WithMany(x => x.Projects).HasForeignKey(x => x.CreatorId).OnDelete(DeleteBehavior.Restrict);
			b.HasIndex(x => x.CreatorId);
		});

		modelBuilder.Entity<Employee>(b =>
		{
			b.ToTable("employees");
			b.HasKey(x => x.Id);
			b.Property(x => x.Name).HasMaxLength(200).IsRequired();
			b.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
			b.HasOne(x => x.Project).WithMany(x => x.Employees).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
			b.HasIndex(x => new { x.ProjectId, x.Name });
		});

		modelBuilder.Entity<ProjectTask>(b =>
		{
			b.ToTable("tasks", table => table.HasCheckConstraint("ck_tasks_dates", "start_date <= end_date"));
			b.HasKey(x => x.Id);
			b.Property(x => x.Name).HasMaxLength(300).IsRequired();
			b.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
			b.Property(x => x.AssigneeId).HasColumnName("assignee_id").IsRequired();
			b.Property(x => x.StartDate).HasColumnName("start_date").IsRequired();
			b.Property(x => x.EndDate).HasColumnName("end_date").IsRequired();
			b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
			b.HasOne(x => x.Project).WithMany(x => x.Tasks).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
			b.HasOne(x => x.Assignee).WithMany(x => x.Tasks).HasForeignKey(x => x.AssigneeId).OnDelete(DeleteBehavior.Restrict);
			b.HasIndex(x => x.ProjectId);
			b.HasIndex(x => x.AssigneeId);
		});

		modelBuilder.Entity<TaskDependency>(b =>
		{
			b.ToTable("task_dependencies");
			b.HasKey(x => new { x.PredecessorTaskId, x.SuccessorTaskId });
			b.Property(x => x.PredecessorTaskId).HasColumnName("predecessor_task_id");
			b.Property(x => x.SuccessorTaskId).HasColumnName("successor_task_id");
			b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
			b.HasOne(x => x.PredecessorTask).WithMany(x => x.SuccessorLinks).HasForeignKey(x => x.PredecessorTaskId).OnDelete(DeleteBehavior.Cascade);
			b.HasOne(x => x.SuccessorTask).WithMany(x => x.PredecessorLinks).HasForeignKey(x => x.SuccessorTaskId).OnDelete(DeleteBehavior.Cascade);
			b.HasIndex(x => x.SuccessorTaskId);
		});
	}
}
