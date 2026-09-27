using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.HttpOverrides;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ProjectManagement.Api.Data;
using ProjectManagement.Api.Middleware;
using ProjectManagement.Api.Hubs;
using ProjectManagement.Api.Services;
using Scalar.AspNetCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();

builder.Services.AddCors(options =>
{
	var configuredOrigins = builder.Configuration["CORS_ORIGINS"]
		?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
		?? [];

	var origins = configuredOrigins.Length > 0
		? configuredOrigins
		: ["https://ripple-azure-one.vercel.app"];

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
- Hub: /hubs/projects
- Client methods: JoinProject(projectId), LeaveProject(projectId)
- Server event: projectChanged
- Event fields: eventId, projectId, entity, action, entityId, data, occurredAt
- Entities: project, task, employee, dependency, history
- Actions: created, updated, deleted, restored, undone
- JoinProject subscribes the connection to project:{projectId:N}.
- REST remains the source of truth; SignalR delivers realtime deltas.
""";

		document.Extensions["x-signalr"] = new OpenApiObject
		{
			["hub"] = new OpenApiString("/hubs/projects"),
			["transport"] = new OpenApiString("SignalR"),
			["clientMethods"] = new OpenApiArray
			{
				new OpenApiString("JoinProject(projectId)"),
				new OpenApiString("LeaveProject(projectId)")
			},
			["serverEvents"] = new OpenApiArray
			{
				new OpenApiObject
				{
					["name"] = new OpenApiString("projectChanged"),
					["payload"] = new OpenApiObject
					{
						["eventId"] = new OpenApiString("uuid"),
						["projectId"] = new OpenApiString("uuid"),
						["entity"] = new OpenApiString("project | task | employee | dependency | history"),
						["action"] = new OpenApiString("created | updated | deleted | restored | undone"),
						["entityId"] = new OpenApiString("uuid | null"),
						["data"] = new OpenApiString("object | null"),
						["occurredAt"] = new OpenApiString("date-time")
					}
				}
			}
		};

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
