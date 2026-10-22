using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Mail;

/// <summary>
/// One OAuth consent attempt at a time. The redirect is received by a plain socket bound
/// to <see cref="IPAddress.Loopback"/> on an OS-assigned port (no http.sys, so no URL ACL
/// and no Host-header matching on other interfaces). It exists only while an attempt is
/// pending (at most ten minutes) and speaks just enough HTTP for one GET. If the socket
/// cannot be opened, the attempt still exists in paste-only mode. The redirect is
/// accepted only with the exact pending state, verified in constant time; the attempt is
/// single-use. The same verification guards the paste-the-redirect-URL fallback.
/// </summary>
public sealed class MailConsent(
    MailStore store,
    GoogleOAuthClient oauth,
    MailService service,
    ILogger<MailConsent>? logger = null,
    TimeProvider? clock = null,
    bool startListener = true,
    Func<TcpListener>? createListener = null) : IDisposable
{
    public static readonly TimeSpan AttemptLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(5);
    private const int MaxRequestHeadBytes = 8 * 1024;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private Attempt? _attempt;

    /// <summary>Raised after a successful connect so the poller can sync immediately.</summary>
    public event Action? Connected;

    private sealed class Attempt(string ownerUserId, DesktopClient client, string state, string verifier,
        string redirectUri, int port, DateTimeOffset expiresAt, TcpListener? listener)
    {
        // The source is cancelled, never disposed, and its token is captured once, so
        // late readers can never hit ObjectDisposedException.
        private readonly CancellationTokenSource _lifetime = new();
        private int _used;

        public string OwnerUserId { get; } = ownerUserId;
        public DesktopClient Client { get; } = client;
        public string State { get; } = state;
        public string Verifier { get; } = verifier;
        public string RedirectUri { get; } = redirectUri;
        public int Port { get; } = port;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public TcpListener? Listener { get; } = listener;
        public CancellationToken LifetimeToken => _lifetime.Token; // safe: _lifetime is never disposed
        public bool TryClaim() => Interlocked.Exchange(ref _used, 1) == 0;
        public bool Used => Volatile.Read(ref _used) == 1;

        public void Close()
        {
            try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
            try { Listener?.Stop(); } catch (SocketException) { } catch (ObjectDisposedException) { }
        }
    }

    public bool Pending { get { lock (_gate) return _attempt is { } a && a.ExpiresAt > _clock.GetUtcNow(); } }

    public async Task<JsonObject> StartAsync(string ownerUserId, CancellationToken ct)
    {
        var config = await store.GetConfigAsync(ct);
        if (!config.ClientConfigured)
            throw new MailFault("client_not_configured", 409);

        TcpListener? listener = null;
        int port;
        if (startListener)
        {
            try
            {
                listener = createListener?.Invoke() ?? new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException or UnauthorizedAccessException)
            {
                logger?.LogWarning("Mail consent listener unavailable ({Type}); paste-only attempt", ex.GetType().Name);
                try { listener?.Stop(); } catch (SocketException) { }
                listener = null;
                port = PasteOnlyPort();
            }
        }
        else port = PasteOnlyPort();

        var redirectUri = $"http://127.0.0.1:{port}/";
        var verifier = Pkce.CreateVerifier();
        var attempt = new Attempt(ownerUserId, new DesktopClient(config.ClientId!, config.ClientSecret!),
            Pkce.CreateState(), verifier, redirectUri, port, _clock.GetUtcNow() + AttemptLifetime, listener);
        var token = attempt.LifetimeToken;

        Attempt? previous;
        lock (_gate) { previous = _attempt; _attempt = attempt; }
        previous?.Close();

        if (listener != null)
            _ = Task.Run(async () =>
            {
                try { await ListenAsync(attempt, listener, token); }
                catch (Exception ex) { logger?.LogWarning("Mail consent listener stopped ({Type})", ex.GetType().Name); Close(attempt); }
            });
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(AttemptLifetime, token); }
            catch (OperationCanceledException) { return; }
            Close(attempt);
        });

        var authUrl = oauth.BuildAuthorizationUrl(attempt.Client, redirectUri, attempt.State, Pkce.Challenge(verifier));
        return new JsonObject
        {
            ["authUrl"] = authUrl,
            ["redirectUri"] = redirectUri,
            ["listening"] = listener != null,
            ["mode"] = listener != null ? "loopback" : "paste-only",
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

    private async Task ListenAsync(Attempt attempt, TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(token); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException)
            { return; }
            using (client)
            {
                await HandleAsync(attempt, client, token);
            }
            if (attempt.Used) { Close(attempt); return; }
        }
    }

    /// <summary>
    /// One connection, one response. Any failure, including a storage error while
    /// connecting, is answered to the browser; a claimed attempt is then closed by the
    /// caller, so nothing is left half-open and no task faults unobserved.
    /// </summary>
    private async Task HandleAsync(Attempt attempt, TcpClient client, CancellationToken token)
    {
        NetworkStream stream;
        try { stream = client.GetStream(); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { return; }
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        readTimeout.CancelAfter(RequestReadTimeout);
        try
        {
            var target = await ReadRequestTargetAsync(stream, readTimeout.Token);
            if (target is null) { await WriteAsync(stream, 400, "Bad request."); return; }
            var question = target.IndexOf('?');
            var path = question < 0 ? target : target[..question];
            if (path != "/") { await WriteAsync(stream, 404, "Not found."); return; }
            var query = QueryHelpers.ParseQuery(question < 0 ? "" : target[question..]);
            try
            {
                await CompleteAsync(attempt, attempt.OwnerUserId,
                    query.TryGetValue("state", out var state) ? state.ToString() : null,
                    query.TryGetValue("code", out var code) ? code.ToString() : null,
                    query.TryGetValue("error", out var error) ? error.ToString() : null, CancellationToken.None);
                await WriteAsync(stream, 200, "Mail is connected with read-only Gmail access. You can close this window.");
            }
            catch (MailFault fault) when (fault.Code == "state_mismatch")
            {
                // An unverified request does not consume the attempt.
                await WriteAsync(stream, 400, "This sign-in response does not match the pending request.");
            }
            catch (MailOAuthException ex)
            {
                logger?.LogWarning("Mail consent failed: {Reason}", ex.Code);
                await WriteAsync(stream, 400, "Connecting Mail failed: " + ex.Message);
            }
            catch (Exception ex)
            {
                // Storage, Gmail, or anything else: answer the browser, never leak details.
                attempt.TryClaim();
                logger?.LogWarning("Mail consent failed: {Reason}", ex is MailFault f ? f.Code : ex.GetType().Name);
                await WriteAsync(stream, 500, "Connecting Mail failed. Return to Settings → Mail and try again.");
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Client went away or was too slow; the attempt stays pending unless it was claimed.
        }
    }

    /// <summary>Reads the request head (bounded size and time) and returns the GET target, or null.</summary>
    internal static async Task<string?> ReadRequestTargetAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestHeadBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
            var head = Encoding.ASCII.GetString(buffer, 0, length);
            if (head.Contains("\r\n\r\n", StringComparison.Ordinal) || head.Contains("\n\n", StringComparison.Ordinal)) break;
        }
        var text = Encoding.ASCII.GetString(buffer, 0, length);
        var lineEnd = text.IndexOf('\n');
        if (lineEnd < 0) return null;
        var parts = text[..lineEnd].TrimEnd('\r').Split(' ');
        if (parts.Length != 3 || parts[0] != "GET" || !parts[1].StartsWith('/') || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            return null;
        return parts[1];
    }

    private static async Task WriteAsync(Stream stream, int status, string message)
    {
        var html = "<!doctype html><meta charset=\"utf-8\"><title>Mail</title><body style=\"font-family:sans-serif;padding:2rem\"><p>"
            + WebUtility.HtmlEncode(message) + "</p></body>";
        var body = Encoding.UTF8.GetBytes(html);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", _ => "Internal Server Error" };
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n"
            + "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private void Close(Attempt attempt)
    {
        lock (_gate) { if (ReferenceEquals(_attempt, attempt)) _attempt = null; }
        attempt.Close();
    }

    public void Cancel()
    {
        Attempt? attempt;
        lock (_gate) { attempt = _attempt; _attempt = null; }
        attempt?.Close();
    }

    /// <summary>A port for the redirect URI when no listener runs (the browser page will fail; the owner pastes it).</summary>
    private static int PasteOnlyPort()
    {
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }
        }
        catch (SocketException) { return Random.Shared.Next(49152, 65535); }
    }

    /// <summary>Test hook: the pending attempt's state, never exposed over HTTP.</summary>
    internal string? PendingStateForTests { get { lock (_gate) return _attempt?.State; } }

    public void Dispose() => Cancel();
}
