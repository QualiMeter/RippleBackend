namespace Ripple.Api.Services;

public sealed class OllamaOptions
{
	public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
	public string Model { get; set; } = "qwen3:1.7b";
	public int TimeoutSeconds { get; set; } = 180;
	public int NumPredict { get; set; } = 768;
	public int NumCtx { get; set; } = 3072;
	public int NumThread { get; set; } = 2;
}
