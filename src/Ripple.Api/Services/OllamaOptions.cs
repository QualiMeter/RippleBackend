namespace Ripple.Api.Services;

public sealed class OllamaOptions
{
	public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
	public string Model { get; set; } = "qwen3:4b";
	public int NumCtx { get; set; } = 8192;
	public int NumThread { get; set; } = 4;
	public int NumPredict { get; set; } = 4096;
	public int TimeoutSeconds { get; set; } = 600;
	public string KeepAlive { get; set; } = "5m";
	public bool Think { get; set; } = false;
}
