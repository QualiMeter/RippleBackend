using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text;

namespace Ripple.Api.Services;

public interface IOllamaClient
{
	Task<AiPlanDocument> CreatePlanAsync(string prompt, AiPlanContext context, CancellationToken ct, Func<string, int, Task>? onProgress = null);
}

public sealed class OllamaClient(HttpClient http, IOptions<OllamaOptions> options, ILogger<OllamaClient> logger) : IOllamaClient
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		PropertyNameCaseInsensitive = true
	};

	static OllamaClient()
	{
		JsonOptions.Converters.Add(new AiGuidJsonConverter());
	}

	private static readonly object OutputSchema = new
	{
		type = "object",
		additionalProperties = false,
		required = new[] { "operation", "summary", "project", "employees", "tasks", "dependencies" },
		properties = new
		{
			operation = new { type = "string", @enum = new[] { "create_project", "update_project" } },
			summary = new { type = "string" },
			project = new
			{
				type = new[] { "object", "null" },
				additionalProperties = false,
				properties = new
				{
					id = new { type = new[] { "string", "null" } },
					name = new { type = new[] { "string", "null" } },
					startDate = new { type = new[] { "string", "null" } },
					endDate = new { type = new[] { "string", "null" } }
				}
			},
			employees = new
			{
				type = "array",
				items = new
				{
					type = "object",
					additionalProperties = false,
					required = new[] { "action", "id", "tempId", "name", "phone", "email" },
					properties = new
					{
						action = new { type = "string", @enum = new[] { "create", "update", "delete" } },
						id = new { type = new[] { "string", "null" } },
						tempId = new { type = new[] { "string", "null" } },
						name = new { type = new[] { "string", "null" } },
						phone = new { type = new[] { "string", "null" } },
						email = new { type = new[] { "string", "null" } }
					}
				}
			},
			tasks = new
			{
				type = "array",
				items = new
				{
					type = "object",
					additionalProperties = false,
					required = new[] { "action", "id", "tempId", "name", "startDate", "endDate", "durationDays", "status", "assigneeId", "assigneeTempId" },
					properties = new
					{
						action = new { type = "string", @enum = new[] { "create", "update", "delete" } },
						id = new { type = new[] { "string", "null" } },
						tempId = new { type = new[] { "string", "null" } },
						name = new { type = new[] { "string", "null" } },
						startDate = new { type = new[] { "string", "null" } },
						endDate = new { type = new[] { "string", "null" } },
						durationDays = new { type = new[] { "integer", "null" }, minimum = 1 },
						status = new { type = new[] { "string", "null" } },
						assigneeId = new { type = new[] { "string", "null" } },
						assigneeTempId = new { type = new[] { "string", "null" } }
					}
				}
			},
			dependencies = new
			{
				type = "array",
				items = new
				{
					type = "object",
					additionalProperties = false,
					required = new[] { "action", "predecessorTaskId", "successorTaskId", "predecessorTempId", "successorTempId" },
					properties = new
					{
						action = new { type = "string", @enum = new[] { "create", "delete" } },
						predecessorTaskId = new { type = new[] { "string", "null" } },
						successorTaskId = new { type = new[] { "string", "null" } },
						predecessorTempId = new { type = new[] { "string", "null" } },
						successorTempId = new { type = new[] { "string", "null" } }
					}
				}
			}
		}
	};

	public async Task<AiPlanDocument> CreatePlanAsync(string prompt, AiPlanContext context, CancellationToken ct, Func<string, int, Task>? onProgress = null)
	{
		if (string.IsNullOrWhiteSpace(prompt))
			throw new ArgumentException("AI prompt is required.");

		var system = """
You are Ripple's project planning engine. Return only the JSON object required by the supplied schema.
You do not execute changes and you must never invent existing IDs.
For update_project, use only IDs present in the supplied project context.
For create_project, IDs for new employees/tasks/projects are not domain GUIDs. You may use simple unique placeholder strings such as employee_1 and task_1. Always use tempId as the canonical reference for newly created entities. Never put a task_* value into an employee tempId or an employee_* value into a task tempId. For a new task assignee, prefer assigneeTempId with the exact employee tempId and leave assigneeId null. For a new dependency, prefer predecessorTempId/successorTempId with the exact task tempIds and leave predecessorTaskId/successorTaskId null.
Project dates must use ISO format YYYY-MM-DD. For TASKS, do NOT calculate or invent endDate. The only scheduling value Qwen should provide for a task is durationDays: a positive integer counting calendar days INCLUDING both the start and end day (1 means a one-day task). startDate may be provided only when the user explicitly specified a task start; otherwise leave task startDate and endDate null. Backend C# is authoritative for task dates and calculates endDate from startDate + durationDays - 1. For updates, durationDays means the desired new task duration; if durationDays is null, preserve the existing duration. Status must be one of NotStarted, InProgress, Completed, Delayed.
Dependencies are predecessor -> successor and the successor must start strictly on the next calendar day after the predecessor end date. Always include every dependency explicitly required by the user's wording or implied by a clear sequence such as "design, then development, then testing".
Ripple will calculate all task dates, preserve duration, and cascade dependency shifts. Never use timestamps, time zones, ISO date-times, or endDate values for tasks.
The project end date must cover all scheduled tasks; backend may extend it automatically when dependencies require more time.
Respect project boundaries when proposing dates. Do not invent employee names; if the user did not provide an employee, leave assignee fields null.
Do not use markdown, comments, explanations outside JSON.
""";

		var now = DateTime.Now;
		var currentDate = now.ToString("yyyy-MM-dd");
		var currentDateTime = now.ToString("yyyy-MM-dd HH:mm:ss");
		var currentYear = now.Year;
		var currentTimeZoneOffset = now.ToString("zzz");

		var contextJson = JsonSerializer.Serialize(context, JsonOptions);
		var user = $"""
RUNTIME DATE/TIME — AUTHORITATIVE
================================
Current local date: {currentDate}
Current local year: {currentYear}
Current local date/time: {currentDateTime}
Local UTC offset: {currentTimeZoneOffset}

These values are provided directly by the Ripple backend runtime. They are authoritative.
Do not infer the current date or year from training data.
If the user says today, tomorrow, yesterday, next week, next month, this year, or similar relative date, calculate it from CURRENT local date above.
Do not assume the year is 2023, 2024, or any other year unless the user explicitly requests a historical date.

USER REQUEST:
{prompt}

CURRENT RIPPLE CONTEXT:
{contextJson}

Produce a proposed plan. For update_project, null fields mean 'leave unchanged'. For every created task, provide durationDays and use startDate only when the user explicitly specified its start. Always leave task endDate null; the backend calculates it. For an existing task, use durationDays only when the user asks to change its duration. If the user asks to move a task without changing its duration, provide startDate and leave durationDays null. Never output a task endDate.
""";

		var request = new
		{
			model = options.Value.Model,
			stream = true,
			think = false,
			keep_alive = "10m",
			messages = new[]
			{
				new { role = "system", content = system },
				new { role = "user", content = user }
			},
			format = OutputSchema,
			options = new { temperature = 0.1, num_ctx = 8192, num_predict = 2048 }
		};

		using var response = await http.PostAsJsonAsync("api/chat", request, JsonOptions, ct);
		if (!response.IsSuccessStatusCode)
		{
			var errorBody = await response.Content.ReadAsStringAsync(ct);
			throw new InvalidOperationException($"Ollama request failed ({(int)response.StatusCode}): {errorBody}");
		}

		if (onProgress is not null) await onProgress("ollama_started", 0);
		await using var stream = await response.Content.ReadAsStreamAsync(ct);
		using var reader = new StreamReader(stream);
		var content = new StringBuilder();
		var received = 0;

		while (!reader.EndOfStream)
		{
			var line = await reader.ReadLineAsync(ct);
			if (string.IsNullOrWhiteSpace(line)) continue;

			OllamaChatResponse? chunk;
			try
			{
				chunk = JsonSerializer.Deserialize<OllamaChatResponse>(line, JsonOptions);
			}
			catch (JsonException)
			{
				continue;
			}

			var piece = chunk?.Message?.Content;
			if (!string.IsNullOrEmpty(piece))
			{
				content.Append(piece);
				received += piece.Length;
				if (received % 160 < piece.Length)
					if (onProgress is not null) await onProgress("generating", received);
			}
		}

		if (content.Length == 0)
			throw new InvalidOperationException("Ollama returned an empty plan.");

		var planJson = content.ToString();
		logger.LogInformation("Ollama returned AI plan JSON: {PlanJson}", planJson);
		if (onProgress is not null) await onProgress("response_received", received);

		try
		{
			return JsonSerializer.Deserialize<AiPlanDocument>(planJson, JsonOptions)
				?? throw new InvalidOperationException("Ollama returned an empty plan.");
		}
		catch (JsonException ex)
		{
			logger.LogError(ex, "Ollama returned malformed plan JSON: {Content}", planJson);
			throw new InvalidOperationException("Ollama returned a plan that could not be parsed.");
		}
	}
}
