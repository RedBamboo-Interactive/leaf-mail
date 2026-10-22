using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Mail;

/// <summary>
/// One OAuth consent attempt at a time. A loopback <see cref="HttpListener"/> on
/// 127.0.0.1:&lt;random free port&gt; exists only while an attempt is pending (at most
/// ten minutes). The redirect is accepted only with the exact pending state, verified in
/// constant time; the attempt is single-use. The same verification guards the
/// paste-the-redirect-URL fallback for consent completed on another device.
/// </summary>
public sealed class MailConsent(
    MailStore store,
    GoogleOAuthClient oauth,
    MailService service,
    ILogger<MailConsent>? logger = null,
    TimeProvider? clock = null,
    bool startListener = true) : IDisposable
{
    public static readonly TimeSpan AttemptLifetime = TimeSpan.FromMinutes(10);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private Attempt? _attempt;

    /// <summary>Raised after a successful connect so the poller can sync immediately.</summary>
    public event Action? Connected;

    private sealed class Attempt(string ownerUserId, DesktopClient client, string state, string verifier,
        string redirectUri, int port, DateTimeOffset expiresAt) : IDisposable
    {
        public string OwnerUserId { get; } = ownerUserId;
        public DesktopClient Client { get; } = client;
        public string State { get; } = state;
        public string Verifier { get; } = verifier;
        public string RedirectUri { get; } = redirectUri;
        public int Port { get; } = port;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public HttpListener? Listener { get; set; }
        public CancellationTokenSource Lifetime { get; } = new();
        private int _used;
        public bool TryClaim() => Interlocked.Exchange(ref _used, 1) == 0;
        public bool Used => Volatile.Read(ref _used) == 1;
        public void Dispose()
        {
            try { Lifetime.Cancel(); } catch (ObjectDisposedException) { }
            try { Listener?.Close(); } catch (ObjectDisposedException) { }
            Lifetime.Dispose();
        }
    }

    public bool Pending { get { lock (_gate) return _attempt is { } a && a.ExpiresAt > _clock.GetUtcNow(); } }

    public async Task<JsonObject> StartAsync(string ownerUserId, CancellationToken ct)
    {
        var config = await store.GetConfigAsync(ct);
        if (!config.ClientConfigured)
            throw new MailFault("client_not_configured", 409);

        var port = FreeLoopbackPort();
        var redirectUri = $"http://127.0.0.1:{port}/";
        var verifier = Pkce.CreateVerifier();
        var attempt = new Attempt(ownerUserId, new DesktopClient(config.ClientId!, config.ClientSecret!),
            Pkce.CreateState(), verifier, redirectUri, port, _clock.GetUtcNow() + AttemptLifetime);

        if (startListener)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(redirectUri);
            listener.Start();
            attempt.Listener = listener;
        }

        Attempt? previous;
        lock (_gate) { previous = _attempt; _attempt = attempt; }
        previous?.Dispose();

        if (attempt.Listener != null)
            _ = Task.Run(() => ListenAsync(attempt));
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(AttemptLifetime, attempt.Lifetime.Token); }
            catch (OperationCanceledException) { return; }
            Close(attempt);
        });

        var authUrl = oauth.BuildAuthorizationUrl(attempt.Client, redirectUri, attempt.State, Pkce.Challenge(verifier));
        return new JsonObject
        {
            ["authUrl"] = authUrl,
            ["redirectUri"] = redirectUri,
            ["expiresAt"] = attempt.ExpiresAt.ToString("O"),
            ["scope"] = MailData.ReadonlyScope,
        };
    }

    /// <summary>Paste fallback: the full redirected URL from a browser on another device.</summary>
    public async Task<JsonObject> CompleteFromRedirectUrlAsync(string ownerUserId, string redirectUrl, CancellationToken ct)
    {
        Attempt attempt;
        lock (_gate) attempt = _attempt ?? throw new MailFault("no_pending_consent", 409);
        if (!Uri.TryCreate(redirectUrl?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != "http"
            || uri.Host is not ("127.0.0.1" or "localhost")
            || uri.Port != attempt.Port
            || uri.AbsolutePath != "/")
            throw new MailFault("redirect_url_mismatch", 400);
        var query = QueryHelpers.ParseQuery(uri.Query);
        try
        {
            return await CompleteAsync(attempt, ownerUserId,
                query.TryGetValue("state", out var state) ? state.ToString() : null,
                query.TryGetValue("code", out var code) ? code.ToString() : null,
                query.TryGetValue("error", out var error) ? error.ToString() : null, ct);
        }
        finally { if (attempt.Used) Close(attempt); }
    }

    /// <summary>Verifies and, once claimed, consumes the attempt. Callers close a used attempt.</summary>
    private async Task<JsonObject> CompleteAsync(
        Attempt attempt, string ownerUserId, string? state, string? code, string? error, CancellationToken ct)
    {
        if (attempt.ExpiresAt <= _clock.GetUtcNow()) { attempt.TryClaim(); throw new MailFault("consent_expired", 410); }
        // State first: nothing about an unverified redirect (including its error) is acted on.
        if (!Pkce.StateMatches(attempt.State, state)) throw new MailFault("state_mismatch", 400);
        if (!string.Equals(attempt.OwnerUserId, ownerUserId, StringComparison.OrdinalIgnoreCase))
            throw new MailFault("owner_required", 403);
        if (!attempt.TryClaim()) throw new MailFault("consent_already_used", 409);
        if (!string.IsNullOrEmpty(error))
            throw new MailOAuthException("consent_denied", "Consent was not granted at Google.");
        if (string.IsNullOrEmpty(code) || code.Length > 2048)
            throw new MailFault("authorization_code_missing", 400);
        var grant = await oauth.ExchangeCodeAsync(attempt.Client, code, attempt.Verifier, attempt.RedirectUri, ct);
        var status = await service.ConnectAsync(attempt.OwnerUserId, grant, ct);
        try { Connected?.Invoke(); } catch { }
        return status;
    }

    private async Task ListenAsync(Attempt attempt)
    {
        var listener = attempt.Listener!;
        while (!attempt.Lifetime.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }

            var response = context.Response;
            try
            {
                if (context.Request.HttpMethod != "GET" || context.Request.Url?.AbsolutePath != "/")
                {
                    await WriteAsync(response, 404, "Not found.");
                    continue;
                }
                var query = context.Request.QueryString;
                try
                {
                    await CompleteAsync(attempt, attempt.OwnerUserId, query["state"], query["code"], query["error"], CancellationToken.None);
                    await WriteAsync(response, 200, "Mail is connected with read-only Gmail access. You can close this window.");
                }
                catch (MailFault fault) when (fault.Code == "state_mismatch")
                {
                    // An unverified request does not consume the attempt.
                    await WriteAsync(response, 400, "This sign-in response does not match the pending request.");
                }
                catch (Exception ex) when (ex is MailFault or MailOAuthException or GmailApiException or MailReauthRequiredException)
                {
                    logger?.LogWarning("Mail consent failed: {Reason}", ex is MailFault f ? f.Code : ex is MailOAuthException o ? o.Code : ex.GetType().Name);
                    await WriteAsync(response, 400, ex is MailOAuthException oauthError
                        ? "Connecting Mail failed: " + oauthError.Message
                        : "Connecting Mail failed. Return to Settings → Mail and try again.");
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            if (attempt.Used) { Close(attempt); return; }
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, int status, string message)
    {
        var html = "<!doctype html><meta charset=\"utf-8\"><title>Mail</title><body style=\"font-family:sans-serif;padding:2rem\"><p>"
            + WebUtility.HtmlEncode(message) + "</p></body>";
        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = status;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private void Close(Attempt attempt)
    {
        lock (_gate) { if (ReferenceEquals(_attempt, attempt)) _attempt = null; }
        attempt.Dispose();
    }

    public void Cancel()
    {
        Attempt? attempt;
        lock (_gate) { attempt = _attempt; _attempt = null; }
        attempt?.Dispose();
    }

    internal static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    /// <summary>Test hook: the pending attempt's state, never exposed over HTTP.</summary>
    internal string? PendingStateForTests { get { lock (_gate) return _attempt?.State; } }

    public void Dispose() => Cancel();
}
