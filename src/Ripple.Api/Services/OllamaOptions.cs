namespace Ripple.Api.Services;

public sealed class OllamaOptions
{
	public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
	public string Model { get; set; } = "qwen3:4b";
	public int TimeoutSeconds { get; set; } = 120;
}
