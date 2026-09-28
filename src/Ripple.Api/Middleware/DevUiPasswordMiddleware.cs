using System.Security.Cryptography;
using System.Text;

namespace Ripple.Api.Middleware;

public sealed class DevUiPasswordMiddleware(RequestDelegate next, IConfiguration configuration)
{
	private const string CookieName = "Ripple.DevUi";
	private const string DefaultPassword = "q192837465";

	public async Task InvokeAsync(HttpContext context)
	{
		if (!IsDevUiRequest(context.Request.Path))
		{
			await next(context);
			return;
		}

		if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
			context.Request.Path.Equals("/dev-ui/login", StringComparison.OrdinalIgnoreCase))
		{
			await LoginAsync(context);
			return;
		}

		if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
			context.Request.Path.Equals("/dev-ui/logout", StringComparison.OrdinalIgnoreCase))
		{
			context.Response.Cookies.Delete(CookieName, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps });
			context.Response.Redirect("/");
			return;
		}

		if (!IsAuthenticated(context))
		{
			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			context.Response.ContentType = "text/html; charset=utf-8";
			context.Response.Headers.CacheControl = "no-store";
			await context.Response.WriteAsync(LoginPage());
			return;
		}

		context.Response.Headers.CacheControl = "no-store";
		await next(context);
	}

	private async Task LoginAsync(HttpContext context)
	{
		var form = await context.Request.ReadFormAsync(context.RequestAborted);
		var password = form["password"].FirstOrDefault() ?? string.Empty;
		var expected = configuration["DEV_UI_PASSWORD"] ?? DefaultPassword;

		if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(expected)))
		{
			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			context.Response.ContentType = "text/html; charset=utf-8";
			await context.Response.WriteAsync(LoginPage("Неверный пароль."));
			return;
		}

		context.Response.Cookies.Append(CookieName, CreateToken(expected), new CookieOptions
		{
			HttpOnly = true,
			SameSite = SameSiteMode.Strict,
			Secure = context.Request.IsHttps,
			IsEssential = true,
			MaxAge = TimeSpan.FromHours(12)
		});
		context.Response.Redirect("/");
	}

	private static string CreateToken(string password) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"Ripple.DevUi:{password}")));

	private bool IsAuthenticated(HttpContext context)
	{
		var expected = CreateToken(configuration["DEV_UI_PASSWORD"] ?? DefaultPassword);
		var actual = context.Request.Cookies[CookieName];
		if (string.IsNullOrEmpty(actual)) return false;
		try
		{
			return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(actual), Convert.FromBase64String(expected));
		}
		catch (FormatException)
		{
			return false;
		}
	}

	private static bool IsDevUiRequest(PathString path) =>
		path == "/" || path.StartsWithSegments("/ui.js") || path.StartsWithSegments("/ui.css") || path.StartsWithSegments("/dev-ui");

	private static string LoginPage(string? error = null) => $$"""
		<!doctype html>
		<html lang="ru">
		<head>
			<meta charset="utf-8">
			<meta name="viewport" content="width=device-width,initial-scale=1">
			<title>Ripple Dev UI — вход</title>
			<style>
				*{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;background:#101318;color:#f4f5f7;font:16px system-ui,sans-serif}form{width:min(420px,calc(100vw - 32px));padding:28px;border:1px solid #303640;border-radius:16px;background:#181c23;box-shadow:0 20px 60px #0008}h1{margin:0 0 8px}p{color:#9ca4b2}label{display:block;margin:20px 0 8px}input{width:100%;padding:12px;border-radius:10px;border:1px solid #3a414d;background:#0f1217;color:#fff;font-size:16px}button{width:100%;margin-top:18px;padding:12px;border:0;border-radius:10px;background:#fff;color:#111;font-weight:700;cursor:pointer}.error{margin-top:14px;color:#ff8d8d}
			</style>
		</head>
		<body>
			<form method="post" action="/dev-ui/login">
				<h1>Ripple Dev UI</h1>
				<p>Внутренний диагностический интерфейс.</p>
				<label for="password">Пароль</label>
				<input id="password" name="password" type="password" autocomplete="current-password" autofocus required>
				<button type="submit">Войти</button>
				{{(string.IsNullOrEmpty(error) ? "" : $"<div class=\"error\">{System.Net.WebUtility.HtmlEncode(error)}</div>")}}
			</form>
		</body>
		</html>
		""";
}
