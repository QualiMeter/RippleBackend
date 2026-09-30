using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.HttpOverrides;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Ripple.Api.Data;
using Ripple.Api.Middleware;
using Ripple.Api.Hubs;
using Ripple.Api.Services;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();

builder.Services.AddCors(options =>
{
	var configuredOrigins = builder.Configuration["CORS_ORIGINS"]
		?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
		?? [];

	var origins = configuredOrigins
		.Concat([
			"https://ripple-azure-one.vercel.app",
			"http://localhost",
			"https://localhost",
			"http://localhost:3000",
			"https://localhost:3000",
			"http://localhost:5173",
			"https://localhost:5173",
			"http://localhost:8080",
			"https://localhost:8080",
			"http://92.63.102.15"
		])
		.Distinct(StringComparer.OrdinalIgnoreCase)
		.ToArray();

	options.AddPolicy("Frontend", policy =>
	{
		policy.WithOrigins(origins)
			.AllowAnyHeader()
			.AllowAnyMethod();
	});
});
builder.Services.AddSignalR();

builder.Services.AddControllers()
	.AddJsonOptions(options =>
	{
		options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
		options.JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
		options.JsonSerializerOptions.WriteIndented = false;
	});

builder.Services.AddResponseCompression(options =>
{
	options.EnableForHttps = true;
	options.Providers.Add<BrotliCompressionProvider>();
	options.Providers.Add<GzipCompressionProvider>();
	options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json", "application/problem+json"]);
});

builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);

builder.Services.AddOpenApi(options =>
{
	options.AddDocumentTransformer((document, _, _) =>
	{
		document.Info.Description = """
Project Management MVP REST API.

Realtime SignalR contract:
Hub: /hubs/projects
Transport: SignalR

Client methods:
- JoinProject(projectId: uuid): subscribes the connection to project:{projectId:N}.
- LeaveProject(projectId: uuid): removes the connection from project:{projectId:N}.

Server event:
- projectChanged

projectChanged payload:
{
  eventId: uuid,
  projectId: uuid,
  entity: project | task | employee | dependency | history,
  action: created | updated | deleted | restored | undone,
  entityId: uuid | null,
  data: object | null,
  occurredAt: date-time
}

REST remains the source of truth. SignalR delivers realtime deltas so the frontend can update individual entities without reloading the whole project.
""";
		return Task.CompletedTask;
	});
});

var connectionString = builder.Configuration.GetConnectionString("Postgres")
	?? Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
	?? throw new InvalidOperationException("Connection string 'Postgres' is not configured.");

builder.Services.AddDbContext<AppDbContext>(options =>
	options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure()));

builder.Services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();
builder.Services.AddScoped<AnalysisService>();
builder.Services.AddScoped<ProjectReconcileService>();
builder.Services.AddScoped<ProjectDiagnosticsService>();
builder.Services.AddScoped<DependencyGraphService>();
builder.Services.AddScoped<ShiftService>();

builder.Services.AddScoped<ChangeHistoryService>();
builder.Services.AddScoped<AppDbContextAccessor>();
builder.Services.AddScoped<IRealtimeNotifier, RealtimeNotifier>();

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
	ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});
app.UseResponseCompression();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors("Frontend");
app.MapOpenApi();
app.MapScalarApiReference("/scalar", options => options.WithTitle("Project Management MVP API").WithOpenApiRoutePattern("/openapi/{documentName}.json"));
app.MapControllers();
app.MapHub<ProjectHub>("/hubs/projects");

await using (var scope = app.Services.CreateAsyncScope())
{
	var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
	await DbInitializer.InitializeAsync(db);
}

app.Run();
