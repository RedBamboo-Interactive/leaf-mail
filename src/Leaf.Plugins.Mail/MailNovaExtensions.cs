using System.Text.Json.Nodes;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Mail;

/// <summary>
/// Mail's optional augmentation of Nova's generic slots. The context fragment carries
/// health and counts only (never subjects, senders, or bodies) and only for the exact
/// owner and bound agent. LIVE notices flow through <see cref="MailLiveOutbox"/>.
/// </summary>
public sealed class MailNovaExtensions(IPluginExtensions extensions, MailStore store, MailLiveOutbox outbox) : IDisposable
{
    public const string ContextExtensionId = "nova-mail-context";
    public const string LiveExtensionId = "nova-mail-live";
    public const int MaxContent = 400;

    private IDisposable? _context;
    private IDisposable? _live;

    public void Start()
    {
        _context ??= extensions.RegisterContextProvider(ContextExtensionId, ContextAsync);
        _live ??= extensions.RegisterEventProjector(LiveExtensionId, MailEvents.LivePending, outbox.ProjectAsync);
    }

    public async Task<PluginContextFragment?> ContextAsync(PluginContextRequest request, CancellationToken ct)
    {
        var config = await store.GetConfigAsync(ct);
        if (!SameId(request.UserId, config.OwnerUserId) || !SameId(request.AgentId, config.AgentId)) return null;
        var account = await store.GetAccountAsync(ct);
        if (account == null || account.Status == MailStatus.Disconnected) return null;
        var content = Render(account);
        return new PluginContextFragment(
            "mail-status",
            MailPlugin.PluginId,
            content,
            new JsonObject
            {
                ["status"] = account.Status,
                ["inboxUnread"] = account.InboxUnread,
                ["lastSyncAt"] = account.LastSyncAt?.ToString("O"),
            },
            account.LastSyncAt,
            Revision: $"{account.Status}:{account.InboxUnread}");
    }

    internal static string Render(MailAccountSnapshot account)
    {
        var text = account.Status == MailStatus.NeedsReauth
            ? "Mail needs reconnecting: Settings → Mail. Until then, mail cannot be searched or read."
            : account.InboxUnread is { } unread
                ? $"Mail connected ({MailData.MaskEmail(account.Email)}): {unread} unread in Inbox. Use the mail skill to search or read."
                : $"Mail connected ({MailData.MaskEmail(account.Email)}). Use the mail skill to search or read.";
        return text.Length <= MaxContent ? text : text[..(MaxContent - 1)] + "…";
    }

    private static bool SameId(string? a, string? b)
        => Guid.TryParse(a, out var x) && Guid.TryParse(b, out var y) && x == y;

    public void Dispose()
    {
        _live?.Dispose();
        _context?.Dispose();
        _live = null;
        _context = null;
    }
}
