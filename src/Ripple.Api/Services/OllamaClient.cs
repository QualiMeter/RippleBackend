using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text;

namespace Ripple.Api.Services;

public interface IOllamaClient
{
	Task<AiPlanDocument> CreatePlanAsync(string prompt, AiPlanContext context, CancellationToken ct, Func<string, int, string?, Task>? onProgress = null);
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

	public async Task<AiPlanDocument> CreatePlanAsync(string prompt, AiPlanContext context, CancellationToken ct, Func<string, int, string?, Task>? onProgress = null)
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

PLANNING DETAIL REQUIREMENTS
- Prefer a detailed, actionable project plan over a short high-level outline. Decompose the user's goal into concrete deliverables and implementation steps.
- For a new project, normally create about 8-15 meaningful tasks for a medium-sized request, and more when the requested scope clearly requires it. Do not inflate the plan with meaningless micro-tasks.
- A task should represent one concrete piece of work that can be completed and verified independently. Avoid vague tasks such as "work on the project", "continue development", or "finish everything".
- Break major phases into smaller tasks. For example, a software project can include requirements/architecture, project setup, core implementation, data/model work, API or integration work, validation/testing, error handling, documentation, and final integration where applicable. Only include categories that make sense for the user's request.
- Give each task a specific, descriptive name that explains the expected outcome.
- Give each created task a realistic durationDays. Simple tasks are usually 1-3 days; substantial implementation tasks can take 3-7 days; larger independent deliverables can take 5-14 days. Do not make every task the same duration.
- Create dependencies that represent the real execution order. Connect prerequisite work to the work that depends on it, but do not create dependencies merely to make the graph longer.
- When a task naturally contains multiple independent deliverables, split them into separate tasks so the timeline is useful.
- Include testing/verification tasks when the request involves software, infrastructure, automation, data processing, or another area where correctness must be checked.
- Include documentation/deployment/integration tasks when they are relevant to the requested outcome.
- For a learning roadmap, divide the subject into concrete topics and practice tasks rather than creating one task per broad technology. Include hands-on exercises or small projects after relevant theory.
- The plan must remain focused on the user's actual request. Do not add unrelated features or speculative work.
- Do not sacrifice JSON validity for detail. Every task must conform to the schema and every dependency must reference a valid task/tempId.
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
			// Qwen3 thinking is intentionally kept internal. We stream safe progress
			// updates to the UI instead of exposing private chain-of-thought.
			think = true,
			keep_alive = options.Value.KeepAlive,
			messages = new[]
			{
				new { role = "system", content = system },
				new { role = "user", content = user }
			},
			format = OutputSchema,
			options = new
			{
				temperature = 0.1,
				num_ctx = options.Value.NumCtx,
				num_thread = options.Value.NumThread,
				num_predict = options.Value.NumPredict
			}
		};

		using var response = await http.PostAsJsonAsync("api/chat", request, JsonOptions, ct);
		if (!response.IsSuccessStatusCode)
		{
			var errorBody = await response.Content.ReadAsStringAsync(ct);
			throw new InvalidOperationException($"Ollama request failed ({(int)response.StatusCode}): {errorBody}");
		}

		if (onProgress is not null) await onProgress("ollama_started", 0, null);
		await using var stream = await response.Content.ReadAsStreamAsync(ct);
		using var reader = new StreamReader(stream);
		var content = new StringBuilder();
		var received = 0;
		var done = false;
		string? doneReason = null;

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

			if (chunk is not null)
			{
				done |= chunk.Done;
				doneReason = chunk.DoneReason ?? doneReason;
			}

			var piece = chunk?.Message?.Content;
			if (!string.IsNullOrEmpty(piece))
			{
				content.Append(piece);
				received += piece.Length;
				if (received % 160 < piece.Length)
					if (onProgress is not null) await onProgress("generating", received, piece);
			}
		}

		if (content.Length == 0)
			throw new InvalidOperationException("Ollama returned an empty plan.");

		if (doneReason is "length" or "max_tokens")
		{
			logger.LogWarning("Ollama generation reached num_predict limit. Received {Characters} characters.", received);
			if (onProgress is not null)
				await onProgress("generation_limit", Math.Min(75, 20 + received / 80), "ИИ не успел завершить JSON-план: увеличен лимит генерации.");
		}

		var planJson = content.ToString();
		logger.LogInformation("Ollama returned AI plan JSON: {PlanJson}", planJson);
		if (onProgress is not null) await onProgress("response_received", received, null);

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
