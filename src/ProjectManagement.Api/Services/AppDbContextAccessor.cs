using ProjectManagement.Api.Data;

namespace ProjectManagement.Api.Services;

public sealed class AppDbContextAccessor(AppDbContext db)
{
	public AppDbContext Db => db;
}
