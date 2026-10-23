using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Mail;

/// <summary>
/// Entity access for the <c>mail-config</c> singleton and the <c>mail-account</c>.
///
/// Both entities are kernel-confidential (<c>confidential</c>, <c>owner_id</c>,
/// <c>owner_agent_id</c>). The kernel hides them from entity lists and admits direct reads
/// and mutations only from the explicit human owner or an owning-agent execution, so
/// LocalDefault and other admins can neither read nor PATCH them. The kernel does not
/// restrict what the owner or the owning agent writes, though, so Mail does not trust a
/// stored binding blindly: authorization comes from the kernel fields, they must agree
/// with Mail's own copies, and the binding Mail last established is pinned in memory. A
/// change made outside Mail is reverted to the pin; with no pin (fresh process) an
/// inconsistent or non-confidential binding fails closed.
/// </summary>
public sealed class MailStore(IEntityStore entities, ILogger<MailStore>? logger = null)
{
    private static readonly string[] BindingKeys = ["confidential", "owner_id", "owner_user_id", "owner_agent_id", "agent_id"];
    private static readonly string[] ApprovalKeys = ["live_approved", "live_approval_id", "live_approved_by", "live_approved_at"];
    private readonly SemaphoreSlim _configGate = new(1, 1);
    private readonly SemaphoreSlim _bindingGate = new(1, 1);
    private MailPolicy? _pinned;
    private LiveApproval? _pinnedApproval;

    public IEntityStore Entities => entities;

    /// <summary>How many times a binding changed outside Mail was detected and reverted.</summary>
    public int BindingRestoreCount { get; private set; }

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
            // Confidential from birth; with no owner yet the kernel admits nobody.
            var created = await entities.UpsertBySlugAsync(MailData.ConfigType, MailData.ConfigSlug, "Mail Config",
                new JsonObject
                {
                    ["confidential"] = true,
                    // No live_approved: new-mail LIVE notices stay off until the owner approves them.
                    ["live_excluded_categories"] = new JsonArray(MailData.DefaultLiveExcludedCategories
                        .Select(c => (JsonNode)JsonValue.Create(c)).ToArray()),
                }, ct);
            return ConfigFrom(created);
        }
        finally { _configGate.Release(); }
    }

    public Task<MailConfigSnapshot> GetConfigAsync(CancellationToken ct = default) => EnsureConfigAsync(ct);

    /// <summary>Non-binding settings only (client, LIVE filters). Binding and approval keys are rejected.</summary>
    public async Task<MailConfigSnapshot> PatchConfigAsync(JsonObject patch, CancellationToken ct = default)
    {
        if (BindingKeys.Any(patch.ContainsKey) || ApprovalKeys.Any(patch.ContainsKey))
            throw new InvalidOperationException("Binding and LIVE approval fields change only through their own API");
        var config = await EnsureConfigAsync(ct);
        return ConfigFrom(await entities.PatchAsync(config.Entity.Id, patch, ct: ct));
    }

    /// <summary>
    /// The verified owner/agent binding used for every authorization decision. Reverts a
    /// binding changed outside Mail to the pinned one; fails closed with no pin.
    /// </summary>
    public async Task<MailPolicy> GetPolicyAsync(CancellationToken ct = default)
    {
        var config = await EnsureConfigAsync(ct);
        var pinned = _pinned;
        if (pinned != null && config.BindingIntact && new MailPolicy(config.OwnerUserId, config.AgentId) == pinned)
        {
            await EnsureAccountBindingAsync(pinned, ct);
            return pinned;
        }

        await _bindingGate.WaitAsync(ct);
        try
        {
            pinned = _pinned;
            config = await EnsureConfigAsync(ct);
            var current = new MailPolicy(config.OwnerUserId, config.AgentId);
            if (pinned == null)
            {
                if (!config.BindingIntact)
                {
                    logger?.LogError("Mail binding is inconsistent or not confidential; refusing all callers");
                    throw new MailFault("mail_binding_untrusted", 409);
                }
                _pinned = pinned = current;
            }
            else if (!config.BindingIntact || current != pinned)
            {
                BindingRestoreCount++;
                logger?.LogWarning("Mail binding was changed outside Mail; restoring the pinned owner and agent");
                await WriteBindingAsync(config, pinned, ct);
            }
            await EnsureAccountBindingAsync(pinned, ct);
            return pinned;
        }
        finally { _bindingGate.Release(); }
    }

    /// <summary>
    /// Atomically claims an unowned Mail (compare-and-set on the config revision). Only an
    /// admin may make the first claim; afterwards only the owner passes.
    /// </summary>
    public async Task<MailPolicy> ClaimOwnerAsync(string userId, bool isAdmin, CancellationToken ct = default)
    {
        var policy = await GetPolicyAsync(ct);
        if (policy.OwnerUserId != null)
            return SameGuid(policy.OwnerUserId, userId) ? policy : throw new MailFault("owner_required", 403);
        if (!isAdmin) throw new MailFault("admin_required_for_first_claim", 403);

        await _bindingGate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < 3 && _pinned?.OwnerUserId == null; attempt++)
            {
                var config = await EnsureConfigAsync(ct);
                if (config.OwnerUserId != null) break;
                var data = config.Entity.Data.DeepClone().AsObject();
                data["confidential"] = true;
                data["owner_id"] = userId;
                data["owner_user_id"] = userId;
                data["owner_agent_id"] = null;
                data["agent_id"] = null;
                if (await entities.CompareExchangeBySlugAsync(MailData.ConfigType, MailData.ConfigSlug,
                        config.Entity.Name, data, config.Entity.UpdatedAt, ct) != null)
                    _pinned = new MailPolicy(userId, null);
            }
            var owner = _pinned?.OwnerUserId;
            if (owner == null) throw new MailFault("owner_claim_conflict", 409);
            if (!SameGuid(owner, userId)) throw new MailFault("owner_required", 403);
            await EnsureAccountBindingAsync(_pinned!, ct);
            return _pinned!;
        }
        finally { _bindingGate.Release(); }
    }

    /// <summary>Binds (or, with null, unbinds) the agent. The caller must already be the owner.</summary>
    /// <summary>Returns the agent bound before this change, read inside the binding gate so
    /// concurrent rebinds each see the binding they actually replaced.</summary>
    public async Task<(string? Previous, MailPolicy Next)> SetAgentAsync(string? agentId, CancellationToken ct = default)
    {
        await GetPolicyAsync(ct);
        await _bindingGate.WaitAsync(ct);
        try
        {
            var policy = _pinned ?? throw new MailFault("mail_binding_untrusted", 409);
            if (policy.OwnerUserId == null) throw new MailFault("owner_required", 403);
            var next = policy with { AgentId = agentId };
            await WriteBindingAsync(await EnsureConfigAsync(ct), next, ct);
            _pinned = next;
            await EnsureAccountBindingAsync(next, ct);
            return (policy.AgentId, next);
        }
        finally { _bindingGate.Release(); }
    }

    /// <summary>
    /// The effective LIVE approval: the one Mail last wrote (pinned), reverting an approval
    /// changed outside Mail (for instance an agent PATCHing <c>live_approved</c> on), and
    /// valid only when approved by the current owner. Off by default.
    /// </summary>
    public async Task<LiveApproval> GetLiveApprovalAsync(CancellationToken ct = default)
    {
        var policy = await GetPolicyAsync(ct);
        var stored = (await EnsureConfigAsync(ct)).LiveApproval;
        var pinned = _pinnedApproval;
        if (pinned == null || stored != pinned)
        {
            await _bindingGate.WaitAsync(ct);
            try
            {
                pinned = _pinnedApproval;
                stored = (await EnsureConfigAsync(ct)).LiveApproval;
                if (pinned == null) _pinnedApproval = pinned = stored;
                else if (stored != pinned)
                {
                    BindingRestoreCount++;
                    logger?.LogWarning("Mail LIVE approval was changed outside Mail; restoring the owner's setting");
                    await WriteApprovalAsync(pinned, ct);
                }
            }
            finally { _bindingGate.Release(); }
        }
        return pinned.ValidFor(policy.OwnerUserId) ? pinned : LiveApproval.Off;
    }

    /// <summary>Owner-only toggle. Every change mints a new approval id (as Body does).</summary>
    public async Task<LiveApproval> SetLiveApprovalAsync(bool enabled, string ownerUserId, CancellationToken ct = default)
    {
        var policy = await GetPolicyAsync(ct);
        if (policy.OwnerUserId == null || !SameGuid(policy.OwnerUserId, ownerUserId))
            throw new MailFault("owner_required", 403);
        var approval = new LiveApproval(enabled, Guid.NewGuid().ToString("N"), policy.OwnerUserId,
            DateTimeOffset.UtcNow.ToString("O"));
        await _bindingGate.WaitAsync(ct);
        try
        {
            await WriteApprovalAsync(approval, ct);
            _pinnedApproval = approval;
        }
        finally { _bindingGate.Release(); }
        return approval;
    }

    private async Task WriteApprovalAsync(LiveApproval approval, CancellationToken ct)
        => await entities.PatchAsync((await EnsureConfigAsync(ct)).Entity.Id, new JsonObject
        {
            ["live_approved"] = approval.Enabled,
            ["live_approval_id"] = approval.ApprovalId,
            ["live_approved_by"] = approval.ApprovedBy,
            ["live_approved_at"] = approval.ApprovedAt,
        }, ct: ct);

    private Task WriteBindingAsync(MailConfigSnapshot config, MailPolicy binding, CancellationToken ct)
        => entities.PatchAsync(config.Entity.Id, new JsonObject
        {
            ["confidential"] = true,
            ["owner_id"] = binding.OwnerUserId,
            ["owner_user_id"] = binding.OwnerUserId,
            ["owner_agent_id"] = binding.AgentId,
            ["agent_id"] = binding.AgentId,
        }, ct: ct);

    private async Task EnsureAccountBindingAsync(MailPolicy binding, CancellationToken ct)
    {
        var account = await entities.GetBySlugAsync(MailData.AccountType, MailData.AccountSlug, ct);
        if (account == null) return;
        var data = account.Data;
        if (MailData.Boolean(data, "confidential")
            && MailData.String(data, "owner_id") == binding.OwnerUserId
            && MailData.String(data, "owner_agent_id") == binding.AgentId)
            return;
        await entities.PatchAsync(account.Id, AccountOwnership(binding), ct: ct);
    }

    private static JsonObject AccountOwnership(MailPolicy binding) => new()
    {
        ["confidential"] = true,
        ["owner_id"] = binding.OwnerUserId,
        ["owner_agent_id"] = binding.AgentId,
    };

    public async Task<MailAccountSnapshot?> GetAccountAsync(CancellationToken ct = default)
    {
        var entity = await entities.GetBySlugAsync(MailData.AccountType, MailData.AccountSlug, ct);
        return entity == null ? null : AccountFrom(entity);
    }

    /// <summary>Creates or patches the account; it always carries the confidential owner/agent binding.</summary>
    public async Task<MailAccountSnapshot> UpsertAccountAsync(JsonObject patch, CancellationToken ct = default)
    {
        var binding = await GetPolicyAsync(ct);
        var data = patch.DeepClone().AsObject();
        foreach (var (key, value) in AccountOwnership(binding)) data[key] = value?.DeepClone();
        var existing = await entities.GetBySlugAsync(MailData.AccountType, MailData.AccountSlug, ct);
        var entity = existing == null
            ? await entities.UpsertBySlugAsync(MailData.AccountType, MailData.AccountSlug, "Gmail Account", data, ct)
            : await entities.PatchAsync(existing.Id, data, ct: ct);
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
        var owner = MailData.String(data, "owner_id");
        var agent = MailData.String(data, "owner_agent_id");
        var intact = MailData.Boolean(data, "confidential")
            && owner == MailData.String(data, "owner_user_id")
            && agent == MailData.String(data, "agent_id")
            && (owner == null || Guid.TryParse(owner, out _))
            && (agent == null || Guid.TryParse(agent, out _));
        return new MailConfigSnapshot(
            entity,
            owner,
            agent,
            MailData.String(data, "client_id"),
            MailData.String(data, "client_secret"),
            new LiveApproval(
                MailData.Boolean(data, "live_approved"),
                MailData.String(data, "live_approval_id"),
                MailData.String(data, "live_approved_by"),
                MailData.String(data, "live_approved_at")),
            excluded.ToHashSet(StringComparer.Ordinal),
            intact);
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

    /// <summary>Drops the cached access token (async: never blocks a thread on the refresh gate).</summary>
    public async Task InvalidateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
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
