using System;
using System.Threading;
using System.Threading.Tasks;

namespace TLIGDashboard.Services.ControlEngineering;

public static class AdvisorCore
{
    private static readonly Services.AiService _shared = new();
    private static readonly SemaphoreSlim _gate = new(1, 1);

    public static string ResolveLanguage(string requested) =>
        string.IsNullOrWhiteSpace(requested)
            ? Services.LocalizationManager.Instance.CurrentLanguage
            : requested;

    public static string LanguageRule(string lang, string extraTerms = "") =>
        (lang == "id"
            ? "Write your review in Indonesian (Bahasa Indonesia), using standard control " +
              "engineering terminology. Keep the established English terms (overshoot, rise time, " +
              "settling time, steady-state error, setpoint, gain" +
              (string.IsNullOrEmpty(extraTerms) ? "" : ", " + extraTerms) +
              ") rather than translating them, as that is how they appear in the course material and UI."
            : "Write your review in English.");

    public static async Task<string> GetReviewAsync(string systemPrompt, string taskPrompt, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Services.AiConfigService.ApplyActive(_shared);
            _shared.SystemPrompt = systemPrompt;
            _shared.ClearHistory();
            return await _shared.StreamChatAsync(taskPrompt, _ => { }, ct).ConfigureAwait(false);
        }
        finally
        {
            _shared.ClearHistory();
            _gate.Release();
        }
    }
}
