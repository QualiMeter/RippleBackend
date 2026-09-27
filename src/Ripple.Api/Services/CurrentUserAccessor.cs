using Microsoft.EntityFrameworkCore;
using Ripple.Api.Data;

namespace Ripple.Api.Services;

public interface ICurrentUserAccessor
{
	Task<Guid> GetUserIdAsync(CancellationToken cancellationToken);
}

public sealed class CurrentUserAccessor(AppDbContext db, IHttpContextAccessor httpContextAccessor) : ICurrentUserAccessor
{
	public async Task<Guid> GetUserIdAsync(CancellationToken cancellationToken)
	{
		var header = httpContextAccessor.HttpContext?.Request.Headers["X-User-Id"].FirstOrDefault();
		if (!string.IsNullOrWhiteSpace(header) && Guid.TryParse(header, out var userId))
		{
			var exists = await db.Users.AnyAsync(x => x.Id == userId, cancellationToken);
			if (exists)
			{
				return userId;
			}
		}

		var demo = await db.Users.OrderBy(x => x.CreatedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
		if (demo is null)
		{
			throw new InvalidOperationException("No manager user exists.");
		}

		return demo.Value;
	}
}
