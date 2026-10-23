using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Mail;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace Leaf.Plugins.Mail.Tests;

/// <summary>Regression tests for the security review findings (numbered as in the review).</summary>
public sealed class SecurityFixTests
{
    private static void History(Harness h, string historyId, params string[] ids)
    {
        var added = new JsonArray(ids.Select(id => (JsonNode)new JsonObject
        {
            ["messagesAdded"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["id"] = id } }),
        }).ToArray());
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/history", new JsonObject { ["history"] = added, ["historyId"] = historyId }.ToJsonString());
    }

    private static async Task SyncTwoMessagesAsync(Harness h)
    {
        h.TokenOk();
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/labels/INBOX", """{"messagesUnread":1}""");
        History(h, "1010", "m1", "m2");
        h.Batch(new()
        {
            ["m1"] = Harness.MessageJson("m1", "Example Sender <sender@example.com>", "Private subject", "INBOX"),
            ["m2"] = Harness.MessageJson("m2", "Other <other@example.com>", "Another", "INBOX"),
        });
        Assert.Equal(SyncResult.Synced, await h.Service.SyncOnceAsync(default));
    }

    // ── 1: records hold no content and are parented to the confidential account ──

    [Fact]
    public async Task F1_records_hold_ids_only_and_are_parented_to_the_confidential_account()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        await SyncTwoMessagesAsync(h);

        var account = (await h.Store.GetAccountAsync())!;
        foreach (var (key, data) in h.Records.Once)
        {
            Assert.Equal(["id", "threadId", "labelIds", "internalDate"], data.Select(p => p.Key).ToArray());
            var json = data.ToJsonString();
            foreach (var leak in new[] { "example.com", "Example Sender", "Private subject", "snippet", "from", "subject" })
                Assert.DoesNotContain(leak, json, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(account.Entity.Id, h.Records.Scope[key].EntityId);
            Assert.Equal(Harness.Owner, h.Records.Scope[key].UserId);
        }
        Assert.True(MailData.Boolean(account.Entity.Data, "confidential"));
        Assert.Equal(Harness.Owner, MailData.String(account.Entity.Data, "owner_id"));
        Assert.Equal(Harness.Agent, MailData.String(account.Entity.Data, "owner_agent_id"));
        // The LIVE notice still names the sender and subject: it is built from memory.
        Assert.Contains(h.Live, p => p.Content.Contains("Private subject"));
    }

    // ── 8: records are deleted on disconnect and on mailbox change ─────────────

    [Fact]
    public async Task F8_disconnect_deletes_message_records()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        await SyncTwoMessagesAsync(h);
        h.Google.Json(HttpMethod.Post, "https://oauth2.googleapis.com/revoke", "{}");
        Assert.Equal(2, h.Records.Once.Count);
        await h.Service.DisconnectAsync(default);
        Assert.Empty(h.Records.Once);
    }

    [Fact]
    public async Task F8_reconnecting_a_different_mailbox_deletes_records_but_the_same_mailbox_keeps_them()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        await SyncTwoMessagesAsync(h);
        h.Google.Json(HttpMethod.Post, "https://oauth2.googleapis.com/revoke", "{}");

        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/profile", """{"emailAddress":"user@example.com","historyId":"9000"}""");
        await h.Service.ConnectAsync(Harness.Owner, new TokenGrant("a2", "refresh-2", DateTimeOffset.UtcNow.AddHours(1), MailData.ReadonlyScope), default);
        Assert.Equal(2, h.Records.Once.Count);

        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/profile", """{"emailAddress":"second@example.com","historyId":"9001"}""");
        await h.Service.ConnectAsync(Harness.Owner, new TokenGrant("a3", "refresh-3", DateTimeOffset.UtcNow.AddHours(1), MailData.ReadonlyScope), default);
        Assert.Empty(h.Records.Once);
        var account = (await h.Store.GetAccountAsync())!;
        Assert.Equal("second@example.com", account.Email);
        Assert.Null(account.HistoryId);
    }

    // ── 2: confidential entities; binding derived from kernel fields and pinned ──

    [Fact]
    public async Task F2_config_and_account_are_confidential_and_binding_comes_from_kernel_owner_fields()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        var config = await h.Store.GetConfigAsync();
        Assert.True(MailData.Boolean(config.Entity.Data, "confidential"));
        Assert.Equal(Harness.Owner, MailData.String(config.Entity.Data, "owner_id"));
        Assert.Equal(Harness.Agent, MailData.String(config.Entity.Data, "owner_agent_id"));
        Assert.True(config.BindingIntact);
        Assert.Equal(new MailPolicy(Harness.Owner, Harness.Agent), await h.Store.GetPolicyAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Store.PatchConfigAsync(new JsonObject { ["owner_id"] = "33333333-3333-3333-3333-333333333333" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Store.PatchConfigAsync(new JsonObject { ["agent_id"] = "33333333-3333-3333-3333-333333333333" }));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("agent")]
    [InlineData("confidential")]
    public async Task F2_binding_rewritten_outside_mail_is_reverted_to_the_pin(string tamper)
    {
        var h = new Harness();
        await h.ConfigureAsync();
        var config = await h.Store.GetConfigAsync();
        var other = "33333333-3333-3333-3333-333333333333";
        // What an admin or the owning agent could do through PATCH /api/entities: rewrite both copies coherently.
        await h.Entities.PatchAsync(config.Entity.Id, tamper switch
        {
            "owner" => new JsonObject { ["owner_id"] = other, ["owner_user_id"] = other },
            "agent" => new JsonObject { ["owner_agent_id"] = other, ["agent_id"] = other },
            _ => new JsonObject { ["confidential"] = false },
        });

        Assert.Equal(new MailPolicy(Harness.Owner, Harness.Agent), await h.Store.GetPolicyAsync());
        Assert.Equal(1, h.Store.BindingRestoreCount);
        var restored = await h.Store.GetConfigAsync();
        Assert.True(restored.BindingIntact);
        Assert.Equal(Harness.Owner, restored.OwnerUserId);
        Assert.Equal(Harness.Agent, restored.AgentId);

        // The rewritten owner gets no access through Mail's caller filter.
        if (tamper == "owner")
        {
            var policy = await h.Store.GetPolicyAsync();
            Assert.Equal("owner_required", (await Assert.ThrowsAsync<MailFault>(() => MailCaller.FromAsync(
                CallerTests.User(other), policy, (_, _, _, _) => Task.FromResult(true), default))).Code);
        }
    }

    [Fact]
    public async Task F2_account_ownership_rewritten_outside_mail_is_restored()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        var account = (await h.Store.GetAccountAsync())!;
        await h.Entities.PatchAsync(account.Entity.Id, new JsonObject { ["confidential"] = false, ["owner_id"] = null });
        await h.Store.GetPolicyAsync();
        var data = (await h.Store.GetAccountAsync())!.Entity.Data;
        Assert.True(MailData.Boolean(data, "confidential"));
        Assert.Equal(Harness.Owner, MailData.String(data, "owner_id"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task F2_a_fresh_process_with_an_inconsistent_or_non_confidential_binding_fails_closed(bool confidential, bool consistent)
    {
        var h = new Harness();
        await h.ConfigureAsync();
        var config = await h.Store.GetConfigAsync();
        await h.Entities.PatchAsync(config.Entity.Id, new JsonObject
        {
            ["confidential"] = confidential,
            ["owner_user_id"] = consistent ? Harness.Owner : "33333333-3333-3333-3333-333333333333",
        });
        var restarted = new MailStore(h.Entities);
        Assert.Equal("mail_binding_untrusted", (await Assert.ThrowsAsync<MailFault>(() => restarted.GetPolicyAsync())).Code);
    }

    // ── 6: atomic, admin-only first claim ─────────────────────────────────────

    [Fact]
    public async Task F6_first_claim_requires_admin_and_later_claims_require_the_owner()
    {
        var h = new Harness();
        await h.Store.EnsureConfigAsync();
        Assert.Equal("admin_required_for_first_claim",
            (await Assert.ThrowsAsync<MailFault>(() => h.Store.ClaimOwnerAsync(Harness.Owner, isAdmin: false))).Code);
        await h.Store.ClaimOwnerAsync(Harness.Owner, isAdmin: true);
        Assert.Equal("owner_required", (await Assert.ThrowsAsync<MailFault>(() =>
            h.Store.ClaimOwnerAsync("33333333-3333-3333-3333-333333333333", isAdmin: true))).Code);
        Assert.Equal(Harness.Owner, (await h.Store.ClaimOwnerAsync(Harness.Owner, isAdmin: false)).OwnerUserId);
    }

    [Fact]
    public async Task F6_concurrent_first_claims_have_exactly_one_winner()
    {
        var entities = new MemoryEntities();
        await new MailStore(entities).EnsureConfigAsync();
        // Separate store instances model separate in-flight requests racing on the entity revision.
        var users = Enumerable.Range(0, 8).Select(i => $"{i + 1:D8}-0000-0000-0000-000000000000").ToArray();
        var results = await Task.WhenAll(users.Select(async user =>
        {
            try { await new MailStore(entities).ClaimOwnerAsync(user, isAdmin: true); return user; }
            catch (MailFault) { return null; }
        }));
        var winner = Assert.Single(results, r => r != null);
        Assert.Equal(winner, (await new MailStore(entities).GetPolicyAsync()).OwnerUserId);
    }

    [Fact]
    public async Task F6_caller_reports_admin_role_from_the_roles_claim()
    {
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
            new System.Security.Claims.Claim("sub", Harness.Owner),
            new System.Security.Claims.Claim("roles", "[\"admin\"]", "JSON_ARRAY"),
        ], "Bearer"));
        var caller = await MailCaller.FromAsync(principal, new MailPolicy(null, null), (_, _, _, _) => Task.FromResult(false), default);
        Assert.True(caller.IsAdmin);
        Assert.False((await MailCaller.FromAsync(CallerTests.User(), new MailPolicy(null, null), (_, _, _, _) => Task.FromResult(false), default)).IsAdmin);
    }

    // ── 3: ReDoS ────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> AdversarialHtml() =>
    [
        ["unclosed anchors", string.Concat(Enumerable.Repeat("<a href=x>", 110_000))],
        ["unclosed scripts", string.Concat(Enumerable.Repeat("<script>", 130_000))],
        ["unclosed comments", string.Concat(Enumerable.Repeat("<!--", 260_000))],
        ["unterminated tags", string.Concat(Enumerable.Repeat("<br", 350_000))],
        ["anchor attribute decoys", string.Concat(Enumerable.Repeat("<a hre", 175_000)) + ">"],
        ["nested decoys", string.Concat(Enumerable.Repeat("<a href='https://e.com/<style'>x", 32_000))],
        ["url-like runs", string.Concat(Enumerable.Repeat("www.", 260_000))],
        ["mixed", string.Concat(Enumerable.Repeat("<style><a href=\"https://x.example/", 30_000))],
    ];

    [Theory]
    [MemberData(nameof(AdversarialHtml))]
    public void F3_adversarial_one_megabyte_html_is_processed_in_under_a_second(string name, string html)
    {
        Assert.True(html.Length >= 1_000_000, name);
        MailMessageParser.SanitizeHtml("<p>warm up <a href=\"https://example.com\">x</a></p>");
        var watch = Stopwatch.StartNew();
        var body = MailMessageParser.SanitizeHtml(html);
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"{name}: {watch.Elapsed.TotalMilliseconds} ms");
        Assert.True(body.Truncated);
        Assert.True(body.Text.Length <= MailMessageParser.BodyCap);
        Assert.True(body.Links.Count <= MailMessageParser.LinkCap);
    }

    [Fact]
    public void F3_adversarial_plain_text_and_oversized_base64_parts_are_capped_fast()
    {
        var watch = Stopwatch.StartNew();
        var text = MailMessageParser.SanitizeText(string.Concat(Enumerable.Repeat("http://", 200_000)));
        var huge = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('a', 3_000_000))).Replace('+', '-').Replace('/', '_');
        var body = MailMessageParser.Body(new MailMessageParser.Part("text/plain", null, huge, null, null, false));
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"{watch.Elapsed.TotalMilliseconds} ms");
        Assert.True(text.Truncated);
        Assert.True(body.Truncated);
        Assert.Equal(MailMessageParser.BodyCap, body.Text.Length);
    }

    [Fact]
    public void F3_body_without_content_reports_unavailable_instead_of_throwing()
    {
        var body = MailMessageParser.Body(new MailMessageParser.Part("text/html", null, null, "att-1", null, false));
        Assert.Equal("unavailable", body.Source);
        Assert.True(body.Truncated);
        Assert.Equal("unavailable: text/html", MailMessageParser.Unavailable("text/html").Source);
    }

    [Fact]
    public void F3_linear_html_conversion_keeps_previous_semantics()
    {
        var body = MailMessageParser.SanitizeHtml("""
            <HEAD><TITLE>t</TITLE></HEAD><SCRIPT>bad()</SCRIPT><Style>.x{}</Style>
            <P>One</P><ul><li>Two</li></ul><A HREF="https://example.com/a">Anchor <b>bold</b></A><a href=https://example.org/b>bare</a>
            <!-- c --><textarea>hidden</textarea>tail
            """);
        Assert.Equal("One\n\n- Two\n\nAnchor bold [link 1] bare [link 2]\ntail", body.Text);
        Assert.Equal(["https://example.com/a", "https://example.org/b"], body.Links.Select(l => l.Href));
        Assert.Equal("Anchor bold", body.Links[0].Text);
    }

    // ── 7: marker and markdown injection ─────────────────────────────────────

    [Theory]
    [InlineData("![](//attacker.example/p.png)")]
    [InlineData("[click](https://evil.example)")]
    [InlineData("[x](evil.tld)")]
    [InlineData("mailto:victim@example.com")]
    [InlineData("**urgent** _now_ `code`")]
    [InlineData("<img src=x>")]
    [InlineData("javascript:alert(1)")]
    public void F7_live_fields_escape_markdown_and_strip_every_link_form(string hostile)
    {
        var field = MailLiveFormatter.Field(hostile);
        Assert.DoesNotContain("//", field);
        Assert.DoesNotContain("mailto", field);
        Assert.DoesNotContain("javascript", field);
        Assert.DoesNotContain("<", field);
        Assert.DoesNotContain("](", field.Replace("\\](", ""));
        for (var i = 0; i < field.Length; i++)
        {
            if ("[]()!*`_".Contains(field[i]) && !IsEscaped(field, i) && !IsRemovedMarker(field, i))
                Assert.Fail($"unescaped '{field[i]}' at {i} in: {field}");
        }
    }

    [Fact]
    public void F7_plain_words_with_colons_survive_and_escapes_are_exact()
    {
        Assert.Equal("Re:Meeting Ticket:123", MailLiveFormatter.Field("Re:Meeting Ticket:123"));
        Assert.Equal(@"\[urgent\] 50% off\!", MailLiveFormatter.Field("[urgent] 50% off!"));
        Assert.Equal("see [link removed] now", MailLiveFormatter.Field("see https://example.com/x now"));
        var capped = MailLiveFormatter.Field(new string('a', MailLiveFormatter.FieldCap - 2) + "**********");
        Assert.EndsWith("…", capped);
        Assert.False(capped[^2] == '\\' && !IsEscaped(capped, capped.Length - 2), capped);
    }

    [Fact]
    public void F7_control_markers_in_input_cannot_spoof_link_markers()
    {
        var spoof = "Pay here \u0001LINK1\u0002 or \u0001LINKX\u0002 https://real.example/x \u0007";
        var text = MailMessageParser.SanitizeText(spoof);
        Assert.Equal("Pay here LINK1 or LINKX [link 1]", text.Text);
        Assert.Equal("https://real.example/x", Assert.Single(text.Links).Href);

        var html = MailMessageParser.SanitizeHtml("<p>\u0001LINK7\u0002</p><a href=\"https://a.example\">a\u0002</a>");
        Assert.DoesNotContain("[link 7]", html.Text);
        Assert.DoesNotContain('\u0001', html.Text);
        Assert.Equal("LINK7\na [link 1]", html.Text);

        Assert.Equal("From x", MailLiveFormatter.Field("From\u0001 x"));
    }

    private static bool IsEscaped(string s, int i)
    {
        var slashes = 0;
        for (var j = i - 1; j >= 0 && s[j] == '\\'; j--) slashes++;
        return slashes % 2 == 1;
    }

    private static bool IsRemovedMarker(string s, int i)
    {
        const string marker = "[link removed]";
        for (var start = Math.Max(0, i - marker.Length + 1); start <= i; start++)
            if (string.CompareOrdinal(s, start, marker, 0, marker.Length) == 0) return true;
        return false;
    }

    // ── 5 and 9a/b: loopback socket listener ────────────────────────────────

    private static async Task<(Harness H, MailConsent Consent, JsonObject Started)> StartAsync(Func<TcpListener>? factory = null)
    {
        var h = new Harness();
        await h.ConfigureAsync(connected: false);
        h.Google.Json(HttpMethod.Post, "https://oauth2.googleapis.com/token",
            $$"""{"access_token":"a","refresh_token":"r","expires_in":3599,"scope":"{{MailData.ReadonlyScope}}"}""");
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/profile", """{"emailAddress":"user@example.com","historyId":"1"}""");
        var consent = new MailConsent(h.Store, h.OAuth, h.Service, createListener: factory);
        return (h, consent, await consent.StartAsync(Harness.Owner, default));
    }

    private static (string State, int Port) Parse(JsonObject started)
    {
        var q = QueryHelpers.ParseQuery(new Uri(started["authUrl"]!.GetValue<string>()).Query);
        return (q["state"].ToString(), new Uri(q["redirect_uri"].ToString()).Port);
    }

    [Fact]
    public async Task F5_listener_failure_degrades_to_a_paste_only_attempt()
    {
        var (h, consent, started) = await StartAsync(() => throw new SocketException((int)SocketError.AccessDenied));
        Assert.False(started["listening"]!.GetValue<bool>());
        Assert.Equal("paste-only", started["mode"]!.GetValue<string>());
        Assert.True(consent.Pending);
        var (state, port) = Parse(started);
        var status = await consent.CompleteFromRedirectUrlAsync(Harness.Owner,
            $"http://127.0.0.1:{port}/?state={Uri.EscapeDataString(state)}&code=c", default);
        Assert.True(status["connected"]!.GetValue<bool>());
        Assert.NotNull(await h.Store.GetAccountAsync());
    }

    [Fact]
    public async Task F5_listener_is_a_loopback_socket_that_rejects_malformed_requests_without_consuming_the_attempt()
    {
        TcpListener? created = null;
        var (_, consent, started) = await StartAsync(() => created = new TcpListener(IPAddress.Loopback, 0));
        Assert.True(started["listening"]!.GetValue<bool>());
        Assert.Equal(IPAddress.Loopback, ((IPEndPoint)created!.LocalEndpoint).Address);
        var (_, port) = Parse(started);

        foreach (var raw in new[] { "POST / HTTP/1.1\r\nHost: x\r\n\r\n", "garbage\r\n\r\n", "GET /other HTTP/1.1\r\n\r\n" })
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(raw));
            var response = await new StreamReader(stream).ReadToEndAsync();
            Assert.Matches("^HTTP/1.1 (400|404) ", response);
        }
        Assert.True(consent.Pending);
        consent.Cancel();
    }

    [Fact]
    public async Task F9a_storage_failure_during_completion_answers_the_browser_and_closes_the_attempt()
    {
        var (h, consent, started) = await StartAsync();
        var (state, port) = Parse(started);
        h.Entities.FailWrite = (type, _) => type == MailData.AccountType ? new InvalidOperationException("database unavailable") : null;

        using var browser = new HttpClient();
        var response = await browser.GetAsync($"http://127.0.0.1:{port}/?state={Uri.EscapeDataString(state)}&code=c");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var page = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("database", page);
        await Task.Delay(100);
        Assert.False(consent.Pending);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => browser.GetAsync($"http://127.0.0.1:{port}/?state=x"));
    }

    [Fact]
    public async Task F9b_cancel_and_restart_never_throw_on_a_closed_attempt()
    {
        var (_, consent, _) = await StartAsync();
        consent.Cancel();
        consent.Cancel();
        await consent.StartAsync(Harness.Owner, default);
        await consent.StartAsync(Harness.Owner, default); // replaces and closes the previous attempt
        consent.Dispose();
        Assert.False(consent.Pending);
    }

    // ── 9c/9d: async invalidation, sync serialized with reconnect ────────────

    [Fact]
    public async Task F9d_a_sync_in_flight_cannot_write_the_old_mailbox_cursor_after_a_reconnect()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        h.TokenOk();
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/labels/INBOX", """{"messagesUnread":0}""");
        h.Google.Json(HttpMethod.Post, "https://oauth2.googleapis.com/revoke", "{}");
        var historyEntered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        h.Google.On(HttpMethod.Get, Harness.Gmail + "/history", (_, _) =>
        {
            historyEntered.TrySetResult();
            release.Task.Wait();
            return FakeGoogle.Response(HttpStatusCode.OK, """{"historyId":"OLD-MAILBOX-CURSOR"}""");
        });
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/profile", """{"emailAddress":"second@example.com","historyId":"77"}""");

        var sync = Task.Run(() => h.Service.SyncOnceAsync(default));
        await historyEntered.Task;
        var connect = Task.Run(() => h.Service.ConnectAsync(Harness.Owner,
            new TokenGrant("a2", "refresh-2", DateTimeOffset.UtcNow.AddHours(1), MailData.ReadonlyScope), default));
        await Task.Delay(100);
        Assert.False(connect.IsCompleted); // waits for the in-flight sync
        release.SetResult();
        await Task.WhenAll(sync, connect);

        var account = (await h.Store.GetAccountAsync())!;
        Assert.Equal("second@example.com", account.Email);
        Assert.Null(account.HistoryId);
        Assert.False(account.BackfillComplete);
    }

    [Fact]
    public async Task F9c_token_invalidation_is_async_and_forces_a_refresh()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        h.TokenOk();
        await h.Tokens.GetAccessTokenAsync(default);
        await h.Tokens.InvalidateAsync();
        await h.Tokens.GetAccessTokenAsync(default);
        Assert.Equal(2, h.Tokens.RefreshCount);
    }

    // ── 9e: externalized bodies ─────────────────────────────────────────────

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Externalized(string mime, long size, string? filename = null) => new JsonObject
    {
        ["id"] = "m5",
        ["threadId"] = "t5",
        ["labelIds"] = new JsonArray("INBOX"),
        ["payload"] = new JsonObject
        {
            ["mimeType"] = "multipart/mixed",
            ["headers"] = new JsonArray(new JsonObject { ["name"] = "Subject", ["value"] = "Big" }),
            ["parts"] = new JsonArray(
                new JsonObject
                {
                    ["mimeType"] = mime,
                    ["filename"] = filename ?? "",
                    ["headers"] = new JsonArray(new JsonObject { ["name"] = "Content-Type", ["value"] = mime + "; charset=utf-8" }),
                    ["body"] = new JsonObject { ["attachmentId"] = "ANGjdJ_external-1", ["size"] = size },
                }),
        },
    }.ToJsonString();

    [Fact]
    public async Task F9e_externalized_html_body_is_fetched_with_attachments_get()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        h.TokenOk();
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/messages/m5?format=full", Externalized("text/html", 900_000));
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/messages/m5/attachments/ANGjdJ_external-1",
            new JsonObject { ["size"] = 40, ["data"] = B64("<p>Large <a href=\"https://example.com/z\">body</a></p>") }.ToJsonString());

        var message = await h.Service.MessageAsync("m5", Harness.OwnerCaller, default);
        Assert.Equal("Large body [link 1]", message["body"]!.GetValue<string>());
        Assert.Equal("text/html", message["bodySource"]!.GetValue<string>());
        Assert.Single(h.Google.Requests, r => r.Url.Contains("/attachments/ANGjdJ_external-1"));
    }

    [Fact]
    public async Task F9e_oversized_externalized_body_is_not_downloaded_and_reported_unavailable()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        h.TokenOk();
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/messages/m5?format=full", Externalized("text/html", 50_000_000));
        var message = await h.Service.MessageAsync("m5", Harness.OwnerCaller, default);
        Assert.Equal("", message["body"]!.GetValue<string>());
        Assert.True(message["truncated"]!.GetValue<bool>());
        Assert.StartsWith("unavailable", message["bodySource"]!.GetValue<string>());
        Assert.DoesNotContain(h.Google.Requests, r => r.Url.Contains("/attachments/"));
    }

    [Fact]
    public async Task F9e_a_named_attachment_is_never_treated_as_the_body()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        h.TokenOk();
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/messages/m5?format=full", Externalized("text/plain", 100, "notes.txt"));
        var message = await h.Service.MessageAsync("m5", Harness.OwnerCaller, default);
        Assert.Equal("none", message["bodySource"]!.GetValue<string>());
        Assert.DoesNotContain(h.Google.Requests, r => r.Url.Contains("/attachments/"));
    }
}
