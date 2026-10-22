using System.Globalization;
using System.Text.Json.Nodes;
using Leaf.Sdk;

namespace Leaf.Plugins.Mail;

internal static class MailData
{
    public const string ConfigType = "mail-config";
    public const string ConfigSlug = "mail-config-singleton";
    public const string AccountType = "mail-account";
    public const string AccountSlug = "mail-account-primary";
    public const string MessagesStream = "messages";

    public const string ReadonlyScope = "https://www.googleapis.com/auth/gmail.readonly";

    public static readonly string[] DefaultLiveExcludedCategories =
        ["CATEGORY_PROMOTIONS", "CATEGORY_SOCIAL", "CATEGORY_FORUMS"];

    public static readonly string[] KnownCategories =
        ["CATEGORY_PROMOTIONS", "CATEGORY_SOCIAL", "CATEGORY_FORUMS", "CATEGORY_UPDATES", "CATEGORY_PERSONAL"];

    public static string? String(JsonObject? data, string key)
    {
        if (data?[key] is not JsonValue value || !value.TryGetValue<string>(out var result)) return null;
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    public static bool Boolean(JsonObject? data, string key, bool fallback = false)
    {
        if (data?[key] is not JsonValue value) return fallback;
        if (value.TryGetValue<bool>(out var result)) return result;
        return value.TryGetValue<string>(out var text) && bool.TryParse(text, out result) ? result : fallback;
    }

    public static long? Long(JsonObject? data, string key)
    {
        if (data?[key] is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text))
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        // Covers element-backed values (from storage) and in-process int/long/double values alike.
        return value.GetValueKind() == System.Text.Json.JsonValueKind.Number
            && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? (long)number : null;
    }

    public static DateTimeOffset? Timestamp(JsonObject? data, string key)
        => DateTimeOffset.TryParse(String(data, key), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var value) ? value : null;

    public static IReadOnlyList<string> StringList(JsonObject? data, string key)
        => data?[key] is JsonArray array
            ? array.OfType<JsonValue>()
                .Select(v => v.TryGetValue<string>(out var s) ? s : null)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToArray()
            : [];

    /// <summary>"user@example.com" → "user@…". Context and LIVE never carry the full address.</summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "unknown";
        var at = email.IndexOf('@');
        if (at <= 0) return "…";
        var local = email[..at];
        return (local.Length > 24 ? local[..24] : local) + "@…";
    }
}

/// <summary>Account status values persisted on the <c>mail-account</c> entity.</summary>
internal static class MailStatus
{
    public const string Connected = "connected";
    public const string NeedsReauth = "needs_reauth";
    public const string Disconnected = "disconnected";
}

internal static class MailEvents
{
    /// <summary>Invalidation only: the payload is an opaque nonce. Content never enters
    /// the plugin bus or its <c>os:events</c> mirror.</summary>
    public const string LivePending = "mail.live.pending";
}

public sealed record MailConfigSnapshot(
    LeafEntity Entity,
    string? OwnerUserId,
    string? AgentId,
    string? ClientId,
    string? ClientSecret,
    bool LiveEnabled,
    IReadOnlySet<string> LiveExcludedCategories)
{
    public bool ClientConfigured => ClientId != null && ClientSecret != null;
}

public sealed record MailAccountSnapshot(
    LeafEntity Entity,
    string? Email,
    string Status,
    string? RefreshToken,
    string? HistoryId,
    string? TokenHealth,
    DateTimeOffset? LastSyncAt,
    string? LastError,
    string? GrantedScope,
    long? InboxUnread,
    bool BackfillComplete)
{
    public bool IsConnected => Status == MailStatus.Connected && RefreshToken != null;
}
