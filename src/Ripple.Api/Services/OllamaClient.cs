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
					tempId = new { type = new[] { "string", "null" } },
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
You do not execute changes.

ID RULES:
- For CREATE operations, id MUST be JSON null. NEVER output a string in id. NEVER use project_123, task_456, employee_789, or any other pseudo-ID in id.
- For CREATE operations, tempId MUST be a unique string across the ENTIRE plan. Employee tempIds and task tempIds must never overlap.
- For UPDATE and DELETE operations, id MUST be an existing GUID copied exactly from CURRENT RIPPLE CONTEXT.
- Never invent an existing entity ID.
- References to newly created employees/tasks MUST use their tempId fields.
- Do not put the same tempId on two different entities.

DATE RULES:
- All dates MUST use exactly YYYY-MM-DD. Never include a time, timezone, or datetime suffix. If the user says only a month/day, use the CURRENT DATE year; do not invent an old year such as 2023.
- Use the current date supplied by the user message to resolve an unspecified year.
- Do not invent a historical year when the user did not specify one.
- Project and task dates must respect project boundaries.

PLANNING RULES:
- Status must be one of NotStarted, InProgress, Completed, Delayed.
- Dependencies are predecessor -> successor.
- A successor must start strictly on the next calendar day after the predecessor end date or later.
- Do not invent employee names. If the user did not provide an employee, leave assignee fields null.
- For update_project, null fields mean leave unchanged.
- For create_project, create only data supported by the user request and context.
- Do not use markdown, comments, or explanations outside JSON.
""";

		var contextJson = JsonSerializer.Serialize(context, JsonOptions);
		var user = $"""
CURRENT DATE (UTC): {DateOnly.FromDateTime(DateTime.UtcNow):yyyy-MM-dd}

USER REQUEST:
{prompt}

CURRENT RIPPLE CONTEXT:
{contextJson}

Produce a proposed plan. For update_project, null fields mean 'leave unchanged'. Remember: create IDs are null; only tempId identifies newly created entities.
""";

		var request = new
		{
			model = options.Value.Model,
			stream = false,
			think = false,
			keep_alive = "5m",
			messages = new[]
			{
				new { role = "system", content = system },
				new { role = "user", content = user }
			},
			format = OutputSchema,
			options = new
			{
				temperature = 0.1,
				top_p = 0.85,
				num_ctx = Math.Clamp(options.Value.NumCtx, 2048, 8192),
				num_predict = Math.Clamp(options.Value.NumPredict, 256, 1536),
				num_thread = Math.Clamp(options.Value.NumThread, 1, 4),
				stop = new[] { "```", "<|im_end|>" }
			}
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

		var content = result.Message.Content.Trim();
		var json = ExtractJsonObject(content);
		try
		{
			var raw = JsonSerializer.Deserialize<AiRawPlanDocument>(json, JsonOptions)
				?? throw new InvalidOperationException("Ollama returned an empty plan.");
			return Normalize(raw);
		}
		catch (JsonException ex)
		{
			logger.LogError(ex, "Ollama returned malformed plan JSON. DoneReason={DoneReason}, Content={Content}", result.DoneReason, content);
			var reason = string.Equals(result.DoneReason, "length", StringComparison.OrdinalIgnoreCase)
				? "Ollama truncated the plan because the output token limit was reached."
				: $"Ollama returned invalid plan data: {ex.Message}";
			throw new InvalidOperationException(reason);
		}
		catch (InvalidOperationException ex)
		{
			logger.LogWarning("Ollama plan normalization failed: {Message}. Content={Content}", ex.Message, content);
			throw;
		}
	}

	private static AiPlanDocument Normalize(AiRawPlanDocument raw)
	{
		var document = new AiPlanDocument
		{
			Operation = raw.Operation?.Trim() ?? "",
			Summary = raw.Summary?.Trim() ?? ""
		};

		if (raw.Project is not null)
		{
			document.Project = new AiProjectChange
			{
				Id = ParseGuid(raw.Project.Id, "project.id", allowInvalidForCreate: string.Equals(raw.Operation, "create_project", StringComparison.OrdinalIgnoreCase)),
				TempId = NullIfWhiteSpace(raw.Project.TempId),
				Name = NullIfWhiteSpace(raw.Project.Name),
				StartDate = NormalizeDate(raw.Project.StartDate, "project.startDate"),
				EndDate = NormalizeDate(raw.Project.EndDate, "project.endDate")
			};
		}

		var usedTempIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in raw.Employees)
		{
			var action = item.Action?.Trim() ?? "create";
			var tempId = NullIfWhiteSpace(item.TempId);
			if (string.Equals(action, "create", StringComparison.OrdinalIgnoreCase))
				tempId = MakeUniqueTempId(tempId, "employee", usedTempIds);

			document.Employees.Add(new AiEmployeeChange
			{
				Action = action,
				Id = ParseGuid(item.Id, "employee.id", allowInvalidForCreate: string.Equals(action, "create", StringComparison.OrdinalIgnoreCase)),
				TempId = tempId,
				Name = NullIfWhiteSpace(item.Name),
				Phone = NullIfWhiteSpace(item.Phone),
				Email = NullIfWhiteSpace(item.Email)
			});
		}

		var taskPseudoIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in raw.Tasks)
		{
			var action = item.Action?.Trim() ?? "create";
			var tempId = NullIfWhiteSpace(item.TempId);
			if (string.Equals(action, "create", StringComparison.OrdinalIgnoreCase))
			{
				var originalPseudoId = NullIfWhiteSpace(item.Id);
				tempId = MakeUniqueTempId(tempId, "task", usedTempIds);
				if (!string.IsNullOrWhiteSpace(originalPseudoId) && !Guid.TryParse(originalPseudoId, out _))
					taskPseudoIdMap[originalPseudoId] = tempId!;
			}

			document.Tasks.Add(new AiTaskChange
			{
				Action = action,
				Id = ParseGuid(item.Id, "task.id", allowInvalidForCreate: string.Equals(action, "create", StringComparison.OrdinalIgnoreCase)),
				TempId = tempId,
				Name = NullIfWhiteSpace(item.Name),
				StartDate = NormalizeDate(item.StartDate, "task.startDate"),
				EndDate = NormalizeDate(item.EndDate, "task.endDate"),
				Status = NullIfWhiteSpace(item.Status),
				AssigneeId = ParseGuid(item.AssigneeId, "task.assigneeId", allowInvalidForCreate: string.Equals(action, "create", StringComparison.OrdinalIgnoreCase)),
				AssigneeTempId = NullIfWhiteSpace(item.AssigneeTempId)
			});
		}

		foreach (var item in raw.Dependencies)
		{
			var action = item.Action?.Trim() ?? "create";
			var predecessorTempId = NullIfWhiteSpace(item.PredecessorTempId);
			var successorTempId = NullIfWhiteSpace(item.SuccessorTempId);
			if (string.Equals(action, "create", StringComparison.OrdinalIgnoreCase))
			{
				var pseudoPredecessor = NullIfWhiteSpace(item.PredecessorTaskId);
				var pseudoSuccessor = NullIfWhiteSpace(item.SuccessorTaskId);
				if (!string.IsNullOrWhiteSpace(pseudoPredecessor) && taskPseudoIdMap.TryGetValue(pseudoPredecessor, out var mappedPredecessor))
					predecessorTempId = mappedPredecessor;
				if (!string.IsNullOrWhiteSpace(pseudoSuccessor) && taskPseudoIdMap.TryGetValue(pseudoSuccessor, out var mappedSuccessor))
					successorTempId = mappedSuccessor;
			}

			document.Dependencies.Add(new AiDependencyChange
			{
				Action = action,
				PredecessorTaskId = ParseGuid(item.PredecessorTaskId, "dependency.predecessorTaskId", allowInvalidForCreate: string.Equals(action, "create", StringComparison.OrdinalIgnoreCase)),
				SuccessorTaskId = ParseGuid(item.SuccessorTaskId, "dependency.successorTaskId", allowInvalidForCreate: string.Equals(action, "create", StringComparison.OrdinalIgnoreCase)),
				PredecessorTempId = predecessorTempId,
				SuccessorTempId = successorTempId
			});
		}

		// A model must never choose database IDs for newly created entities.
		if (document.Operation.Equals("create_project", StringComparison.OrdinalIgnoreCase))
		{
			if (document.Project is not null) document.Project.Id = null;
			foreach (var employee in document.Employees.Where(x => x.Action.Equals("create", StringComparison.OrdinalIgnoreCase))) employee.Id = null;
			foreach (var task in document.Tasks.Where(x => x.Action.Equals("create", StringComparison.OrdinalIgnoreCase)))
			{
				task.Id = null;
				task.AssigneeId = null;
			}
			foreach (var dependency in document.Dependencies.Where(x => x.Action.Equals("create", StringComparison.OrdinalIgnoreCase)))
			{
				dependency.PredecessorTaskId = null;
				dependency.SuccessorTaskId = null;
			}
		}

		return document;
	}

	private static string? MakeUniqueTempId(string? requested, string prefix, ISet<string> used)
	{
		var candidate = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
		if (!string.IsNullOrWhiteSpace(candidate) && used.Add(candidate))
			return candidate;

		for (var index = 1; ; index++)
		{
			candidate = $"{prefix}_{index}";
			if (used.Add(candidate))
				return candidate;
		}
	}

	private static Guid? ParseGuid(string? value, string field, bool allowInvalidForCreate)
	{
		if (string.IsNullOrWhiteSpace(value)) return null;
		if (Guid.TryParse(value, out var guid)) return guid;
		if (allowInvalidForCreate) return null;
		throw new InvalidOperationException($"AI returned a non-GUID value for {field}: '{value}'. Existing entities require a real GUID from CURRENT RIPPLE CONTEXT.");
	}

	private static string? NormalizeDate(string? value, string field)
	{
		if (string.IsNullOrWhiteSpace(value)) return null;
		if (DateOnly.TryParseExact(value, "yyyy-MM-dd", out var date)) return date.ToString("yyyy-MM-dd");
		if (DateTimeOffset.TryParse(value, out var dto)) return DateOnly.FromDateTime(dto.DateTime).ToString("yyyy-MM-dd");
		throw new InvalidOperationException($"AI returned invalid date for {field}: '{value}'. Expected YYYY-MM-DD.");
	}

	private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

	private static string ExtractJsonObject(string content)
	{
		var start = content.IndexOf('{');
		var end = content.LastIndexOf('}');
		if (start < 0 || end <= start)
			return content;

		return content[start..(end + 1)];
	}
}
