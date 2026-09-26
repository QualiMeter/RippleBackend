using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProjectManagement.Api.Contracts;
using ProjectManagement.Api.Data;

namespace ProjectManagement.Api.Controllers;

[ApiController]
[Route("api/v1/users")]
public sealed class UsersController(AppDbContext db) : ControllerBase
{
	[HttpGet]
	public async Task<ActionResult<IReadOnlyList<UserDto>>> GetAll(CancellationToken ct)
	{
		var users = await db.Users.AsNoTracking().OrderBy(x => x.Name).Select(x => new UserDto(x.Id, x.Name, x.Email)).ToListAsync(ct);
		return Ok(users);
	}

	[HttpGet("{id:guid}")]
	public async Task<ActionResult<UserDto>> Get(Guid id, CancellationToken ct)
	{
		var user = await db.Users.AsNoTracking().Where(x => x.Id == id).Select(x => new UserDto(x.Id, x.Name, x.Email)).SingleOrDefaultAsync(ct);
		return user is null ? NotFound() : Ok(user);
	}
}