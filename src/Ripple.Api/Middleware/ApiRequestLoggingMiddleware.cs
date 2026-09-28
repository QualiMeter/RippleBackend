using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Ripple.Api.Middleware;

public sealed class ApiRequestLoggingMiddleware(RequestDelegate next, ILogger<ApiRequestLoggingMiddleware> logger)
{
	private const int MaxBodyLength = 64 * 1024;

	public async Task InvokeAsync(HttpContext context)
	{
		if (!context.Request.Path.StartsWithSegments("/api"))
		{
			await next(context);
			return;
		}

		var stopwatch = Stopwatch.StartNew();
		var requestBody = await ReadRequestBodyAsync(context.Request);
		var originalResponseBody = context.Response.Body;
		await using var responseBuffer = new MemoryStream();
		context.Response.Body = responseBuffer;

		Exception? exception = null;
		try
		{
			await next(context);
		}
		catch (Exception ex)
		{
			exception = ex;
			throw;
		}
		finally
		{
			stopwatch.Stop();
			responseBuffer.Position = 0;
			var responseBody = await ReadResponseBodyAsync(responseBuffer);
			responseBuffer.Position = 0;
			await responseBuffer.CopyToAsync(originalResponseBody);
			context.Response.Body = originalResponseBody;

			var entry = new
			{
				method = context.Request.Method,
				path = context.Request.Path.Value,
				query = context.Request.QueryString.Value,
				status = context.Response.StatusCode,
				durationMs = stopwatch.Elapsed.TotalMilliseconds,
				requestHeaders = context.Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString()),
				requestBody,
				responseHeaders = context.Response.Headers.ToDictionary(x => x.Key, x => x.Value.ToString()),
				responseBody,
				error = exception?.ToString()
			};

			logger.LogInformation("RIPPLE_API_REQUEST {Request}", JsonSerializer.Serialize(entry));
		}
	}

	private static async Task<string?> ReadRequestBodyAsync(HttpRequest request)
	{
		if (request.ContentLength is 0 || !request.Body.CanRead)
			return null;
		request.EnableBuffering();
		request.Body.Position = 0;
		using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
		var body = await reader.ReadToEndAsync();
		request.Body.Position = 0;
		return Truncate(body);
	}

	private static async Task<string?> ReadResponseBodyAsync(Stream stream)
	{
		if (stream.Length == 0)
			return null;
		stream.Position = 0;
		using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
		return Truncate(await reader.ReadToEndAsync());
	}

	private static string? Truncate(string? value) => value is null || value.Length <= MaxBodyLength ? value : value[..MaxBodyLength] + "… [truncated]";
}
