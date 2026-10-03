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
			"http://localhost:4137",
			"https://localhost:4137",
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
AI planning hub: /hubs/ai-planning
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

AI planning SignalR contract:
- Hub: /hubs/ai-planning
- Client invokes CreateProjectPlan(prompt) or CreateProjectUpdatePlan(projectId, prompt).
- Server sends aiPlanProgress events while Ollama is generating and validating.
- The invocation result is the final AiPlanDto and always contains planId and changes.
- aiPlanCompleted is also emitted with the same final AiPlanDto.

Local AI planning:
- POST /api/v1/ai/projects/plan: generate a preview for creating a project from natural language.
- POST /api/v1/projects/{projectId}/ai/plan: generate a preview for editing an existing project.
- GET /api/v1/ai/plans/{planId}: read a stored preview.
- POST /api/v1/ai/plans/{planId}/confirm: confirm a new-project plan.
- POST /api/v1/projects/{projectId}/ai/plan/{planId}/confirm: confirm an existing-project plan.
AI uses a local Ollama model and never writes to the database before confirmation. Confirmed AI changes are recorded in the normal project history.
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
	builder.Services.AddScoped<ProjectImportExportService>();
builder.Services.AddScoped<AppDbContextAccessor>();
builder.Services.AddScoped<IRealtimeNotifier, RealtimeNotifier>();
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));
builder.Services.AddHttpClient<IOllamaClient, OllamaClient>((serviceProvider, client) =>
{
	var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<OllamaOptions>>().Value;
	client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
	client.Timeout = TimeSpan.FromSeconds(Math.Max(10, options.TimeoutSeconds));
});
builder.Services.AddScoped<AiPlanningService>();

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
app.MapHub<AiPlanningHub>("/hubs/ai-planning");

await using (var scope = app.Services.CreateAsyncScope())
{
	var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
	await DbInitializer.InitializeAsync(db);
}

app.Run();
