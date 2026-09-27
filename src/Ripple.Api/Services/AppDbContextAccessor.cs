using Ripple.Api.Data;

namespace Ripple.Api.Services;

public sealed class AppDbContextAccessor(AppDbContext db)
{
	public AppDbContext Db => db;
}
