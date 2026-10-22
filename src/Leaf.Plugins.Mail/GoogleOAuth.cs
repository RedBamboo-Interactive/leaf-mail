using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Leaf.Plugins.Mail;

/// <summary>Fixed Google endpoints. Tests substitute the HTTP handler, never these hosts.</summary>
public sealed record GoogleEndpoints(
    string AuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth",
    string TokenUrl = "https://oauth2.googleapis.com/token",
    string RevokeUrl = "https://oauth2.googleapis.com/revoke",
    string GmailBase = "https://gmail.googleapis.com/gmail/v1/users/me",
    string GmailBatchUrl = "https://gmail.googleapis.com/batch/gmail/v1");

/// <summary>Credentials of a Google "Desktop app" OAuth client (non-confidential by design).</summary>
public sealed record DesktopClient(string ClientId, string ClientSecret);

/// <summary>
/// An OAuth failure whose message is safe to persist and show. It carries only the
/// OAuth error code and our own text, never a response body, code, verifier, or token.
/// </summary>
public sealed class MailOAuthException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
    public bool RequiresReauth => Code is "invalid_grant" or "invalid_client" or "unauthorized_client";
}

/// <summary>Raised when Google rejects the stored refresh token; polling stops until reconnect.</summary>
public sealed class MailReauthRequiredException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class MailNotConnectedException() : Exception("Mail is not connected");

public sealed record TokenGrant(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? Scope);

public static class Pkce
{
    /// <summary>RFC 7636 verifier: 64 unreserved characters from 48 random bytes.</summary>
    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(48));

    public static string Challenge(string verifier)
        => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>256 bits of state; verified in constant time against the pending attempt.</summary>
    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static bool StateMatches(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual)) return false;
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(actual);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class GoogleClientJson
{
    public const int MaxBytes = 16 * 1024;
    private static readonly Regex ClientIdShape = new(
        @"^[0-9]{6,20}-[a-z0-9]{8,64}\.apps\.googleusercontent\.com$", RegexOptions.Compiled);
    private static readonly Regex SecretShape = new(@"^[A-Za-z0-9_\-]{10,128}$", RegexOptions.Compiled);

    /// <summary>
    /// Parses a downloaded Google client JSON. Only the "installed" (Desktop app) shape is
    /// accepted: a "web" client would need a public redirect and a confidential secret.
    /// </summary>
    public static DesktopClient Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxBytes)
            throw new ArgumentException("Client JSON is empty or too large");
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { throw new ArgumentException("Client JSON is not valid JSON"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Client JSON must be an object");
            var keys = root.EnumerateObject().Select(p => p.Name).ToArray();
            if (keys.Length != 1 || keys[0] != "installed")
                throw new ArgumentException(keys is ["web"]
                    ? "This is a Web application client. Create a Desktop app OAuth client instead."
                    : "Client JSON must contain exactly one \"installed\" (Desktop app) client");
            var installed = root.GetProperty("installed");
            if (installed.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("\"installed\" must be an object");
            var clientId = Str(installed, "client_id");
            var secret = Str(installed, "client_secret");
            if (clientId == null || !ClientIdShape.IsMatch(clientId))
                throw new ArgumentException("installed.client_id is missing or is not a Google OAuth client id");
            if (secret == null || !SecretShape.IsMatch(secret))
                throw new ArgumentException("installed.client_secret is missing or malformed");
            if (Str(installed, "token_uri") is { } tokenUri && tokenUri != "https://oauth2.googleapis.com/token")
                throw new ArgumentException("installed.token_uri is not Google's token endpoint");
            if (Str(installed, "auth_uri") is { } authUri && authUri != "https://accounts.google.com/o/oauth2/auth")
                throw new ArgumentException("installed.auth_uri is not Google's authorization endpoint");
            return new DesktopClient(clientId, secret);
        }
    }

    private static string? Str(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}

public static class GoogleScopes
{
    /// <summary>The granted set must be exactly gmail.readonly: extra or missing scopes fail closed.</summary>
    public static bool IsExactlyReadonly(string? granted)
    {
        if (string.IsNullOrWhiteSpace(granted)) return false;
        var scopes = granted.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        return scopes.Length == 1 && scopes[0] == MailData.ReadonlyScope;
    }
}

/// <summary>Authorization-code + PKCE client for Google's token endpoint.</summary>
public sealed class GoogleOAuthClient(HttpClient http, GoogleEndpoints endpoints)
{
    public string BuildAuthorizationUrl(DesktopClient client, string redirectUri, string state, string codeChallenge)
    {
        // include_granted_scopes is deliberately absent so no earlier grant widens this token.
        var query = new (string Key, string Value)[]
        {
            ("client_id", client.ClientId),
            ("redirect_uri", redirectUri),
            ("response_type", "code"),
            ("scope", MailData.ReadonlyScope),
            ("access_type", "offline"),
            ("prompt", "consent"),
            ("state", state),
            ("code_challenge", codeChallenge),
            ("code_challenge_method", "S256"),
        };
        return endpoints.AuthorizeUrl + "?" + string.Join("&",
            query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
    }

    public Task<TokenGrant> ExchangeCodeAsync(
        DesktopClient client, string code, string verifier, string redirectUri, CancellationToken ct)
        => PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = client.ClientId,
            ["client_secret"] = client.ClientSecret,
        }, ct);

    public Task<TokenGrant> RefreshAsync(DesktopClient client, string refreshToken, CancellationToken ct)
        => PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = client.ClientId,
            ["client_secret"] = client.ClientSecret,
        }, ct);

    /// <summary>Best-effort revocation. Returns false on any failure; never throws token text.</summary>
    public async Task<bool> RevokeAsync(string token, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
            using var response = await http.PostAsync(endpoints.RevokeUrl, content, ct);
            // 400 invalid_token means it is already unusable, which is the goal.
            return response.IsSuccessStatusCode || (int)response.StatusCode == 400;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<TokenGrant> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            response = await http.PostAsync(endpoints.TokenUrl, content, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new MailOAuthException("unreachable", "Google's token endpoint could not be reached");
        }

        using (response)
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var code = OAuthErrorCode(json) ?? $"http_{(int)response.StatusCode}";
                throw new MailOAuthException(code, code switch
                {
                    "invalid_grant" => "Google rejected the authorization (invalid_grant). Reconnect in Settings → Mail.",
                    "invalid_client" => "Google rejected the OAuth client (invalid_client). Upload the Desktop client JSON again.",
                    "unauthorized_client" => "The OAuth client is not allowed this grant (unauthorized_client).",
                    _ => $"Google token request failed ({code}).",
                });
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
                if (string.IsNullOrEmpty(access))
                    throw new MailOAuthException("malformed_response", "Google returned no access token");
                var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds)
                    ? seconds : 3600;
                return new TokenGrant(
                    access,
                    root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
                    DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(expiresIn, 60, 86_400)),
                    root.TryGetProperty("scope", out var s) ? s.GetString() : null);
            }
            catch (JsonException)
            {
                throw new MailOAuthException("malformed_response", "Google returned an unreadable token response");
            }
        }
    }

    private static readonly Regex ErrorCode = new("^[a-z_]{1,40}$", RegexOptions.Compiled);

    /// <summary>Extracts only a well-formed OAuth error code; free text is discarded.</summary>
    internal static string? OAuthErrorCode(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString() is { } code && ErrorCode.IsMatch(code))
                return code;
        }
        catch (JsonException) { }
        return null;
    }
}
