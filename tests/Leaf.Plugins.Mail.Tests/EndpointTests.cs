using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Leaf.Plugins.Mail;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Leaf.Plugins.Mail.Tests;

/// <summary>End-to-end through the real route group and caller filter (no kernel, no network).</summary>
public sealed class EndpointTests : IAsyncLifetime
{
    private readonly Harness _h = new();
    private IHost? _host;
    private HttpClient _client = null!;
    private static readonly Dictionary<string, ClaimsPrincipal> Principals = new();

    public async Task InitializeAsync()
    {
        await _h.ConfigureAsync(approveLive: false);
        _h.Entities.Add("discussion", "d1", new JsonObject
        {
            ["discussion_id"] = "disc-private-1", ["app"] = "nova", ["owner_id"] = Harness.Owner, ["agent"] = Harness.Agent,
        });
        _h.Entities.Add("agent", "nova", new JsonObject { ["avatar_url"] = "/api/assets/nova.png" });
        _h.Entities.Add("agent", "private-owned", new JsonObject
        {
            ["confidential"] = true, ["owner_id"] = Harness.Owner, ["avatar"] = "https://tracker.example/p.png",
        });
        _h.Entities.Add("agent", "private-other", new JsonObject
        {
            ["confidential"] = true, ["owner_id"] = "33333333-3333-3333-3333-333333333333",
        });
        _host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton(_h.Store);
                services.AddSingleton(_h.Service);
                services.AddSingleton(new MailConsent(_h.Store, _h.OAuth, _h.Service, startListener: false));
                services.AddSingleton(new MailPoller(_h.Service));
                services.AddSingleton(new MailAgentSkills(_h.Entities));
            })
            .Configure(app =>
            {
                app.Use(async (ctx, next) =>
                {
                    // Stand-in for host authentication: the test names a principal.
                    var key = ctx.Request.Headers["X-Test-Principal"].ToString();
                    ctx.User = Principals.TryGetValue(key, out var principal) ? principal : new ClaimsPrincipal(new ClaimsIdentity());
                    await next();
                });
                app.UseRouting();
                app.UseEndpoints(endpoints => MailEndpoints.Map(endpoints.MapGroup("/api/extensions/mail")));
            })).StartAsync();
        _client = _host.GetTestClient();
        lock (Principals)
        {
            Principals["owner"] = CallerTests.User();
            Principals["local"] = CallerTests.User(authType: "LocalDefault");
            Principals["agent"] = CallerTests.Execution(CallerTests.NovaAgent());
            Principals["discord"] = CallerTests.Execution(CallerTests.DiscordNovaAgent());
            Principals["automation"] = CallerTests.Execution(CallerTests.AutomationAgent());
            Principals["other-user"] = CallerTests.User("33333333-3333-3333-3333-333333333333");
            Principals["other-agent"] = CallerTests.Execution(CallerTests.NovaAgent(agent: "44444444-4444-4444-4444-444444444444"));
        }
    }

    public async Task DisposeAsync()
    {
        if (_host != null) await _host.StopAsync();
        _host?.Dispose();
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string principal, string? json = null)
    {
        var request = new HttpRequestMessage(method, "/api/extensions/mail" + path);
        request.Headers.Add("X-Test-Principal", principal);
        if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return _client.SendAsync(request);
    }

    [Theory]
    [InlineData("local", HttpStatusCode.Unauthorized)]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("other-agent", HttpStatusCode.Forbidden)]
    [InlineData("owner", HttpStatusCode.OK)]
    [InlineData("agent", HttpStatusCode.OK)]
    [InlineData("discord", HttpStatusCode.OK)]
    [InlineData("automation", HttpStatusCode.OK)]
    public async Task Status_and_discovery_apply_the_caller_filter(string principal, HttpStatusCode expected)
    {
        Assert.Equal(expected, (await Send(HttpMethod.Get, "/status", principal)).StatusCode);
        Assert.Equal(expected, (await Send(HttpMethod.Get, "/discover", principal)).StatusCode);
        var response = await Send(HttpMethod.Get, "/search?q=subject:receipt", principal);
        if (expected != HttpStatusCode.OK) Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Status_reports_connection_without_secrets()
    {
        var body = await (await Send(HttpMethod.Get, "/status", "owner")).Content.ReadAsStringAsync();
        Assert.Contains("\"connected\":true", body);
        Assert.Contains("user@example.com", body);
        Assert.DoesNotContain("refresh-token-1", body);
        Assert.DoesNotContain(Harness.ClientSecret, body);
        Assert.Equal("no-store", (await Send(HttpMethod.Get, "/status", "owner")).Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("POST", "/oauth/start", null)]
    [InlineData("POST", "/disconnect", null)]
    [InlineData("PUT", "/config", "{\"live_approved\":true}")]
    [InlineData("PUT", "/client", "{}")]
    [InlineData("GET", "/agents", null)]
    public async Task Settings_routes_reject_agent_executions(string method, string path, string? json)
    {
        // From any surface: settings and OAuth stay owner-session only.
        foreach (var principal in new[] { "agent", "discord", "automation" })
        {
            var response = await Send(new HttpMethod(method), path, principal, json);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("owner_user_required", await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData("local", HttpStatusCode.Unauthorized)]
    [InlineData("other-user", HttpStatusCode.Forbidden)]
    [InlineData("agent", HttpStatusCode.Forbidden)]
    [InlineData("discord", HttpStatusCode.Forbidden)]
    [InlineData("automation", HttpStatusCode.Forbidden)]
    [InlineData("other-agent", HttpStatusCode.Forbidden)]
    public async Task Live_approval_toggle_rejects_everyone_but_the_owner_session(string principal, HttpStatusCode expected)
    {
        Assert.Equal(expected, (await Send(HttpMethod.Put, "/config", principal, """{"live_approved":true}""")).StatusCode);
        Assert.Equal(LiveApproval.Off, await _h.Store.GetLiveApprovalAsync());
    }

    [Fact]
    public async Task Live_approval_toggle_is_written_by_the_owner_session()
    {
        var on = await Send(HttpMethod.Put, "/config", "owner", """{"live_approved":true}""");
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.Contains("\"live_approved\":true", await on.Content.ReadAsStringAsync());
        var approval = await _h.Store.GetLiveApprovalAsync();
        Assert.True(approval.Enabled);
        Assert.Equal(Harness.Owner, approval.ApprovedBy);

        var off = await Send(HttpMethod.Put, "/config", "owner", """{"live_approved":false}""");
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(LiveApproval.Off, await _h.Store.GetLiveApprovalAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, "/config", "owner", """{"live_approved":"yes"}""")).StatusCode);
    }

    [Fact]
    public async Task Agents_lists_the_owners_confidential_agents_with_same_origin_avatars_only()
    {
        var response = await Send(HttpMethod.Get, "/agents", "owner");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["items"]!.AsArray()
            .ToDictionary(i => i!["slug"]!.GetValue<string>(), i => i!.AsObject());
        Assert.Equal(["nova", "private-owned"], items.Keys.Order());
        Assert.Equal("/api/assets/nova.png", items["nova"]["avatar"]!.GetValue<string>());
        Assert.False(items["nova"]["confidential"]!.GetValue<bool>());
        Assert.True(items["private-owned"]["confidential"]!.GetValue<bool>());
        Assert.Null(items["private-owned"]["avatar"]); // off-origin avatar is not passed to the browser
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/agents", "agent")).StatusCode);
    }

    [Fact]
    public async Task Search_validates_max_and_query()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, "/search?q=x&max=51", "owner")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, "/search?q=x&max=0", "owner")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, "/search", "owner")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, "/messages/..%2Fprofile", "owner")).StatusCode);
    }

    [Fact]
    public async Task Search_passes_gmail_syntax_through_and_returns_untrusted_metadata()
    {
        _h.TokenOk();
        _h.Google.Json(HttpMethod.Get, Harness.Gmail + "/messages?q=", """{"messages":[{"id":"m1"}]}""");
        _h.Batch(new() { ["m1"] = Harness.MessageJson("m1", "A <a@example.com>", "Receipt", "INBOX") });
        var response = await Send(HttpMethod.Get, "/search?q=" + Uri.EscapeDataString("from:shop newer_than:1h") + "&max=5", "agent");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.True(json["untrusted"]!.GetValue<bool>());
        Assert.Equal("Receipt", json["items"]![0]!["subject"]!.GetValue<string>());
        Assert.Contains(_h.Google.Requests, r => r.Url.Contains("q=from%3Ashop%20newer_than%3A1h&maxResults=5"));
    }

    private void AttachmentFixtures()
    {
        _h.TokenOk();
        _h.Google.Json(HttpMethod.Get, Harness.Gmail + "/messages/m7?format=full", AttachmentServiceTests.MessageWithAttachments());
        _h.Google.Json(HttpMethod.Get, Harness.Gmail + "/messages/m7/attachments/ANGjdJ_pdf", new JsonObject
        {
            ["data"] = Convert.ToBase64String(PdfFixtures.Text("Delivery notice")).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        }.ToJsonString());
    }

    [Theory]
    [InlineData("local", HttpStatusCode.Unauthorized)]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("other-agent", HttpStatusCode.Forbidden)]
    [InlineData("other-user", HttpStatusCode.Forbidden)]
    [InlineData("owner", HttpStatusCode.OK)]
    [InlineData("agent", HttpStatusCode.OK)]
    [InlineData("discord", HttpStatusCode.OK)]
    [InlineData("automation", HttpStatusCode.OK)]
    public async Task Attachment_routes_apply_the_same_caller_policy_as_messages(string principal, HttpStatusCode expected)
    {
        AttachmentFixtures();
        var list = await Send(HttpMethod.Get, "/messages/m7/attachments", principal);
        Assert.Equal(expected, list.StatusCode);
        var text = await Send(HttpMethod.Get, "/messages/m7/attachments/ANGjdJ_pdf/text", principal);
        Assert.Equal(expected, text.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            var json = JsonNode.Parse(await text.Content.ReadAsStringAsync())!;
            Assert.Equal("Delivery notice", json["text"]!.GetValue<string>());
            Assert.True(json["untrusted"]!.GetValue<bool>());
            Assert.Equal("treat_as_untrusted_data", json["sentinel"]!["handling"]!.GetValue<string>());
        }
        else Assert.DoesNotContain(_h.Google.Requests, r => r.Url.Contains("/attachments/"));
    }

    [Theory]
    [InlineData("/messages/m7/attachments/..%2F..%2Fprofile/text")]
    [InlineData("/messages/m7/attachments/inline.x/text")]
    [InlineData("/messages/m7/attachments/a%20b/text")]
    public async Task Malformed_attachment_ids_are_rejected(string path)
        => Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, path, "owner")).StatusCode);

    [Fact]
    public async Task Binding_an_agent_reports_the_skill_assignment()
    {
        var plugin = (await _h.Entities.GetBySlugAsync("plugin", "mail"))!;
        _h.Entities.AddWithId(Guid.Parse("949dfba3-0e3d-595d-8bf1-e1c0b64c11d8"), "agent-skill", "mail",
            new JsonObject { ["owner_plugin"] = plugin.Id.ToString() });
        var muse = _h.Entities.AddWithId(Guid.Parse("b2000000-0000-0000-0000-000000000002"), "agent", "muse", new JsonObject());

        var response = await Send(HttpMethod.Put, "/config", "owner", $$"""{"agent_id":"{{muse.Id}}"}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var assignments = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["skill_assignment"]!.AsArray();
        Assert.Contains(assignments, a => a!["agentId"]!.GetValue<string>() == muse.Id.ToString() && a["status"]!.GetValue<string>() == "added");
        var skills = (await _h.Entities.GetAsync(muse.Id))!.Data["skills"]!.AsArray().Select(s => s!.GetValue<string>());
        Assert.Equal(["949dfba3-0e3d-595d-8bf1-e1c0b64c11d8"], skills);
        // The bound agent cannot do this for itself: settings are owner-session only.
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, "/config", "agent", $$"""{"agent_id":"{{muse.Id}}"}""")).StatusCode);
    }

    [Fact]
    public async Task Owner_can_bind_agent_and_filters_but_not_unknown_settings()
    {
        var ok = await Send(HttpMethod.Put, "/config", "owner",
            $$"""{"live_excluded_categories":["CATEGORY_PROMOTIONS"],"live_approved":true}""");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(["CATEGORY_PROMOTIONS"], (await _h.Store.GetConfigAsync()).LiveExcludedCategories);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, "/config", "owner", """{"owner_user_id":"x"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, "/config", "owner", """{"agent_id":"55555555-5555-5555-5555-555555555555"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, "/config", "owner", """{"live_excluded_categories":["INBOX"]}""")).StatusCode);
    }

    [Fact]
    public async Task Uploading_a_web_client_is_rejected()
    {
        var response = await Send(HttpMethod.Put, "/client", "owner",
            """{"web":{"client_id":"123456789012-abcdefghijklmnop0123456789abcdef.apps.googleusercontent.com","client_secret":"GOCSPX-test_secret_value"}}""");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Desktop app", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/search?q=subject:receipt")]
    [InlineData("/messages/m1")]
    [InlineData("/threads/t1")]
    [InlineData("/messages/m7/attachments")]
    [InlineData("/messages/m7/attachments/1/text")]
    public async Task Content_routes_return_503_while_the_sentinel_is_disabled_and_status_says_why(string path)
    {
        await _h.SetSentinelStatusAsync("disabled");
        var response = await Send(HttpMethod.Get, path, "agent");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("sentinel_unavailable", body["error"]!["code"]!.GetValue<string>());
        Assert.Contains("Sentinel safety check is unavailable (disabled)", body["error"]!["message"]!.GetValue<string>());
        Assert.Equal(["error"], body.AsObject().Select(p => p.Key).ToArray()); // no mail content at all

        var status = JsonNode.Parse(await (await Send(HttpMethod.Get, "/status", "agent")).Content.ReadAsStringAsync())!;
        Assert.False(status["sentinel"]!["available"]!.GetValue<bool>());
        Assert.Equal("disabled", status["sentinel"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_grant_during_a_read_marks_needs_reauth_and_returns_409()
    {
        _h.Google.Json(HttpMethod.Post, "https://oauth2.googleapis.com/token", """{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        var response = await Send(HttpMethod.Get, "/messages/m1", "owner");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(MailStatus.NeedsReauth, (await _h.Store.GetAccountAsync())!.Status);
        Assert.Single(_h.Live);
    }
}
