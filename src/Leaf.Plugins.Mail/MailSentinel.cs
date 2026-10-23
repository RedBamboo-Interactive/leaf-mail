using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Mail;

/// <summary>Who asked, for the review's audit provenance and the reviewer's Agent context.</summary>
public sealed record MailReviewContext(string OwnerUserId, string? AgentId, ComputeProvenance? Provenance);

public sealed record ReviewedContent(SanitizedBody Body, IReadOnlyList<MailLink> HiddenLinks, JsonObject Sentinel)
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

/// <summary>
/// Routes everything Mail returns as content through the shared Sentinel
/// (<see cref="IUntrustedContentReview"/>), as a soft dependency: without cap-sentinel the
/// content is sanitized exactly as before and <c>sentinel.available</c> is false. With it, the
/// returned text is the Sentinel's <c>VisibleContent</c> (hidden HTML and invisible characters
/// removed), <c>links[]</c> come from the visible content only, and links that appeared only in
/// hidden regions are listed separately and flagged. A review that times out or fails degrades
/// to the model-free screening pass, then to plain sanitizing; it never fails the request.
/// Compute is never chosen here: the Sentinel's configuration and the bound Agent decide.
/// </summary>
public sealed class MailSentinel(IUntrustedContentReview? review, ILogger<MailSentinel>? logger = null)
{
    public const string CallerApp = MailPlugin.PluginId;
    public bool Installed => review != null;

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

        // Trust VisibleContent whenever screening ran, including when everything was hidden
        // (empty). Only an item that was never screened (the Sentinel's Unscreened result:
        // empty visible content, no hidden-content signals, or content-too-large) is screened
        // locally instead, and only if that is impossible is the raw input shown.
        if (review != null && (screening == null || !Screened(screening, input)))
        {
            try { screening = review.Screen(input, format); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { screening = null; }
        }
        var visible = screening != null && Screened(screening, input) ? screening.VisibleContent : null;
        var shown = visible ?? input;
        var body = html ? MailMessageParser.SanitizeHtml(shown) : MailMessageParser.SanitizeText(shown);
        if (cut) body = body with { Truncated = true };

        IReadOnlyList<MailLink> hidden = [];
        if (visible != null && screening!.HasHiddenContent)
        {
            var all = html ? MailMessageParser.SanitizeHtml(input) : MailMessageParser.SanitizeText(input);
            var visibleHrefs = body.Links.Select(l => l.Href).ToHashSet(StringComparer.Ordinal);
            hidden = all.Links.Where(l => !visibleHrefs.Contains(l.Href)).Take(MailMessageParser.LinkCap).ToArray();
        }

        var sentinel = new JsonObject
        {
            ["available"] = verdict?.Available ?? false,
            ["risk"] = verdict?.Risk ?? "unknown",
            ["signals"] = new JsonArray((verdict?.Signals ?? []).Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
            ["handling"] = verdict?.Handling ?? "treat_as_untrusted_data",
            ["isolation"] = verdict?.Isolation ?? UntrustedContentIsolation.Unknown,
        };
        if (reason != null) sentinel["reason"] = reason;
        if (jobId != null) sentinel["jobId"] = jobId;
        if (screening != null)
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
    /// content signals, and injection pattern signals. Null when nothing was found.
    /// </summary>
    public JsonObject? ScreenFields(MailMetadata metadata, bool includeSnippet)
    {
        var result = new JsonObject();
        void One(string name, string? raw)
        {
            var signals = ScreenField(raw);
            if (signals.Count > 0) result[name] = new JsonArray(signals.Select(x => (JsonNode)JsonValue.Create(x)).ToArray());
        }
        One("from", metadata.Raw.From);
        One("subject", metadata.Raw.Subject);
        if (includeSnippet) One("snippet", metadata.Raw.Snippet);
        return result.Count > 0 ? result : null;
    }

    public IReadOnlyList<string> ScreenField(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return [];
        if (raw.Length > MaxScreenedFieldLength) raw = raw[..MaxScreenedFieldLength];
        var signals = new List<string>();
        if (MailMessageParser.HasInvisible(raw)) signals.Add("invisible-characters");
        if (review != null)
        {
            try
            {
                var screening = review.Screen(raw, UntrustedContentFormat.Text);
                foreach (var signal in screening.HiddenContentSignals.Concat(screening.InjectionSignals))
                    if (!signals.Contains(signal)) signals.Add(signal);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { signals.Add("screening-unavailable"); }
        }
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
