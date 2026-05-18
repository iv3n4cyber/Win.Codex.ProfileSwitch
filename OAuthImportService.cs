using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Win.Codex.ProfileSwitch;

internal sealed class OAuthImportService
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string RedirectUri = "http://localhost:1455/auth/callback";
    private const string AuthUrl = "https://auth.openai.com/oauth/authorize";
    private const string TokenUrl = "https://auth.openai.com/oauth/token";
    private const string Scope = "openid profile email offline_access api.connectors.read api.connectors.invoke";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    private readonly ProfileSwitcherService profileService;

    public OAuthImportService(ProfileSwitcherService profileService)
    {
        this.profileService = profileService;
    }

    public async Task<OAuthProfileImportResult> ImportAsync(CancellationToken cancellationToken = default)
    {
        var flow = PendingOAuthFlow.Create();
        using var callbackServer = new LocalhostOAuthCallbackServer();

        callbackServer.Start();
        OpenBrowser(BuildAuthorizationUrl(flow));

        var callbackUrl = await callbackServer.WaitForCallbackAsync(cancellationToken);
        var (code, state) = ParseCallback(callbackUrl);
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException(AppText.S(
                "The OAuth callback did not contain an authorization code.",
                "OAuth 回调中没有授权 code。"
            ));
        }

        if (!string.IsNullOrWhiteSpace(state) &&
            !string.Equals(state, flow.ExpectedState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(AppText.S(
                "The OAuth callback state did not match. Please try again.",
                "OAuth 回调 state 不匹配，请重新尝试。"
            ));
        }

        var tokens = await ExchangeCodeAsync(code, flow, cancellationToken);
        var account = OAuthAccountInfo.FromTokens(tokens);
        if (string.IsNullOrWhiteSpace(account.RemoteAccountId))
        {
            throw new InvalidOperationException(AppText.S(
                "OAuth login succeeded, but the account id could not be resolved from the token.",
                "OAuth 登录成功，但无法从 token 中解析 account id。"
            ));
        }

        var profileName = PreferredProfileName(account);
        var profile = profileService.CreateProfileFromOAuth(
            profileName,
            RenderAuthJson(tokens, account),
            RenderConfigToml()
        );

        return new OAuthProfileImportResult(profile, account.Email);
    }

    private static Uri BuildAuthorizationUrl(PendingOAuthFlow flow)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scope,
            ["code_challenge"] = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(flow.CodeVerifier))),
            ["code_challenge_method"] = "S256",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["state"] = flow.ExpectedState,
            ["originator"] = "Codex Desktop"
        };

        var builder = new UriBuilder(AuthUrl)
        {
            Query = string.Join("&", query.Select(item =>
                $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"))
        };
        return builder.Uri;
    }

    private static void OpenBrowser(Uri url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url.AbsoluteUri,
            UseShellExecute = true
        });
    }

    private static async Task<OAuthTokens> ExchangeCodeAsync(
        string code,
        PendingOAuthFlow flow,
        CancellationToken cancellationToken
    )
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = flow.CodeVerifier
        });
        using var response = await HttpClient.PostAsync(TokenUrl, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            var description = root.TryGetProperty("error_description", out var errorDescription)
                ? errorDescription.GetString()
                : null;
            throw new InvalidOperationException(AppText.S(
                $"OAuth token exchange failed: {error.GetString()} {description}",
                $"OAuth token 交换失败：{error.GetString()} {description}"
            ));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(AppText.S(
                $"OAuth token exchange failed: HTTP {(int)response.StatusCode}",
                $"OAuth token 交换失败：HTTP {(int)response.StatusCode}"
            ));
        }

        var accessToken = RequiredString(root, "access_token");
        var refreshToken = RequiredString(root, "refresh_token");
        var idToken = RequiredString(root, "id_token");
        var clientId = TryGetString(root, "client_id") ?? ClientId;
        return new OAuthTokens(accessToken, refreshToken, idToken, clientId, DateTimeOffset.UtcNow);
    }

    private static (string? Code, string? State) ParseCallback(string callbackUrl)
    {
        var uri = new Uri(callbackUrl);
        var query = ParseQuery(uri.Query);
        return (
            query.TryGetValue("code", out var code) ? code : null,
            query.TryGetValue("state", out var state) ? state : null
        );
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        return query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1].Replace("+", " ")),
                StringComparer.Ordinal
            );
    }

    private static string RenderAuthJson(OAuthTokens tokens, OAuthAccountInfo account)
    {
        var payload = new Dictionary<string, object?>
        {
            ["auth_mode"] = "chatgpt",
            ["OPENAI_API_KEY"] = null,
            ["last_refresh"] = tokens.LastRefreshAt.ToString("O"),
            ["client_id"] = tokens.ClientId,
            ["tokens"] = new Dictionary<string, object?>
            {
                ["access_token"] = tokens.AccessToken,
                ["refresh_token"] = tokens.RefreshToken,
                ["id_token"] = tokens.IdToken,
                ["account_id"] = account.RemoteAccountId
            }
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string RenderConfigToml()
    {
        var text = File.Exists(CodexPaths.ConfigTomlPath)
            ? File.ReadAllText(CodexPaths.ConfigTomlPath)
            : "";

        text = UpsertSetting(text, "model_provider", Quote("openai"));
        text = RemoveSetting(text, "oss_provider");
        text = RemoveSetting(text, "preferred_auth_method");
        text = RemoveSetting(text, "openai_base_url");
        text = RemoveBlock(text, "OpenAI");
        text = RemoveBlock(text, "openai");

        return Regex.Replace(text.Trim(), @"\n{3,}", "\n\n") + Environment.NewLine;
    }

    private static string PreferredProfileName(OAuthAccountInfo account)
    {
        if (!string.IsNullOrWhiteSpace(account.Email))
        {
            return account.Email;
        }

        if (!string.IsNullOrWhiteSpace(account.RemoteAccountId))
        {
            return account.RemoteAccountId;
        }

        return "openai-oauth";
    }

    private static string UpsertSetting(string text, string key, string value)
    {
        var line = $"{key} = {value}";
        var pattern = $@"(?m)^{Regex.Escape(key)}\s*=.*$";
        return Regex.IsMatch(text, pattern)
            ? Regex.Replace(text, pattern, line)
            : line + Environment.NewLine + text;
    }

    private static string RemoveSetting(string text, string key) =>
        Regex.Replace(text, $@"(?m)^{Regex.Escape(key)}\s*=.*\r?\n?", "");

    private static string RemoveBlock(string text, string key) =>
        Regex.Replace(text, $@"(?ms)^\[model_providers\.{Regex.Escape(key)}\]\r?\n.*?(?=^\[|\z)", "");

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string RequiredString(JsonElement element, string propertyName)
    {
        var value = TryGetString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(AppText.S(
                $"OAuth token response did not contain {propertyName}.",
                $"OAuth token 响应中没有 {propertyName}。"
            ));
        }

        return value;
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Dictionary<string, object?> DecodeJwtPayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return [];
        }

        try
        {
            var bytes = Base64UrlDecode(parts[1]);
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(
                bytes,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static Dictionary<string, object?> GetObjectClaim(
        Dictionary<string, object?> claims,
        string name
    )
    {
        return claims.TryGetValue(name, out var value) &&
            value is JsonElement { ValueKind: JsonValueKind.Object } element
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText()) ?? []
            : [];
    }

    private static string? GetStringClaim(Dictionary<string, object?> claims, string name)
    {
        if (!claims.TryGetValue(name, out var value))
        {
            return null;
        }

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        };
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        var padding = base64.Length % 4;
        if (padding > 0)
        {
            base64 += new string('=', 4 - padding);
        }

        return Convert.FromBase64String(base64);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed record PendingOAuthFlow(string CodeVerifier, string ExpectedState)
    {
        public static PendingOAuthFlow Create() =>
            new(
                Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
                Guid.NewGuid().ToString("N")
            );
    }

    private sealed record OAuthTokens(
        string AccessToken,
        string RefreshToken,
        string IdToken,
        string ClientId,
        DateTimeOffset LastRefreshAt
    );

    private sealed record OAuthAccountInfo(string Email, string RemoteAccountId)
    {
        public static OAuthAccountInfo FromTokens(OAuthTokens tokens)
        {
            var accessClaims = DecodeJwtPayload(tokens.AccessToken);
            var authClaims = GetObjectClaim(accessClaims, "https://api.openai.com/auth");
            var remoteAccountId = GetStringClaim(authClaims, "chatgpt_account_id")
                ?? GetStringClaim(authClaims, "chatgpt_account_user_id")
                ?? "";

            var idClaims = DecodeJwtPayload(tokens.IdToken);
            var email = GetStringClaim(idClaims, "email") ?? "";
            return new OAuthAccountInfo(email, remoteAccountId);
        }
    }
}

internal sealed record OAuthProfileImportResult(CodexProfile Profile, string Email);

internal sealed class LocalhostOAuthCallbackServer : IDisposable
{
    private const int Port = 1455;
    private const string CallbackPath = "/auth/callback";

    private readonly TcpListener listener = new(IPAddress.Loopback, Port);
    private Task<string>? callbackTask;

    public void Start()
    {
        listener.Start();
        callbackTask = AcceptCallbackAsync();
    }

    public async Task<string> WaitForCallbackAsync(CancellationToken cancellationToken)
    {
        if (callbackTask is null)
        {
            throw new InvalidOperationException("OAuth callback server has not been started.");
        }

        using var registration = cancellationToken.Register(() => listener.Stop());
        try
        {
            return await callbackTask;
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private async Task<string> AcceptCallbackAsync()
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync();
            var callbackUrl = await HandleClientAsync(client);
            if (callbackUrl is not null)
            {
                listener.Stop();
                return callbackUrl;
            }
        }
    }

    private static async Task<string?> HandleClientAsync(TcpClient client)
    {
        await using var stream = client.GetStream();
        var buffer = new byte[8192];
        var read = await stream.ReadAsync(buffer);
        if (read <= 0)
        {
            await WriteResponseAsync(stream, "400 Bad Request", "Invalid callback request.");
            return null;
        }

        var request = Encoding.UTF8.GetString(buffer, 0, read);
        var firstLine = request.Split("\r\n", 2, StringSplitOptions.None).FirstOrDefault();
        var parts = firstLine?.Split(' ');
        if (parts is not { Length: >= 2 } || !string.Equals(parts[0], "GET", StringComparison.Ordinal))
        {
            await WriteResponseAsync(stream, "400 Bad Request", "Invalid callback request.");
            return null;
        }

        var pathAndQuery = parts[1];
        if (!pathAndQuery.StartsWith(CallbackPath, StringComparison.Ordinal))
        {
            await WriteResponseAsync(stream, "404 Not Found", "Callback route not found.");
            return null;
        }

        await WriteResponseAsync(stream, "200 OK", SuccessHtml);
        return $"http://localhost:{Port}{pathAndQuery}";
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = string.Join("\r\n", [
            $"HTTP/1.1 {status}",
            "Content-Type: text/html; charset=utf-8",
            $"Content-Length: {bodyBytes.Length}",
            "Cache-Control: no-store",
            "Connection: close",
            "",
            ""
        ]);
        var headerBytes = Encoding.UTF8.GetBytes(headers);
        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(bodyBytes);
    }

    public void Dispose()
    {
        listener.Stop();
    }

    private const string SuccessHtml = """
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="utf-8">
      <meta name="viewport" content="width=device-width, initial-scale=1">
      <title>Win.Codex.ProfileSwitch Login Received</title>
      <style>
        body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #111; color: #f5f5f5; font-family: "Segoe UI", sans-serif; }
        .card { width: min(92vw, 420px); padding: 28px 24px; border-radius: 8px; background: #1b1b1b; border: 1px solid #2c2c2c; box-shadow: 0 24px 60px rgba(0,0,0,.35); }
        h1 { margin: 0 0 10px; font-size: 22px; }
        p { margin: 0; color: #b7b7b7; line-height: 1.5; }
      </style>
    </head>
    <body>
      <div class="card">
        <h1>Login received</h1>
        <p>Win.Codex.ProfileSwitch captured the localhost callback. You can return to the app now.</p>
      </div>
    </body>
    </html>
    """;
}
