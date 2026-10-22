using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Mail;

/// <summary>Entity access for the <c>mail-config</c> singleton and the <c>mail-account</c>.</summary>
public sealed class MailStore(IEntityStore entities)
{
    private readonly SemaphoreSlim _configGate = new(1, 1);

    public IEntityStore Entities => entities;

    /// <summary>Created once at startup (never seeded: seeds re-upsert on every boot).</summary>
    public async Task<MailConfigSnapshot> EnsureConfigAsync(CancellationToken ct = default)
    {
        var existing = await entities.GetBySlugAsync(MailData.ConfigType, MailData.ConfigSlug, ct);
        if (existing != null) return ConfigFrom(existing);
        await _configGate.WaitAsync(ct);
        try
        {
            existing = await entities.GetBySlugAsync(MailData.ConfigType, MailData.ConfigSlug, ct);
            if (existing != null) return ConfigFrom(existing);
            var created = await entities.UpsertBySlugAsync(MailData.ConfigType, MailData.ConfigSlug, "Mail Config",
                new JsonObject
                {
                    ["live_enabled"] = true,
                    ["live_excluded_categories"] = new JsonArray(MailData.DefaultLiveExcludedCategories
                        .Select(c => (JsonNode)JsonValue.Create(c)).ToArray()),
                }, ct);
            return ConfigFrom(created);
        }
        finally { _configGate.Release(); }
    }

    public Task<MailConfigSnapshot> GetConfigAsync(CancellationToken ct = default) => EnsureConfigAsync(ct);

    public async Task<MailConfigSnapshot> PatchConfigAsync(JsonObject patch, CancellationToken ct = default)
    {
        var config = await EnsureConfigAsync(ct);
        return ConfigFrom(await entities.PatchAsync(config.Entity.Id, patch, ct: ct));
    }

    public async Task<MailAccountSnapshot?> GetAccountAsync(CancellationToken ct = default)
    {
        var entity = await entities.GetBySlugAsync(MailData.AccountType, MailData.AccountSlug, ct);
        return entity == null ? null : AccountFrom(entity);
    }

    public async Task<MailAccountSnapshot> UpsertAccountAsync(JsonObject patch, CancellationToken ct = default)
    {
        var existing = await entities.GetBySlugAsync(MailData.AccountType, MailData.AccountSlug, ct);
        var entity = existing == null
            ? await entities.UpsertBySlugAsync(MailData.AccountType, MailData.AccountSlug, "Gmail Account", patch, ct)
            : await entities.PatchAsync(existing.Id, patch, ct: ct);
        return AccountFrom(entity);
    }

    public async Task<MailAccountSnapshot> PatchAccountAsync(MailAccountSnapshot account, JsonObject patch, CancellationToken ct = default)
        => AccountFrom(await entities.PatchAsync(account.Entity.Id, patch, ct: ct));

    /// <summary>True only for a Nova discussion entity owned by exactly this owner and agent.</summary>
    public async Task<bool> IsOwnedNovaDiscussionAsync(string discussionId, string ownerUserId, string agentId, CancellationToken ct)
    {
        var rows = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = "discussion",
            DataEquals = new Dictionary<string, object?> { ["discussion_id"] = discussionId },
            Limit = 2,
        }, ct);
        if (rows.Count != 1) return false;
        var data = rows[0].Data;
        return MailData.String(data, "discussion_id") == discussionId
            && MailData.String(data, "app") == "nova"
            && SameGuid(MailData.String(data, "owner_id"), ownerUserId)
            && SameGuid(MailData.String(data, "agent"), agentId);
    }

    public async Task<bool> IsAgentAsync(string agentId, CancellationToken ct)
        => Guid.TryParse(agentId, out var id) && (await entities.GetAsync(id, ct))?.TypeSlug == "agent";

    private static bool SameGuid(string? a, string? b)
        => Guid.TryParse(a, out var x) && Guid.TryParse(b, out var y) && x == y;

    internal static MailConfigSnapshot ConfigFrom(LeafEntity entity)
    {
        var data = entity.Data;
        var excluded = data["live_excluded_categories"] is JsonArray
            ? MailData.StringList(data, "live_excluded_categories")
            : MailData.DefaultLiveExcludedCategories;
        return new MailConfigSnapshot(
            entity,
            MailData.String(data, "owner_user_id"),
            MailData.String(data, "agent_id"),
            MailData.String(data, "client_id"),
            MailData.String(data, "client_secret"),
            MailData.Boolean(data, "live_enabled", true),
            excluded.ToHashSet(StringComparer.Ordinal));
    }

    internal static MailAccountSnapshot AccountFrom(LeafEntity entity)
    {
        var data = entity.Data;
        return new MailAccountSnapshot(
            entity,
            MailData.String(data, "email"),
            MailData.String(data, "status") ?? MailStatus.Disconnected,
            MailData.String(data, "refresh_token"),
            MailData.String(data, "history_id"),
            MailData.String(data, "token_health"),
            MailData.Timestamp(data, "last_sync_at"),
            MailData.String(data, "last_error"),
            MailData.String(data, "granted_scope"),
            MailData.Long(data, "inbox_unread"),
            MailData.Boolean(data, "backfill_complete"));
    }
}

/// <summary>
/// In-memory access-token cache. Refreshes under a semaphore five minutes before expiry
/// (one refresh in flight, concurrent callers reuse it). invalid_grant and other
/// non-retryable OAuth failures surface as <see cref="MailReauthRequiredException"/>.
/// </summary>
public sealed class MailTokenCache(GoogleOAuthClient oauth, MailStore store, TimeProvider? clock = null) : IMailAccessTokens
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private string? _mintedFrom;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public int RefreshCount { get; private set; }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var account = await store.GetAccountAsync(ct);
        if (account is not { IsConnected: true }) throw new MailNotConnectedException();
        if (Reusable(account.RefreshToken)) return _accessToken!;

        await _gate.WaitAsync(ct);
        try
        {
            account = await store.GetAccountAsync(ct);
            if (account is not { IsConnected: true }) throw new MailNotConnectedException();
            if (Reusable(account.RefreshToken)) return _accessToken!;
            var config = await store.GetConfigAsync(ct);
            if (!config.ClientConfigured) throw new MailReauthRequiredException("client_missing",
                "The Google OAuth client is not configured. Upload the Desktop client JSON in Settings → Mail.");

            TokenGrant grant;
            try
            {
                grant = await oauth.RefreshAsync(new DesktopClient(config.ClientId!, config.ClientSecret!),
                    account.RefreshToken!, ct);
            }
            catch (MailOAuthException ex) when (ex.RequiresReauth)
            {
                InvalidateLocked();
                throw new MailReauthRequiredException(ex.Code, ex.Message);
            }
            RefreshCount++;
            if (grant.Scope != null && !GoogleScopes.IsExactlyReadonly(grant.Scope))
            {
                InvalidateLocked();
                throw new MailReauthRequiredException("scope_mismatch",
                    "Google returned a token whose scopes are not exactly gmail.readonly. Reconnect in Settings → Mail.");
            }
            var refreshToken = account.RefreshToken!;
            if (grant.RefreshToken is { Length: > 0 } rotated && rotated != refreshToken)
            {
                await store.PatchAccountAsync(account, new JsonObject { ["refresh_token"] = rotated }, ct);
                refreshToken = rotated;
            }
            _accessToken = grant.AccessToken;
            _expiresAt = grant.ExpiresAt;
            _mintedFrom = refreshToken;
            return grant.AccessToken;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Seeds the cache with the access token returned by the consent exchange.</summary>
    public async Task PrimeAsync(TokenGrant grant, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _accessToken = grant.AccessToken;
            _expiresAt = grant.ExpiresAt;
            _mintedFrom = grant.RefreshToken;
        }
        finally { _gate.Release(); }
    }

    public void Invalidate()
    {
        _gate.Wait();
        try { InvalidateLocked(); }
        finally { _gate.Release(); }
    }

    private void InvalidateLocked()
    {
        _accessToken = null;
        _mintedFrom = null;
        _expiresAt = DateTimeOffset.MinValue;
    }

    private bool Reusable(string? refreshToken)
        => _accessToken != null && refreshToken != null && _mintedFrom == refreshToken
            && _expiresAt - RefreshSkew > _clock.GetUtcNow();
}
