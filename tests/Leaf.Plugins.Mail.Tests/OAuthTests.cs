using System.Net;
using System.Text.Json.Nodes;
using Leaf.Plugins.Mail;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace Leaf.Plugins.Mail.Tests;

public sealed class OAuthTests
{
    [Fact]
    public void Pkce_challenge_matches_the_RFC_7636_S256_vector()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var verifier = Pkce.CreateVerifier();
        Assert.InRange(verifier.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9_-]+$", verifier);
        Assert.NotEqual(Pkce.CreateState(), Pkce.CreateState());
    }

    [Fact]
    public void State_verification_rejects_mismatch_missing_and_prefix()
    {
        var state = Pkce.CreateState();
        Assert.True(Pkce.StateMatches(state, state));
        Assert.False(Pkce.StateMatches(state, Pkce.CreateState()));
        Assert.False(Pkce.StateMatches(state, null));
        Assert.False(Pkce.StateMatches(state, ""));
        Assert.False(Pkce.StateMatches(state, state[..^1]));
        Assert.False(Pkce.StateMatches(null, state));
    }

    [Fact]
    public void Authorization_url_requests_only_readonly_offline_consent_with_S256()
    {
        var oauth = new GoogleOAuthClient(new HttpClient(new FakeGoogle()), new GoogleEndpoints());
        var url = new Uri(oauth.BuildAuthorizationUrl(new DesktopClient(Harness.ClientId, Harness.ClientSecret),
            "http://127.0.0.1:5555/", "state-value", "challenge-value"));
        var q = QueryHelpers.ParseQuery(url.Query);
        Assert.Equal("accounts.google.com", url.Host);
        Assert.Equal(MailData.ReadonlyScope, q["scope"].ToString());
        Assert.Equal("offline", q["access_type"].ToString());
        Assert.Equal("consent", q["prompt"].ToString());
        Assert.Equal("S256", q["code_challenge_method"].ToString());
        Assert.Equal("challenge-value", q["code_challenge"].ToString());
        Assert.Equal("state-value", q["state"].ToString());
        Assert.Equal("code", q["response_type"].ToString());
        Assert.False(q.ContainsKey("include_granted_scopes"));
    }

    [Theory]
    [InlineData("https://www.googleapis.com/auth/gmail.readonly", true)]
    [InlineData("https://www.googleapis.com/auth/gmail.readonly https://www.googleapis.com/auth/gmail.readonly", true)]
    [InlineData("https://www.googleapis.com/auth/gmail.readonly https://www.googleapis.com/auth/gmail.send", false)]
    [InlineData("https://www.googleapis.com/auth/gmail.readonly openid", false)]
    [InlineData("https://www.googleapis.com/auth/gmail.modify", false)]
    [InlineData("https://mail.google.com/", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Scope_set_must_be_exactly_gmail_readonly(string? granted, bool ok)
        => Assert.Equal(ok, GoogleScopes.IsExactlyReadonly(granted));

    [Fact]
    public void Installed_client_json_is_accepted()
    {
        var client = GoogleClientJson.Parse($$$"""
            {"installed":{"client_id":"{{{Harness.ClientId}}}","project_id":"example-project",
            "auth_uri":"https://accounts.google.com/o/oauth2/auth","token_uri":"https://oauth2.googleapis.com/token",
            "auth_provider_x509_cert_url":"https://www.googleapis.com/oauth2/v1/certs",
            "client_secret":"{{{Harness.ClientSecret}}}","redirect_uris":["http://localhost"]}}
            """);
        Assert.Equal(Harness.ClientId, client.ClientId);
        Assert.Equal(Harness.ClientSecret, client.ClientSecret);
    }

    [Theory]
    [InlineData("""{"web":{"client_id":"123456789012-abcdefghijklmnop0123456789abcdef.apps.googleusercontent.com","client_secret":"GOCSPX-test_secret_value"}}""", "Web application")]
    [InlineData("""{"installed":{"client_id":"123456789012-abcdefghijklmnop0123456789abcdef.apps.googleusercontent.com","client_secret":"GOCSPX-test_secret_value"},"web":{}}""", "exactly one")]
    [InlineData("""{"installed":{"client_id":"not-a-google-client","client_secret":"GOCSPX-test_secret_value"}}""", "client_id")]
    [InlineData("""{"installed":{"client_id":"123456789012-abcdefghijklmnop0123456789abcdef.apps.googleusercontent.com"}}""", "client_secret")]
    [InlineData("""{"installed":{"client_id":"123456789012-abcdefghijklmnop0123456789abcdef.apps.googleusercontent.com","client_secret":"GOCSPX-test_secret_value","token_uri":"https://evil.example/token"}}""", "token_uri")]
    [InlineData("""{"type":"service_account","private_key":"x"}""", "installed")]
    [InlineData("not json", "valid JSON")]
    [InlineData("[]", "object")]
    public void Non_installed_or_malformed_client_json_is_rejected(string json, string message)
    {
        var ex = Assert.Throws<ArgumentException>(() => GoogleClientJson.Parse(json));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public async Task Token_errors_expose_only_the_oauth_code_never_the_body()
    {
        var google = new FakeGoogle().Json(HttpMethod.Post, "https://oauth2.googleapis.com/token",
            """{"error":"invalid_grant","error_description":"Token has been expired or revoked. refresh-token-1"}""", HttpStatusCode.BadRequest);
        var oauth = new GoogleOAuthClient(new HttpClient(google), new GoogleEndpoints());
        var ex = await Assert.ThrowsAsync<MailOAuthException>(() =>
            oauth.RefreshAsync(new DesktopClient(Harness.ClientId, Harness.ClientSecret), "refresh-token-1", default));
        Assert.Equal("invalid_grant", ex.Code);
        Assert.True(ex.RequiresReauth);
        Assert.DoesNotContain("refresh-token-1", ex.Message);
        Assert.DoesNotContain("expired or revoked", ex.Message);
    }

    // ── Consent flow ─────────────────────────────────────────────────────────

    private static async Task<(Harness H, MailConsent Consent, string State, int Port, string Verifier)> StartConsentAsync(bool listener = false)
    {
        var h = new Harness();
        await h.ConfigureAsync(connected: false);
        var consent = new MailConsent(h.Store, h.OAuth, h.Service, startListener: listener);
        var started = await consent.StartAsync(Harness.Owner, default);
        var auth = new Uri(started["authUrl"]!.GetValue<string>());
        var q = QueryHelpers.ParseQuery(auth.Query);
        var redirect = new Uri(q["redirect_uri"].ToString());
        Assert.Equal("127.0.0.1", redirect.Host);
        Assert.Equal(consent.PendingStateForTests, q["state"].ToString());
        return (h, consent, q["state"].ToString(), redirect.Port, q["code_challenge"].ToString());
    }

    private static void GoogleGrants(Harness h, string scope)
    {
        h.Google.Json(HttpMethod.Post, "https://oauth2.googleapis.com/token",
            $$"""{"access_token":"access-1","refresh_token":"refresh-new","expires_in":3599,"scope":"{{scope}}","token_type":"Bearer"}""");
        h.Google.Json(HttpMethod.Post, "https://oauth2.googleapis.com/revoke", "{}");
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/profile", """{"emailAddress":"user@example.com","historyId":"5000"}""");
    }

    [Fact]
    public async Task Paste_fallback_with_bad_state_is_rejected_without_exchange_or_consuming_the_attempt()
    {
        var (h, consent, state, port, _) = await StartConsentAsync();
        GoogleGrants(h, MailData.ReadonlyScope);

        var bad = await Assert.ThrowsAsync<MailFault>(() => consent.CompleteFromRedirectUrlAsync(Harness.Owner,
            $"http://127.0.0.1:{port}/?state=forged&code=abc&scope={Uri.EscapeDataString(MailData.ReadonlyScope)}", default));
        Assert.Equal("state_mismatch", bad.Code);
        var noState = await Assert.ThrowsAsync<MailFault>(() => consent.CompleteFromRedirectUrlAsync(Harness.Owner,
            $"http://127.0.0.1:{port}/?code=abc", default));
        Assert.Equal("state_mismatch", noState.Code);
        var wrongPort = await Assert.ThrowsAsync<MailFault>(() => consent.CompleteFromRedirectUrlAsync(Harness.Owner,
            $"http://127.0.0.1:{port + 1}/?state={state}&code=abc", default));
        Assert.Equal("redirect_url_mismatch", wrongPort.Code);
        var otherHost = await Assert.ThrowsAsync<MailFault>(() => consent.CompleteFromRedirectUrlAsync(Harness.Owner,
            $"https://evil.example:{port}/?state={state}&code=abc", default));
        Assert.Equal("redirect_url_mismatch", otherHost.Code);
        Assert.Equal(0, h.Google.Count("https://oauth2.googleapis.com/token"));
        Assert.True(consent.Pending);

        var status = await consent.CompleteFromRedirectUrlAsync(Harness.Owner,
            $"http://127.0.0.1:{port}/?state={Uri.EscapeDataString(state)}&code=good-code&scope={Uri.EscapeDataString(MailData.ReadonlyScope)}", default);
        Assert.True(status["connected"]!.GetValue<bool>());
        Assert.False(consent.Pending);

        var exchange = Assert.Single(h.Google.Requests, r => r.Url.StartsWith("https://oauth2.googleapis.com/token"));
        var form = QueryHelpers.ParseQuery("?" + exchange.Body);
        Assert.Equal("authorization_code", form["grant_type"].ToString());
        Assert.Equal("good-code", form["code"].ToString());
        Assert.Equal($"http://127.0.0.1:{port}/", form["redirect_uri"].ToString());
        Assert.Equal(64, form["code_verifier"].ToString().Length);

        // Single use: replaying the same redirect fails.
        var replay = await Assert.ThrowsAsync<MailFault>(() => consent.CompleteFromRedirectUrlAsync(Harness.Owner,
            $"http://127.0.0.1:{port}/?state={Uri.EscapeDataString(state)}&code=good-code", default));
        Assert.Equal("no_pending_consent", replay.Code);
    }

    [Fact]
    public async Task Verifier_sent_at_exchange_matches_the_challenge_in_the_auth_url()
    {
        var (h, consent, state, port, challenge) = await StartConsentAsync();
        GoogleGrants(h, MailData.ReadonlyScope);
        await consent.CompleteFromRedirectUrlAsync(Harness.Owner, $"http://127.0.0.1:{port}/?state={Uri.EscapeDataString(state)}&code=c", default);
        var form = QueryHelpers.ParseQuery("?" + h.Google.Requests.Single(r => r.Url.StartsWith("https://oauth2.googleapis.com/token")).Body);
        Assert.Equal(challenge, Pkce.Challenge(form["code_verifier"].ToString()));
    }

    [Theory]
    [InlineData("https://www.googleapis.com/auth/gmail.readonly https://www.googleapis.com/auth/gmail.send")]
    [InlineData("https://www.googleapis.com/auth/userinfo.email")]
    public async Task Consent_with_extra_or_missing_scope_stores_nothing_and_revokes(string scope)
    {
        var (h, consent, state, port, _) = await StartConsentAsync();
        GoogleGrants(h, scope);
        var ex = await Assert.ThrowsAsync<MailOAuthException>(() => consent.CompleteFromRedirectUrlAsync(Harness.Owner,
            $"http://127.0.0.1:{port}/?state={Uri.EscapeDataString(state)}&code=c", default));
        Assert.Equal("scope_mismatch", ex.Code);
        Assert.Null(await h.Store.GetAccountAsync());
        var revoke = Assert.Single(h.Google.Requests, r => r.Url.StartsWith("https://oauth2.googleapis.com/revoke"));
        Assert.Contains("refresh-new", revoke.Body);
    }

    [Fact]
    public async Task Consent_by_another_user_is_rejected()
    {
        var (h, consent, state, port, _) = await StartConsentAsync();
        GoogleGrants(h, MailData.ReadonlyScope);
        var ex = await Assert.ThrowsAsync<MailFault>(() => consent.CompleteFromRedirectUrlAsync(
            "33333333-3333-3333-3333-333333333333", $"http://127.0.0.1:{port}/?state={Uri.EscapeDataString(state)}&code=c", default));
        Assert.Equal("owner_required", ex.Code);
        Assert.Equal(0, h.Google.Count("https://oauth2.googleapis.com/token"));
    }

    [Fact]
    public async Task Expired_attempt_is_rejected()
    {
        var h = new Harness();
        await h.ConfigureAsync(connected: false);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var consent = new MailConsent(h.Store, h.OAuth, h.Service, clock: clock, startListener: false);
        var started = await consent.StartAsync(Harness.Owner, default);
        var q = QueryHelpers.ParseQuery(new Uri(started["authUrl"]!.GetValue<string>()).Query);
        var port = new Uri(q["redirect_uri"].ToString()).Port;
        clock.Now += TimeSpan.FromMinutes(11);
        var ex = await Assert.ThrowsAsync<MailFault>(() => consent.CompleteFromRedirectUrlAsync(Harness.Owner,
            $"http://127.0.0.1:{port}/?state={Uri.EscapeDataString(q["state"].ToString())}&code=c", default));
        Assert.Equal("consent_expired", ex.Code);
        Assert.False(consent.Pending);
    }

    [Fact]
    public async Task Loopback_listener_rejects_bad_state_then_completes_and_closes()
    {
        var (h, consent, state, port, _) = await StartConsentAsync(listener: true);
        GoogleGrants(h, MailData.ReadonlyScope);
        using var browser = new HttpClient();

        var forged = await browser.GetAsync($"http://127.0.0.1:{port}/?state=forged&code=abc");
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.True(consent.Pending);

        var ok = await browser.GetAsync($"http://127.0.0.1:{port}/?state={Uri.EscapeDataString(state)}&code=good&scope={Uri.EscapeDataString(MailData.ReadonlyScope)}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("read-only", await ok.Content.ReadAsStringAsync());
        Assert.Equal(MailStatus.Connected, (await h.Store.GetAccountAsync())!.Status);

        await Task.Delay(100);
        Assert.False(consent.Pending);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => browser.GetAsync($"http://127.0.0.1:{port}/?state=x"));
    }

    [Fact]
    public async Task Disconnect_revokes_and_clears_the_refresh_token()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        h.Google.Json(HttpMethod.Post, "https://oauth2.googleapis.com/revoke", "{}");
        var status = await h.Service.DisconnectAsync(default);
        Assert.True(status["revoked"]!.GetValue<bool>());
        Assert.Contains("refresh-token-1", Assert.Single(h.Google.Requests).Body);
        var account = (await h.Store.GetAccountAsync())!;
        Assert.Equal(MailStatus.Disconnected, account.Status);
        Assert.Null(account.RefreshToken);
        Assert.Null(account.HistoryId);
    }
}
