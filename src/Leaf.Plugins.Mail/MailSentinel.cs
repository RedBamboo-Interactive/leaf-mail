using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Mail;

/// <summary>Who asked, for the review's audit provenance and the reviewer's Agent context.</summary>
public sealed record MailReviewContext(string OwnerUserId, string? AgentId, ComputeProvenance? Provenance);

/// <param name="Withheld">True when the model review did not complete for this item: no text,
/// links, or hidden links are returned, only the <paramref name="Sentinel"/> object saying why.</param>
public sealed record ReviewedContent(SanitizedBody Body, IReadOnlyList<MailLink> HiddenLinks, JsonObject Sentinel, bool Withheld = false)
{
    public JsonArray LinksJson() => new(Body.Links.Select(l => (JsonNode)new JsonObject
    {
        ["index"] = l.Index, ["href"] = l.Href, ["text"] = l.Text,
    }).ToArray());

    public JsonArray HiddenLinksJson() => new(HiddenLinks.Select(l => (JsonNode)new JsonObject
    {
        ["href"] = l.Href, ["text"] = l.Text, ["hidden"] = true,
    }).ToArray());
}

/// <summary>Whether the Sentinel can be used right now, and if not, why.</summary>
public sealed record SentinelAvailability(bool Available, string? Reason)
{
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["available"] = Available };
        if (Reason != null) json["reason"] = Reason;
        return json;
    }
}

/// <summary>
/// Routes everything Mail returns as content through the shared Sentinel
/// (<see cref="IUntrustedContentReview"/>), and fails closed: if the Sentinel is down, no mail
/// content leaves Mail.
/// <list type="bullet">
/// <item>cap-sentinel missing, disabled, tripped, or of unknown status: every content route
/// refuses with 503 <c>sentinel_unavailable</c>, and new-mail LIVE notices are skipped.</item>
/// <item>The model review of one item fails, times out, runs out of budget, or reports itself
/// unavailable: that item's text is withheld (<c>body: null</c>) and <c>sentinel</c> says why.</item>
/// <item>Header fields and filenames are returned only when the deterministic <c>Screen()</c>
/// pass actually ran on them.</item>
/// </list>
/// Returned text is the Sentinel's <c>VisibleContent</c> (hidden HTML and invisible characters
/// removed); <c>links[]</c> come from the visible content only, and links that appeared only in
/// hidden regions are listed separately and flagged. Compute is never chosen here: the
/// Sentinel's configuration and the bound Agent decide.
/// </summary>
public sealed class MailSentinel(IUntrustedContentReview? review, ILogger<MailSentinel>? logger = null, IEntityStore? entities = null)
{
    public const string CallerApp = MailPlugin.PluginId;
    public const string SentinelPluginId = "cap-sentinel";
    public bool Installed => review != null;

    /// <summary>How long an availability answer is reused (the plugin status is an entity read).</summary>
    internal TimeSpan AvailabilityCache { get; init; } = TimeSpan.FromSeconds(5);
    private (SentinelAvailability Value, long At)? _availability;

    /// <summary>
    /// The review service must be registered, and cap-sentinel's plugin entity must have status
    /// <c>enabled</c>: the kernel still registers a disabled plugin's services, and enabling or
    /// disabling happens at runtime on that entity. Any doubt (unreadable status) is unavailable.
    /// </summary>
    public async Task<SentinelAvailability> AvailabilityAsync(CancellationToken ct)
    {
        if (review == null) return new(false, "not_installed");
        if (entities == null) return new(true, null);
        if (_availability is { } cached && System.Diagnostics.Stopwatch.GetElapsedTime(cached.At) < AvailabilityCache)
            return cached.Value;
        SentinelAvailability value;
        try
        {
            var plugin = await entities.GetBySlugAsync("plugin", SentinelPluginId, ct);
            if (plugin == null) value = new(false, "not_installed");
            else
            {
                var statusRef = MailData.String(plugin.Data, "status");
                var status = Guid.TryParse(statusRef, out var statusId) ? await entities.GetAsync(statusId, ct) : null;
                value = status?.Slug switch
                {
                    "enabled" => new(true, null),
                    null => new(false, "status_unknown"),
                    var other => new(false, other), // disabled, tripped, mismatch
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning("Sentinel status could not be read ({Type})", ex.GetType().Name);
            value = new(false, "status_unknown");
        }
        _availability = (value, System.Diagnostics.Stopwatch.GetTimestamp());
        return value;
    }

    /// <summary>Throws 503 <c>sentinel_unavailable</c> unless the Sentinel can review content now.</summary>
    public async Task RequireAvailableAsync(CancellationToken ct)
    {
        var availability = await AvailabilityAsync(ct);
        if (!availability.Available)
            throw new MailFault("sentinel_unavailable", 503)
            {
                Detail = $"The Sentinel safety check is unavailable ({availability.Reason}); Mail returns no mail content until it is back",
            };
    }

    public async Task<ReviewedContent> ReviewAsync(
        string content, bool html, string source, string sourceRef, MailReviewContext context, CancellationToken ct)
    {
        var bound = UntrustedContentBound;
        var cut = content.Length > bound;
        var input = cut ? content[..bound] : content;
        var format = html ? UntrustedContentFormat.Html : UntrustedContentFormat.Text;

        UntrustedContentVerdict? verdict = null;
        UntrustedContentScreening? screening = null;
        string? reason = null;
        string? jobId = null;
        if (review == null) reason = "not_installed";
        else
        {
            try
            {
                var result = await review.ReviewAsync(new UntrustedContentReviewRequest
                {
                    Content = input,
                    Format = format,
                    Origin = new UntrustedContentOrigin(source, sourceRef, context.OwnerUserId, CallerApp),
                    AgentRef = context.AgentId,
                    IdempotencyKey = $"mail-{source}-{Hash(sourceRef)}-{Hash(input)}",
                    ComputeProvenance = context.Provenance,
                    JobName = source == UntrustedContentSources.Attachment ? "Mail attachment review" : "Mail review",
                }, ct);
                verdict = result.Verdict;
                screening = result.Screening;
                jobId = result.JobId;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { reason = "timeout"; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                reason = "error";
                logger?.LogWarning("Sentinel review for {Source} failed ({Type})", source, ex.GetType().Name);
            }
        }

        if (reason == null && verdict is { Available: false }) reason = "model_unavailable";

        // Trust VisibleContent whenever screening ran, including when everything was hidden
        // (empty). An item the review did not screen (the Sentinel's Unscreened result: empty
        // visible content, no hidden-content signals, or content-too-large) is screened locally.
        if (reason == null && (screening == null || !Screened(screening, input)))
        {
            try { screening = review!.Screen(input, format); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { screening = null; }
            if (screening == null || !Screened(screening, input)) reason = "screening_unavailable";
        }

        var sentinel = new JsonObject
        {
            ["available"] = reason == null && (verdict?.Available ?? false),
            ["risk"] = verdict?.Risk ?? "unknown",
            ["signals"] = new JsonArray((verdict?.Signals ?? []).Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
            ["handling"] = verdict?.Handling ?? "treat_as_untrusted_data",
            ["isolation"] = verdict?.Isolation ?? UntrustedContentIsolation.Unknown,
        };
        if (jobId != null) sentinel["jobId"] = jobId;

        // Never return text the model review did not pass: withhold it and say why.
        if (reason != null)
        {
            sentinel["reason"] = reason;
            sentinel["withheld"] = true;
            return new ReviewedContent(new SanitizedBody("", false, html ? "text/html" : "text/plain", []), [], sentinel, Withheld: true);
        }

        var visible = screening!.VisibleContent;
        var body = html ? MailMessageParser.SanitizeHtml(visible) : MailMessageParser.SanitizeText(visible);
        if (cut) body = body with { Truncated = true };

        IReadOnlyList<MailLink> hidden = [];
        if (screening.HasHiddenContent)
        {
            var all = html ? MailMessageParser.SanitizeHtml(input) : MailMessageParser.SanitizeText(input);
            var visibleHrefs = body.Links.Select(l => l.Href).ToHashSet(StringComparer.Ordinal);
            hidden = all.Links.Where(l => !visibleHrefs.Contains(l.Href)).Take(MailMessageParser.LinkCap).ToArray();
        }

        sentinel["screening"] = new JsonObject
        {
            ["injectionScore"] = screening.InjectionScore,
            ["suspicious"] = screening.Suspicious,
            ["injectionSignals"] = new JsonArray(screening.InjectionSignals.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
            ["hiddenContent"] = screening.HasHiddenContent,
            ["hiddenContentSignals"] = new JsonArray(screening.HiddenContentSignals.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
            // Counts only: hidden text is exactly what must not reach the reader.
            ["hiddenSegments"] = screening.HiddenSegments.Count,
        };
        return new ReviewedContent(body, hidden, sentinel);
    }

    private const int MaxScreenedFieldLength = 8 * 1024;

    /// <summary>
    /// Screens the untrusted header fields (from, subject and, when returned, snippet) with the
    /// model-free pre-pass. The values themselves are already cleaned of invisible characters by
    /// the parser; this flags what was there: <c>invisible-characters</c>, the Sentinel's hidden
    /// content signals, and injection pattern signals (null when nothing was found).
    /// <c>Screened</c> is false when the pass could not run on every field: the metadata must
    /// then be withheld.
    /// </summary>
    public (bool Screened, JsonObject? Signals) ScreenFields(MailMetadata metadata, bool includeSnippet)
    {
        var result = new JsonObject();
        var screened = true;
        void One(string name, string? raw)
        {
            var signals = ScreenField(raw);
            if (signals == null) { screened = false; return; }
            if (signals.Count > 0) result[name] = new JsonArray(signals.Select(x => (JsonNode)JsonValue.Create(x)).ToArray());
        }
        One("from", metadata.Raw.From);
        One("to", string.Join(", ", metadata.To.Select(t => t.Name).Where(n => !string.IsNullOrEmpty(n))));
        One("subject", metadata.Raw.Subject);
        if (includeSnippet) One("snippet", metadata.Raw.Snippet);
        return (screened, result.Count > 0 ? result : null);
    }

    /// <summary>Signals for one untrusted field, or null when the Screen() pass could not run on it.</summary>
    public IReadOnlyList<string>? ScreenField(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return [];
        if (review == null) return null;
        if (raw.Length > MaxScreenedFieldLength) raw = raw[..MaxScreenedFieldLength];
        var signals = new List<string>();
        if (MailMessageParser.HasInvisible(raw)) signals.Add("invisible-characters");
        try
        {
            var screening = review.Screen(raw, UntrustedContentFormat.Text);
            foreach (var signal in screening.HiddenContentSignals.Concat(screening.InjectionSignals))
                if (!signals.Contains(signal)) signals.Add(signal);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        return signals;
    }

    /// <summary>True when the screening actually looked at <paramref name="input"/>.</summary>
    internal static bool Screened(UntrustedContentScreening screening, string input)
        => !screening.InjectionSignals.Contains(UntrustedContentScreening.ContentTooLargeSignal)
            && (screening.HasHiddenContent || screening.VisibleContent.Length > 0 || input.Length == 0);

    /// <summary>The Sentinel reviews at most this many characters (its own review bound); longer
    /// input is cut first and the result marked truncated.</summary>
    public const int UntrustedContentBound = 200_000;

    private static string Hash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    /// <summary>
    /// Audit provenance for the review job, built from the host-verified caller: app Mail,
    /// actor the bound agent (or the owner's own session), beneficiary the owner, and the
    /// discussion(s) named in the caller's execution context plus the mail item. Null if anything is missing;
    /// the gateway then derives identity from the ambient execution scope.
    /// </summary>
    public static async Task<ComputeProvenance?> ProvenanceAsync(
        IEntityStore entities, MailCaller caller, string route, IReadOnlyList<ComputeContextReference> context, CancellationToken ct)
    {
        try
        {
            var plugin = await entities.GetBySlugAsync("plugin", MailPlugin.PluginId, ct);
            if (plugin == null) return null;
            var app = new ComputeAppReference("plugin", plugin.Slug, plugin.Id.ToString(), plugin.Name);
            var user = Guid.TryParse(caller.UserId, out var userId) ? await entities.GetAsync(userId, ct) : null;
            var beneficiary = new ComputeBeneficiary("user", caller.UserId, user?.Name);
            ComputeActor actor;
            if (caller.Kind == MailCallerKind.Agent && Guid.TryParse(caller.AgentId, out var agentId))
            {
                var agent = await entities.GetAsync(agentId, ct);
                if (agent?.TypeSlug != "agent") return null;
                actor = new ComputeActor("agent", agent.Name, agent.Id.ToString(), agent.Slug);
            }
            else actor = new ComputeActor("user", user?.Name ?? "Owner", caller.UserId, caller.UserId);
            var references = caller.DiscussionIds.Select(d => new ComputeContextReference("discussion", d))
                .Concat(context).Take(15).ToArray();
            return new ComputeProvenance(ComputeProvenance.CurrentSchemaVersion,
                new ComputeOrigin("redleaf", app, new ComputeEntrypoint("http", route, "GET")),
                actor, beneficiary, references, new ComputeTrace(caller.ExecutionId),
                ComputeProvenanceAssurance.Verified, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }
}
