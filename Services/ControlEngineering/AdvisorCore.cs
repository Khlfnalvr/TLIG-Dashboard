using System;
using System.Threading;
using System.Threading.Tasks;

namespace TLIGDashboard.Services.ControlEngineering;

public static class AdvisorCore
{
    private static readonly Services.AiService _shared = new();
    private static readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Hard ceiling on one advisor review.
    /// </summary>
    /// <remarks>
    /// AiService's HttpClient.Timeout only covers fetching the response HEADERS, because the chat
    /// call uses HttpCompletionOption.ResponseHeadersRead; reading the streamed body afterwards is
    /// outside it. A provider that answers the headers and then goes quiet would leave the read
    /// pending forever — and with it CascadeSessionService.IsRunning, which is exactly what greys
    /// out the dashboard's RUN button with no way back except restarting the app.
    ///
    /// Bounding the review makes that impossible. Timing out is harmless by design: the caller
    /// treats any failure as "Advisor unavailable", and the simulation, metrics and recommendation
    /// are all computed locally, so the student still gets the run — only the written review is
    /// missing.
    /// </remarks>
    private static readonly TimeSpan ReviewTimeout = TimeSpan.FromSeconds(120);

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
            // Started only after the gate is held, so a review queued behind another one gets its
            // own full budget instead of spending it waiting in line.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReviewTimeout);

            Services.AiConfigService.ApplyActive(_shared);
            _shared.SystemPrompt = systemPrompt;
            _shared.ClearHistory();
            return await _shared.StreamChatAsync(taskPrompt, _ => { }, timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            _shared.ClearHistory();
            _gate.Release();
        }
    }
}
