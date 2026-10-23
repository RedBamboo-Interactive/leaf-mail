using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Mail;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Leaf.Plugins.Mail.Tests;

/// <summary>PDF fixtures are generated in memory with PdfPig's writer; nothing touches disk.</summary>
internal static class PdfFixtures
{
    public static byte[] Text(params string[] lines) => Pages(1, lines);

    public static byte[] Pages(int pages, params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var p = 0; p < pages; p++)
        {
            var page = builder.AddPage(PageSize.A4);
            var y = 760.0;
            foreach (var line in lines) { page.AddText(line, 11, new PdfPoint(40, y), font); y -= 16; }
        }
        return builder.Build();
    }

    public static byte[] ImageOnly()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4).AddPng(png, new PdfRectangle(50, 50, 400, 400));
        return builder.Build();
    }

    /// <summary>A real PDF whose trailer declares a Standard security handler with a password.</summary>
    public static byte[] Encrypted()
    {
        var text = Encoding.Latin1.GetString(Text("Secret statement"));
        var trailer = text.IndexOf("trailer", StringComparison.Ordinal);
        var open = text.IndexOf("<<", trailer, StringComparison.Ordinal);
        text = text[..(open + 2)] + " /Encrypt << /Filter /Standard /V 2 /R 3 /Length 128 /O <"
            + new string('1', 64) + "> /U <" + new string('2', 64) + "> /P -4 >> " + text[(open + 2)..];
        return Encoding.Latin1.GetBytes(text);
    }

    public static byte[] Malformed() => Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Type /Catalog >> broken stream without xref");
}

public sealed class AttachmentExtractionTests
{
    [Fact]
    public void Pdf_text_is_extracted()
    {
        var result = AttachmentExtractor.Extract(PdfFixtures.Text("Delivery notice: parcel 12345 arrives Friday", "Pick up at the post office"),
            "application/pdf", "notice.pdf", null);
        Assert.Equal(AttachmentStatus.Ok, result.Status);
        Assert.Contains("parcel 12345 arrives Friday", result.Content);
        Assert.Contains("Pick up at the post office", result.Content);
        Assert.Equal(1, result.Pages);
        Assert.False(result.IsHtml);
    }

    [Fact]
    public void Encrypted_pdf_is_unreadable_with_a_clear_reason()
    {
        var result = AttachmentExtractor.Extract(PdfFixtures.Encrypted(), "application/pdf", "statement.pdf", null);
        Assert.Equal(AttachmentStatus.Unreadable, result.Status);
        Assert.Contains("encrypted", result.Detail);
        Assert.Equal("", result.Content);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("truncated")]
    [InlineData("not-a-pdf")]
    public void Malformed_pdf_is_unreadable(string kind)
    {
        var bytes = kind switch
        {
            "malformed" => PdfFixtures.Malformed(),
            "truncated" => PdfFixtures.Text("cut")[..200],
            _ => Encoding.ASCII.GetBytes("%PDF-1.4 this is not really a pdf"),
        };
        var result = AttachmentExtractor.Extract(bytes, "application/pdf", "x.pdf", null);
        Assert.Equal(AttachmentStatus.Unreadable, result.Status);
        Assert.Contains("malformed", result.Detail);
    }

    [Fact]
    public void Image_only_pdf_reports_no_text_without_ocr()
    {
        var result = AttachmentExtractor.Extract(PdfFixtures.ImageOnly(), "application/pdf", "scan.pdf", null);
        Assert.Equal(AttachmentStatus.NoText, result.Status);
        Assert.Contains("OCR", result.Detail);
        Assert.Equal(1, result.Pages);
    }

    [Fact]
    public void Huge_pdf_is_bounded_by_page_and_text_caps()
    {
        var line = new string('x', 90);
        var bytes = PdfFixtures.Pages(AttachmentExtractor.MaxPdfPages + 20, Enumerable.Repeat(line, 40).ToArray());
        var result = AttachmentExtractor.Extract(bytes, "application/pdf", "huge.pdf", null);
        Assert.Equal(AttachmentStatus.Ok, result.Status);
        Assert.True(result.Truncated);
        Assert.True(result.Content.Length <= AttachmentExtractor.RawTextCap);
        Assert.Equal(AttachmentExtractor.MaxPdfPages + 20, result.Pages);
    }

    [Fact]
    public void Plain_text_and_csv_are_decoded_with_their_charset_and_capped()
    {
        var latin = Encoding.Latin1.GetBytes("Zürich, Grüße");
        Assert.Equal("Zürich, Grüße", AttachmentExtractor.Extract(latin, "text/plain", "a.txt", "iso-8859-1").Content);
        var csv = AttachmentExtractor.Extract(Encoding.UTF8.GetBytes("item,price\nparcel,12.50\n"), "text/csv", "r.csv", null);
        Assert.Equal(AttachmentStatus.Ok, csv.Status);
        Assert.Contains("parcel,12.50", csv.Content);
        var big = AttachmentExtractor.Extract(Encoding.UTF8.GetBytes(new string('a', AttachmentExtractor.RawTextCap + 100)), "text/plain", null, null);
        Assert.True(big.Truncated);
        Assert.Equal(AttachmentExtractor.RawTextCap, big.Content.Length);
        Assert.Equal(AttachmentStatus.NoText, AttachmentExtractor.Extract("   \n"u8.ToArray(), "text/plain", null, null).Status);
    }

    [Fact]
    public void Html_is_returned_as_html_for_the_shared_sanitizer()
    {
        var result = AttachmentExtractor.Extract("<p>Invoice <script>x()</script></p>"u8.ToArray(), "text/html", "i.html", null);
        Assert.True(result.IsHtml);
        Assert.Equal("Invoice", MailMessageParser.SanitizeHtml(result.Content).Text);
    }

    [Fact]
    public void Calendar_is_unfolded_into_readable_lines()
    {
        var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:Parcel pick-up\\, counter 3\r\nDTSTART:20261024T090000Z\r\n"
            + "LOCATION:Post office\r\nDESCRIPTION:Bring ID\\nand the notice\r\nORGANIZER;CN=Post:mailto:noreply@example.com\r\n"
            + "X-PRIVATE:ignored\r\nUID:abc\r\nATTENDEE:mailto:user@example.com\r\n A-folded\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var result = AttachmentExtractor.Extract(Encoding.UTF8.GetBytes(ics), "text/calendar", "invite.ics", null);
        Assert.Equal(AttachmentStatus.Ok, result.Status);
        Assert.Equal("Event\nSummary: Parcel pick-up, counter 3\nDtstart: 20261024T090000Z\nLocation: Post office\n"
            + "Description: Bring ID\nand the notice\nOrganizer: noreply@example.com\nAttendee: user@example.comA-folded\n", result.Content);
    }

    [Fact]
    public void Rfc822_message_is_rendered_as_text_with_headers()
    {
        var eml = "From: Shop <shop@example.com>\r\nTo: user@example.com\r\nSubject: =?utf-8?Q?Re=C3=A7u?=\r\n"
            + "Date: Fri, 23 Oct 2026 10:00:00 +0000\r\nMIME-Version: 1.0\r\nContent-Type: multipart/alternative; boundary=b\r\n\r\n"
            + "--b\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\nTotal: 12=2E50 CHF\r\n"
            + "--b\r\nContent-Type: text/html\r\n\r\n<p>html</p>\r\n--b--\r\n";
        var result = AttachmentExtractor.Extract(Encoding.ASCII.GetBytes(eml), "message/rfc822", "fwd.eml", null);
        Assert.Equal(AttachmentStatus.Ok, result.Status);
        Assert.Contains("Subject: Reçu", result.Content);
        Assert.Contains("From: \"Shop\" <shop@example.com>", result.Content);
        Assert.Contains("Total: 12.50 CHF", result.Content);
    }

    [Theory]
    [InlineData("image/png", "photo.png")]
    [InlineData("application/zip", "files.zip")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "a.docx")]
    [InlineData("application/x-msdownload", "setup.exe")]
    public void Other_types_are_unsupported_and_never_opened(string mime, string filename)
    {
        var result = AttachmentExtractor.Extract([1, 2, 3, 4], mime, filename, null);
        Assert.Equal(AttachmentStatus.Unsupported, result.Status);
        Assert.Equal(mime, result.Kind);
    }

    [Fact]
    public void Generic_octet_stream_is_refined_by_magic_bytes_or_extension_only()
    {
        Assert.Equal(AttachmentStatus.Ok, AttachmentExtractor.Extract(PdfFixtures.Text("magic"), "application/octet-stream", "noext", null).Status);
        Assert.Equal("text/calendar", AttachmentExtractor.Kind("application/octet-stream", "x.ics", []));
        Assert.Equal("image/png", AttachmentExtractor.Kind("image/png", "evil.pdf", "%PDF-"u8)); // a declared type is not overridden
    }
}

public sealed class AttachmentServiceTests
{
    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string MessageWithAttachments(long pdfSize = 1000) => new JsonObject
    {
        ["id"] = "m7",
        ["threadId"] = "t7",
        ["labelIds"] = new JsonArray("INBOX"),
        ["payload"] = new JsonObject
        {
            ["mimeType"] = "multipart/mixed",
            ["headers"] = new JsonArray(new JsonObject { ["name"] = "Subject", ["value"] = "Your delivery" }),
            ["parts"] = new JsonArray(
                new JsonObject
                {
                    ["partId"] = "0", ["mimeType"] = "text/plain", ["filename"] = "",
                    ["headers"] = new JsonArray(new JsonObject { ["name"] = "Content-Type", ["value"] = "text/plain; charset=utf-8" }),
                    ["body"] = new JsonObject { ["data"] = B64("See attached."u8.ToArray()), ["size"] = 13 },
                },
                new JsonObject
                {
                    ["partId"] = "1", ["mimeType"] = "application/pdf", ["filename"] = "notice.pdf",
                    ["headers"] = new JsonArray(new JsonObject { ["name"] = "Content-Disposition", ["value"] = "attachment; filename=notice.pdf" }),
                    ["body"] = new JsonObject { ["attachmentId"] = "ANGjdJ_pdf", ["size"] = pdfSize },
                },
                new JsonObject
                {
                    ["partId"] = "2", ["mimeType"] = "image/png", ["filename"] = "",
                    ["headers"] = new JsonArray(
                        new JsonObject { ["name"] = "Content-ID", ["value"] = "<logo@example>" },
                        new JsonObject { ["name"] = "Content-Disposition", ["value"] = "inline" }),
                    ["body"] = new JsonObject { ["attachmentId"] = "ANGjdJ_logo", ["size"] = 2048 },
                },
                new JsonObject
                {
                    ["partId"] = "3", ["mimeType"] = "text/calendar", ["filename"] = "invite.ics",
                    ["headers"] = new JsonArray(new JsonObject { ["name"] = "Content-Type", ["value"] = "text/calendar; charset=utf-8" }),
                    ["body"] = new JsonObject { ["data"] = B64("BEGIN:VEVENT\r\nSUMMARY:Pick-up\r\nEND:VEVENT\r\n"u8.ToArray()), ["size"] = 40 },
                }),
        },
    }.ToJsonString();

    private static async Task<Harness> ConnectedAsync(long pdfSize = 1000)
    {
        var h = new Harness();
        await h.ConfigureAsync();
        h.TokenOk();
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/messages/m7?format=full", MessageWithAttachments(pdfSize));
        return h;
    }

    [Fact]
    public async Task Listing_reports_filename_type_size_and_inline_images()
    {
        var h = await ConnectedAsync();
        var result = await h.Service.AttachmentsAsync("m7", default);
        var items = result["items"]!.AsArray().Select(i => i!.AsObject()).ToArray();
        Assert.Equal(3, items.Length); // the text/plain body is not an attachment
        Assert.Equal(("notice.pdf", "application/pdf", 1000L, false),
            (items[0]["filename"]!.GetValue<string>(), items[0]["mimeType"]!.GetValue<string>(), items[0]["size"]!.GetValue<long>(), items[0]["inline"]!.GetValue<bool>()));
        Assert.Equal(("1", "ANGjdJ_pdf"), (items[0]["partId"]!.GetValue<string>(), items[0]["attachmentId"]!.GetValue<string>()));
        Assert.True(items[1]["inline"]!.GetValue<bool>());
        Assert.Equal("image/png", items[1]["mimeType"]!.GetValue<string>());
        Assert.Equal("3", items[2]["partId"]!.GetValue<string>()); // small part Gmail returned inline: no attachmentId
        Assert.Null(items[2]["attachmentId"]);
    }

    [Fact]
    public async Task Pdf_attachment_text_is_fetched_live_extracted_and_never_persisted()
    {
        var h = await ConnectedAsync();
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/messages/m7/attachments/ANGjdJ_pdf",
            new JsonObject { ["size"] = 900, ["data"] = B64(PdfFixtures.Text("Parcel 12345: https://example.com/track")) }.ToJsonString());
        var entitiesBefore = (await h.Entities.QueryAsync(new Leaf.Sdk.EntityQuery { TypeSlug = MailData.AccountType })).Single().Data.ToJsonString();

        var result = await h.Service.AttachmentTextAsync("m7", "ANGjdJ_pdf", Harness.OwnerCaller, default);

        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.Equal("Parcel 12345: [link 1]", result["text"]!.GetValue<string>());
        Assert.Equal("https://example.com/track", result["links"]![0]!["href"]!.GetValue<string>());
        Assert.True(result["untrusted"]!.GetValue<bool>());
        Assert.False(result["sentinel"]!["available"]!.GetValue<bool>());
        Assert.Equal("not_installed", result["sentinel"]!["reason"]!.GetValue<string>());
        // No persistence: no records, no entity changes.
        Assert.Empty(h.Records.Once);
        Assert.Equal(entitiesBefore, (await h.Entities.QueryAsync(new Leaf.Sdk.EntityQuery { TypeSlug = MailData.AccountType })).Single().Data.ToJsonString());
    }

    [Fact]
    public async Task Inline_calendar_part_is_extracted_without_a_download()
    {
        var h = await ConnectedAsync();
        var result = await h.Service.AttachmentTextAsync("m7", "inline.3", Harness.OwnerCaller, default);
        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.Equal("Event\nSummary: Pick-up", result["text"]!.GetValue<string>());
        Assert.DoesNotContain(h.Google.Requests, r => r.Url.Contains("/attachments/"));
    }

    [Fact]
    public async Task Oversized_attachment_is_refused_before_download()
    {
        var h = await ConnectedAsync(pdfSize: AttachmentExtractor.MaxDownloadBytes + 1);
        var result = await h.Service.AttachmentTextAsync("m7", "ANGjdJ_pdf", Harness.OwnerCaller, default);
        Assert.Equal("too_large", result["status"]!.GetValue<string>());
        Assert.Equal("", result["text"]!.GetValue<string>());
        Assert.DoesNotContain(h.Google.Requests, r => r.Url.Contains("/attachments/"));
    }

    [Fact]
    public async Task Attachment_larger_than_its_declared_size_is_cut_off_while_downloading()
    {
        var h = await ConnectedAsync(pdfSize: 1000); // the size field lies
        var huge = new byte[AttachmentExtractor.MaxDownloadBytes + 64 * 1024];
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/messages/m7/attachments/ANGjdJ_pdf", new JsonObject { ["data"] = B64(huge) }.ToJsonString());
        var result = await h.Service.AttachmentTextAsync("m7", "ANGjdJ_pdf", Harness.OwnerCaller, default);
        Assert.Equal("too_large", result["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unsupported_type_and_unknown_attachment_are_reported()
    {
        var h = await ConnectedAsync();
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/messages/m7/attachments/ANGjdJ_logo", new JsonObject { ["data"] = B64([137, 80, 78, 71]) }.ToJsonString());
        var image = await h.Service.AttachmentTextAsync("m7", "ANGjdJ_logo", Harness.OwnerCaller, default);
        Assert.Equal("unsupported", image["status"]!.GetValue<string>());
        Assert.Equal("image/png", image["kind"]!.GetValue<string>());
        var missing = await Assert.ThrowsAsync<MailFault>(() => h.Service.AttachmentTextAsync("m7", "ANGjdJ_other", Harness.OwnerCaller, default));
        Assert.Equal(404, missing.Status);
    }
}
