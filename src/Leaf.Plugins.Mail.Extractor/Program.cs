using System.Text;
using Leaf.Plugins.Mail;

// Attachment text extractor. Reads one request from stdin, writes one JSON result to stdout.
// No network, no files, no child processes: the plugin runs this inside a job object that
// caps memory, CPU time and process count, and kills it on any limit.
// Exit codes: 0 result written, 2 malformed request, 3 out of memory.

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // legacy charsets in text parts

try
{
    var (bytes, mimeType, filename, charset) = AttachmentWire.ReadRequest(Console.OpenStandardInput());
    var result = AttachmentExtractor.Extract(bytes, mimeType, filename, charset);
    Array.Clear(bytes);
    using var stdout = Console.OpenStandardOutput();
    stdout.Write(AttachmentWire.WriteResult(result));
    return 0;
}
catch (OutOfMemoryException)
{
    return 3;
}
catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or System.Text.Json.JsonException or InvalidOperationException)
{
    return 2;
}
