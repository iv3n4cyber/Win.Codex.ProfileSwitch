using System.Text.Json;

namespace Win.Codex.ProfileSwitch;

internal sealed class CodexUsageService
{
    private const int MaxSessionFilesToScan = 80;
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public async Task<CodexUsageStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var account = ReadOAuthAccount();
        if (account is null)
        {
            return CodexUsageStatus.NotOAuth();
        }

        try
        {
            return CodexUsageStatus.Available(await FetchWhamUsageAsync(account, cancellationToken));
        }
        catch (Exception ex)
        {
            var fallback = FindLatestSnapshot();
            return fallback is not null
                ? CodexUsageStatus.Available(fallback)
                : CodexUsageStatus.Unavailable(AppText.S(
                    $"WHAM usage refresh failed: {ex.Message}",
                    $"WHAM usage 刷新失败：{ex.Message}"
                ));
        }
    }

    public CodexUsageStatus GetLocalSnapshotStatus()
    {
        if (ReadOAuthAccount() is null)
        {
            return CodexUsageStatus.NotOAuth();
        }

        var snapshot = FindLatestSnapshot();
        return snapshot is null
            ? CodexUsageStatus.Unavailable(AppText.S(
                "No local usage snapshot found yet.",
                "尚未找到本地 usage 快照。"
            ))
            : CodexUsageStatus.Available(snapshot);
    }

    private static OAuthAccount? ReadOAuthAccount()
    {
        if (!File.Exists(CodexPaths.AuthJsonPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(CodexPaths.AuthJsonPath));
            var root = document.RootElement;

            if (!root.TryGetProperty("tokens", out var tokens) ||
                tokens.ValueKind != JsonValueKind.Object ||
                !tokens.TryGetProperty("access_token", out var accessToken) ||
                accessToken.ValueKind != JsonValueKind.String ||
                !tokens.TryGetProperty("account_id", out var accountId) ||
                accountId.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var accessTokenValue = accessToken.GetString();
            var accountIdValue = accountId.GetString();
            return string.IsNullOrWhiteSpace(accessTokenValue) || string.IsNullOrWhiteSpace(accountIdValue)
                ? null
                : new OAuthAccount(accessTokenValue, accountIdValue);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<CodexUsageSnapshot> FetchWhamUsageAsync(
        OAuthAccount account,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", account.AccessToken);
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", account.AccountId);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("oai-language", AppText.CurrentLanguage == AppLanguage.Chinese ? "zh-CN" : "en-US");
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"
        );
        request.Headers.Referrer = new Uri("https://chatgpt.com/codex/settings/usage");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("rate_limit", out var rateLimit) ||
            !rateLimit.TryGetProperty("primary_window", out var primary) ||
            !rateLimit.TryGetProperty("secondary_window", out var secondary))
        {
            throw new InvalidOperationException(AppText.S("WHAM usage response is missing rate_limit windows.", "WHAM usage 响应缺少 rate_limit 窗口。"));
        }

        var planType = root.TryGetProperty("plan_type", out var planElement) &&
            planElement.ValueKind == JsonValueKind.String
            ? planElement.GetString()
            : null;

        return new CodexUsageSnapshot(
            DateTimeOffset.UtcNow,
            ParseWhamLimit(primary),
            ParseWhamLimit(secondary),
            planType
        );
    }

    private static CodexUsageSnapshot? FindLatestSnapshot()
    {
        var sessionsRoot = Path.Combine(CodexPaths.CodexRoot, "sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(MaxSessionFilesToScan))
        {
            var snapshot = FindLatestSnapshotInFile(file.FullName);
            if (snapshot is not null)
            {
                return snapshot;
            }
        }

        return null;
    }

    private static CodexUsageSnapshot? FindLatestSnapshotInFile(string path)
    {
        CodexUsageSnapshot? latest = null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("rate_limits", StringComparison.Ordinal))
                {
                    continue;
                }

                var snapshot = TryParseSnapshot(line);
                if (snapshot is not null)
                {
                    latest = snapshot;
                }
            }
        }
        catch
        {
            return null;
        }

        return latest;
    }

    private static CodexUsageSnapshot? TryParseSnapshot(string jsonLine)
    {
        try
        {
            using var document = JsonDocument.Parse(jsonLine);
            var root = document.RootElement;
            if (!root.TryGetProperty("payload", out var payload) ||
                !payload.TryGetProperty("rate_limits", out var rateLimits))
            {
                return null;
            }

            if (!rateLimits.TryGetProperty("primary", out var primary) ||
                !rateLimits.TryGetProperty("secondary", out var secondary))
            {
                return null;
            }

            var timestamp = TryGetDateTimeOffset(root, "timestamp") ?? DateTimeOffset.UtcNow;
            return new CodexUsageSnapshot(
                timestamp,
                ParseSessionLimit(primary),
                ParseSessionLimit(secondary),
                TryGetString(rateLimits, "plan_type")
            );
        }
        catch
        {
            return null;
        }
    }

    private static CodexUsageLimit ParseWhamLimit(JsonElement element)
    {
        var usedPercent = TryGetDouble(element, "used_percent") ?? 0;
        var windowSeconds = TryGetInt(element, "limit_window_seconds") ?? 0;
        var resetsAt = TryGetLong(element, "reset_at");
        var resetAt = resetsAt is null
            ? (DateTimeOffset?)null
            : DateTimeOffset.FromUnixTimeSeconds(resetsAt.Value);

        return new CodexUsageLimit(usedPercent, windowSeconds, resetAt);
    }

    private static CodexUsageLimit ParseSessionLimit(JsonElement element)
    {
        var usedPercent = TryGetDouble(element, "used_percent") ?? 0;
        var windowMinutes = TryGetInt(element, "window_minutes") ?? 0;
        var resetsAt = TryGetLong(element, "resets_at");
        var resetAt = resetsAt is null
            ? (DateTimeOffset?)null
            : DateTimeOffset.FromUnixTimeSeconds(resetsAt.Value);

        return new CodexUsageLimit(usedPercent, windowMinutes * 60, resetAt);
    }

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var number)
            ? number
            : null;
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    private static long? TryGetLong(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var number)
            ? number
            : null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private sealed record OAuthAccount(string AccessToken, string AccountId);
}

internal enum CodexUsageStatusKind
{
    NotOAuth,
    Unavailable,
    Available
}

internal sealed record CodexUsageStatus(
    CodexUsageStatusKind Kind,
    CodexUsageSnapshot? Snapshot = null,
    string? Message = null
)
{
    public static CodexUsageStatus NotOAuth() => new(CodexUsageStatusKind.NotOAuth);
    public static CodexUsageStatus Unavailable(string message) => new(CodexUsageStatusKind.Unavailable, Message: message);
    public static CodexUsageStatus Available(CodexUsageSnapshot snapshot) => new(CodexUsageStatusKind.Available, snapshot);
}

internal sealed record CodexUsageSnapshot(
    DateTimeOffset CapturedAt,
    CodexUsageLimit Primary,
    CodexUsageLimit Secondary,
    string? PlanType = null
);

internal sealed record CodexUsageLimit(
    double UsedPercent,
    int WindowSeconds,
    DateTimeOffset? ResetAt
)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}
