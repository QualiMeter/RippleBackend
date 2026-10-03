using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Ripple.Api.Services;

public interface IOllamaClient
{
	Task<AiPlanDocument> CreatePlanAsync(string prompt, AiPlanContext context, CancellationToken ct);
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
					required = new[] { "action", "id", "tempId", "name", "startDate", "endDate", "status", "assigneeId", "assigneeTempId" },
					properties = new
					{
						action = new { type = "string", @enum = new[] { "create", "update", "delete" } },
						id = new { type = new[] { "string", "null" } },
						tempId = new { type = new[] { "string", "null" } },
						name = new { type = new[] { "string", "null" } },
						startDate = new { type = new[] { "string", "null" } },
						endDate = new { type = new[] { "string", "null" } },
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

	public async Task<AiPlanDocument> CreatePlanAsync(string prompt, AiPlanContext context, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(prompt))
			throw new ArgumentException("AI prompt is required.");

		var system = """
You are Ripple's project planning engine. Return only the JSON object required by the supplied schema.
You do not execute changes and you must never invent existing IDs.
For update_project, use only IDs present in the supplied project context.
For create_project, IDs for new employees/tasks/projects are not domain GUIDs. You may use simple unique placeholder strings such as employee_1 and task_1. Always use tempId as the canonical reference for newly created entities. Never put a task_* value into an employee tempId or an employee_* value into a task tempId. For a new task assignee, prefer assigneeTempId with the exact employee tempId and leave assigneeId null. For a new dependency, prefer predecessorTempId/successorTempId with the exact task tempIds and leave predecessorTaskId/successorTaskId null.
Dates must use ISO format YYYY-MM-DD. Status must be one of NotStarted, InProgress, Completed, Delayed.
Dependencies are predecessor -> successor and the successor must start strictly after the predecessor end date.
Ripple will normalize dependency dates after generation, preserving task duration and cascading shifts to successors.
The project end date must cover all scheduled tasks; if the requested work requires a later end, propose that later project end explicitly.
Respect project boundaries when proposing dates. Do not invent employee names; if the user did not provide an employee, leave assignee fields null.
Do not use markdown, comments, explanations outside JSON.
""";

		var contextJson = JsonSerializer.Serialize(context, JsonOptions);
		var user = $"""
USER REQUEST:
{prompt}

CURRENT RIPPLE CONTEXT:
{contextJson}

Produce a proposed plan. For update_project, null fields mean 'leave unchanged'. If changing only an existing task's startDate, leave endDate null so Ripple preserves the existing task duration. If changing only endDate, leave startDate null unless the user explicitly requested a start-date change.
""";

		var request = new
		{
			model = options.Value.Model,
			stream = false,
			think = false,
			keep_alive = "10m",
			messages = new[]
			{
				new { role = "system", content = system },
				new { role = "user", content = user }
			},
			format = OutputSchema,
			options = new { temperature = 0.1 }
		};

		using var response = await http.PostAsJsonAsync("api/chat", request, JsonOptions, ct);
		var body = await response.Content.ReadAsStringAsync(ct);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Ollama request failed ({(int)response.StatusCode}): {body}");

		OllamaChatResponse? result;
		try
		{
			result = JsonSerializer.Deserialize<OllamaChatResponse>(body, JsonOptions);
		}
		catch (JsonException ex)
		{
			logger.LogError(ex, "Failed to parse Ollama response.");
			throw new InvalidOperationException("Ollama returned an invalid response.");
		}

		if (result?.Message is null || string.IsNullOrWhiteSpace(result.Message.Content))
			throw new InvalidOperationException("Ollama returned an empty plan.");

		logger.LogInformation("Ollama returned AI plan JSON: {PlanJson}", result.Message.Content);

		try
		{
			return JsonSerializer.Deserialize<AiPlanDocument>(result.Message.Content, JsonOptions)
				?? throw new InvalidOperationException("Ollama returned an empty plan.");
		}
		catch (JsonException ex)
		{
			logger.LogError(ex, "Ollama returned malformed plan JSON: {Content}", result.Message.Content);
			throw new InvalidOperationException("Ollama returned a plan that could not be parsed.");
		}
	}
}
