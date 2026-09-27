using Microsoft.AspNetCore.ResponseCompression;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using ProjectManagement.Api.Data;
using ProjectManagement.Api.Middleware;
using ProjectManagement.Api.Services;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
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
		document.Servers =
		[
			new OpenApiServer
			{
				Url = "https://localhost:7080"
			}
		];

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

var app = builder.Build();

app.UseResponseCompression();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.MapOpenApi();
app.MapScalarApiReference("/scalar", options => options.WithTitle("Project Management MVP API").WithOpenApiRoutePattern("/openapi/{documentName}.json"));
app.MapControllers();

await using (var scope = app.Services.CreateAsyncScope())
{
	var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
	await DbInitializer.InitializeAsync(db);
}

app.Run();
