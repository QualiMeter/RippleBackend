using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ripple.Api.Contracts;

namespace Ripple.Api.Middleware;

public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
	public async Task InvokeAsync(HttpContext context)
	{
		try
		{
			await next(context);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Unhandled API exception");
			await HandleAsync(context, ex);
		}
	}

	private static async Task HandleAsync(HttpContext context, Exception exception)
	{
		var (status, code, message) = exception switch
		{
			KeyNotFoundException => ((int)HttpStatusCode.NotFound, "not_found", exception.Message),
			InvalidOperationException => ((int)HttpStatusCode.Conflict, "business_rule", exception.Message),
			DbUpdateException => ((int)HttpStatusCode.Conflict, "database_error", "Database operation failed."),
			ArgumentException => ((int)HttpStatusCode.BadRequest, "bad_request", exception.Message),
			_ => ((int)HttpStatusCode.InternalServerError, "server_error", "Unexpected server error.")
		};

		context.Response.StatusCode = status;
		context.Response.ContentType = "application/json";
		await context.Response.WriteAsync(JsonSerializer.Serialize(new ApiErrorDto(code, message)));
	}
}
