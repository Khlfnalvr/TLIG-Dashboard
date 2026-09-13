using System;
using System.Collections.Generic;

namespace TLIGDashboard.Services;

public static class AiContextLimits
{
    public const int DefaultLimit = 128000;

    private static readonly Dictionary<string, int> _limits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deepseek-v4-flash"] = 128000,
        ["deepseek-v4-pro"]   = 128000,
        ["gpt-5.5"]        = 400000,
        ["gpt-5.4"]        = 400000,
        ["gpt-5.4-mini"]   = 400000,
        ["gpt-5.4-nano"]   = 400000,
        ["claude-opus-4-8"]  = 200000,
        ["claude-sonnet-5"]  = 200000,
        ["claude-sonnet-4-6"] = 200000,
        ["claude-haiku-4-5"] = 200000,
        ["gemini-3.1-pro-preview"] = 1000000,
        ["gemini-3.5-flash"]       = 1000000,
        ["gemini-3.1-flash-lite"]  = 1000000,
        ["gemini-2.5-pro"]   = 1000000,
        ["gemini-2.5-flash"] = 1000000,
        ["gemini-2.0-flash"] = 1000000,
        ["gemini-2.0-flash-lite"] = 1000000,
    };

    public static int ForModel(string? model, out bool known)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            string id = model.Trim();
            if (_limits.TryGetValue(id, out int limit))
            {
                known = true;
                return limit;
            }
            if (id.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)) { known = true; return 1000000; }
            if (id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))   { known = true; return 400000; }
            if (id.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) { known = true; return 200000; }
            if (id.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase)) { known = true; return 128000; }
        }
        known = false;
        return DefaultLimit;
    }
}
