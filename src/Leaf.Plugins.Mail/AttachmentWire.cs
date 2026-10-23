using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Leaf.Plugins.Mail;

/// <summary>
/// The extractor helper's protocol, compiled into both sides. Request on stdin: a 4-byte
/// little-endian length and a UTF-8 JSON header <c>{ mimeType, filename, charset }</c>, then a
/// 4-byte length and the attachment bytes. Response on stdout: one JSON object, bounded by the
/// extractor's own text cap. Nothing touches disk.
/// </summary>
public static class AttachmentWire
{
    public const int MaxHeaderBytes = 16 * 1024;
    public const int MaxPayloadBytes = (int)AttachmentExtractor.MaxDownloadBytes;
    /// <summary>Largest response the plugin reads: 512K characters JSON-escaped, plus room.</summary>
    public const int MaxResponseBytes = 8 * 1024 * 1024;

    private static readonly string[] Statuses = [AttachmentStatus.Ok, AttachmentStatus.Unsupported, AttachmentStatus.Unreadable,
        AttachmentStatus.NoText, AttachmentStatus.TooLarge, AttachmentStatus.TooComplex];

    public static async Task WriteRequestAsync(Stream stream, byte[] bytes, string mimeType, string? filename, string? charset, CancellationToken ct)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["mimeType"] = mimeType,
            ["filename"] = filename,
            ["charset"] = charset,
        });
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, header.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(header, ct);
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public static (byte[] Bytes, string MimeType, string? Filename, string? Charset) ReadRequest(Stream stream)
    {
        var header = Frame(stream, MaxHeaderBytes);
        var json = JsonNode.Parse(header) as JsonObject ?? throw new InvalidDataException("header");
        var mimeType = json["mimeType"]?.GetValue<string>() ?? throw new InvalidDataException("mimeType");
        var bytes = Frame(stream, MaxPayloadBytes);
        return (bytes, mimeType, json["filename"]?.GetValue<string>(), json["charset"]?.GetValue<string>());
    }

    private static byte[] Frame(Stream stream, int max)
    {
        var length = new byte[4];
        stream.ReadExactly(length);
        var size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size < 0 || size > max) throw new InvalidDataException("frame size");
        var data = new byte[size];
        stream.ReadExactly(data);
        return data;
    }

    public static byte[] WriteResult(ExtractedAttachment result)
    {
        var json = new JsonObject
        {
            ["status"] = result.Status,
            ["content"] = result.Content,
            ["isHtml"] = result.IsHtml,
            ["truncated"] = result.Truncated,
            ["kind"] = result.Kind,
            ["detail"] = result.Detail,
            ["pages"] = result.Pages,
        };
        if (result.Signals is { Count: > 0 } signals)
            json["signals"] = new JsonArray(signals.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        if (result.Stats is { } stats)
            json["stats"] = new JsonObject
            {
                ["decodedBytes"] = stats.DecodedBytes,
                ["allocatedBytes"] = stats.AllocatedBytes,
                ["elapsedMs"] = stats.Elapsed.TotalMilliseconds,
                ["removed"] = stats.Glyphs.Removed,
                ["ocrLayer"] = stats.Glyphs.OcrLayer,
                ["white"] = stats.Glyphs.White,
                ["tiny"] = stats.Glyphs.Tiny,
                ["offPage"] = stats.Glyphs.OffPage,
            };
        return JsonSerializer.SerializeToUtf8Bytes(json);
    }

    /// <summary>The helper's answer, validated; null when it is not a well-formed result.</summary>
    public static ExtractedAttachment? ReadResult(ReadOnlySpan<byte> utf8)
    {
        try
        {
            if (JsonNode.Parse(utf8) is not JsonObject json) return null;
            var status = json["status"]?.GetValue<string>();
            if (status == null || !Statuses.Contains(status)) return null;
            var content = json["content"]?.GetValue<string>() ?? "";
            if (content.Length > AttachmentExtractor.RawTextCap || (status != AttachmentStatus.Ok && content.Length > 0)) return null;
            var detail = json["detail"]?.GetValue<string>();
            if (detail?.Length > 1024) return null;
            var signals = json["signals"]?.AsArray().Select(s => s!.GetValue<string>()).ToArray();
            if (signals?.Any(s => !s.StartsWith("pdf_", StringComparison.Ordinal) || s.Length > 64) == true || signals?.Length > 8) return null;
            PdfStats? stats = null;
            if (json["stats"] is JsonObject s)
                stats = new PdfStats(s["decodedBytes"]!.GetValue<long>(), s["allocatedBytes"]!.GetValue<long>(),
                    TimeSpan.FromMilliseconds(s["elapsedMs"]!.GetValue<double>()),
                    new PdfGlyphCounts(s["removed"]!.GetValue<int>(), s["ocrLayer"]!.GetValue<int>(), s["white"]!.GetValue<int>(),
                        s["tiny"]!.GetValue<int>(), s["offPage"]!.GetValue<int>()));
            return new ExtractedAttachment(status, content, json["isHtml"]?.GetValue<bool>() ?? false,
                json["truncated"]?.GetValue<bool>() ?? false, json["kind"]?.GetValue<string>() ?? "",
                detail, json["pages"]?.GetValue<int?>(), stats, signals);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NullReferenceException)
        {
            return null;
        }
    }
}
