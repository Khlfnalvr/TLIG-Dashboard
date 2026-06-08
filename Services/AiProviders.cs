namespace TLIGDashboard.Services;

/// <summary>
/// Wire protocol an AI provider speaks. DeepSeek and OpenAI use the OpenAI
/// chat-completions shape; Anthropic uses the Messages API (different endpoint,
/// auth header, body shape, and SSE event format). <see cref="AiService"/> and the
/// server proxy branch on these constants.
/// </summary>
public static class AiProtocols
{
    public const string OpenAi    = "openai";
    public const string Anthropic = "anthropic";

    public static bool IsValid(string? p) => p is OpenAi or Anthropic;
}

/// <summary>
/// Static metadata for a supported AI provider: its stable id, display name, base
/// URL, wire protocol, and the catalogue of models we offer in the pickers. This is
/// compile-time information; per-install state (key, enabled flag, chosen models)
/// lives in <see cref="AiProviderSettings"/>.
/// </summary>
public sealed record AiProviderInfo(
    string   Id,
    string   Name,
    string   BaseUrl,
    string   Protocol,
    string[] Models,
    string   KeyHint);

/// <summary>
/// The registry of AI providers TLIG Dashboard can talk to. Order here is the order
/// shown in the provider dropdowns.
/// </summary>
public static class AiProviders
{
    public const string DeepSeek  = "deepseek";
    public const string OpenAi    = "openai";
    public const string Anthropic = "anthropic";

    public static readonly IReadOnlyList<AiProviderInfo> All = new[]
    {
        new AiProviderInfo(
            DeepSeek, "DeepSeek", "https://api.deepseek.com", AiProtocols.OpenAi,
            new[] { "deepseek-chat", "deepseek-reasoner" }, "sk-..."),

        new AiProviderInfo(
            OpenAi, "OpenAI", "https://api.openai.com/v1", AiProtocols.OpenAi,
            new[] { "gpt-4o", "gpt-4o-mini", "gpt-4.1", "gpt-4.1-mini", "o3-mini" }, "sk-..."),

        new AiProviderInfo(
            Anthropic, "Anthropic", "https://api.anthropic.com", AiProtocols.Anthropic,
            new[] { "claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5-20251001" }, "sk-ant-..."),
    };

    public static AiProviderInfo? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(p => p.Id == id);

    /// <summary>The provider info for <paramref name="id"/>, or DeepSeek as a safe fallback.</summary>
    public static AiProviderInfo Resolve(string? id) => Find(id) ?? All[0];

    public static bool IsValid(string? id) => Find(id) is not null;
}
