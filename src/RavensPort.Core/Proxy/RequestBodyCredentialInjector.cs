using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using RavensPort.Core.Diagnostics;
using Yarp.ReverseProxy.Transforms;

namespace RavensPort.Core.Proxy;

/// <summary>
/// Writes credentials into the request body, for the upstreams that want them there rather
/// than in a header or the query string.
///
/// Unlike the other two placements this cannot be done in passing: the body has to be buffered,
/// parsed, edited, and re-serialized, which is only safe for bodies that are small, declared,
/// and in a shape this understands. Everything else is refused outright and reported, because a
/// half-rewritten body reaching an upstream is worse than an honest "could not attach" line in
/// the activity log next to the 401 it explains.
///
/// A route may put several credentials in the body at once, so every field is written in a
/// single pass. Injecting them one at a time would re-buffer and re-serialize the body once per
/// field, and would leave the request half-authenticated if a later field failed.
/// </summary>
internal static class RequestBodyCredentialInjector
{
    /// <summary>
    /// Anything larger is forwarded untouched. Buffering is the whole cost of this placement,
    /// and a route that streams file uploads must not have them held in memory.
    /// </summary>
    private const long MaxBufferedBodyBytes = 1024 * 1024;

    private enum BodyKind { Unsupported, Json, Form }

    public static async ValueTask<bool> TryInjectAsync(
        RequestTransformContext context,
        IReadOnlyList<KeyValuePair<string, string>> fields,
        ActivityLog activityLog,
        string? credentialName)
    {
        var request = context.HttpContext.Request;

        if (fields.Count == 0) return true;

        if (Refuse(request) is { } reason)
        {
            activityLog.Log($"BODY-AUTH '{credentialName}' not attached to {request.Method} {request.Path} — {reason}");
            return false;
        }

        var kind = Classify(request.ContentType);

        var (buffered, exceededLimit) = await ReadCappedAsync(
            request.Body, MaxBufferedBodyBytes, context.HttpContext.RequestAborted);

        if (exceededLimit)
        {
            // Only reachable for an undeclared body, since a declared one over the limit was
            // refused above. Nothing has been lost: the prefix already read is put back in front
            // of the unread remainder so the request still forwards in full, just unauthenticated.
            activityLog.Log($"BODY-AUTH '{credentialName}' not attached to {request.Method} {request.Path} "
                            + $"— streamed body exceeded the {MaxBufferedBodyBytes}-byte limit for body injection");

            request.Body = new PrefixedStream(buffered, request.Body);
            return false;
        }

        var buffer = new MemoryStream(buffered, writable: false);
        var body = Encoding.UTF8.GetString(buffered);

        var (rewritten, mediaType) = kind == BodyKind.Json
            ? (RewriteJson(body, fields), $"{MediaTypeOf(request.ContentType)}; charset=utf-8")
            : (RewriteForm(body, fields), "application/x-www-form-urlencoded");

        if (rewritten is null)
        {
            // The only way to get here is a JSON body that is not an object — an array or a bare
            // literal has nowhere to put a named field.
            activityLog.Log($"BODY-AUTH '{credentialName}' not attached to {request.Method} {request.Path} "
                            + "— JSON body is not an object, so it has no field to set");

            // Still forwarded, just unauthenticated: the body was consumed above, so it has to
            // be put back either way.
            Apply(context, request, buffer.ToArray(), request.ContentType);
            return false;
        }

        Apply(context, request, Encoding.UTF8.GetBytes(rewritten), mediaType);
        return true;
    }

    /// <summary>Returns null when the body can be rewritten, or a short reason when it cannot.</summary>
    private static string? Refuse(HttpRequest request)
    {
        if (Classify(request.ContentType) == BodyKind.Unsupported)
        {
            return $"content type '{request.ContentType ?? "(none)"}' is not JSON or form-urlencoded";
        }

        // Decoding, editing and re-encoding a compressed body would mean re-implementing the
        // upstream's content coding; forwarding it as-is at least keeps the request valid.
        if (request.Headers.ContainsKey("Content-Encoding"))
        {
            return "body is content-encoded";
        }

        // A body with no declared length used to be refused outright, on the reasoning that
        // buffering could not be budgeted in advance and a partly read body could not be undone.
        // Both halves turned out to be wrong: reading is capped, and whatever was read can be
        // put back in front of the remainder (see PrefixedStream).
        //
        // Refusing it was not a small gap either. Every MCP client streams its JSON-RPC POSTs
        // chunked, so body placement silently never attached a credential to MCP traffic at all —
        // the one kind of upstream this proxy exists to serve.
        if (request.ContentLength is not { } length) return null;

        return length > MaxBufferedBodyBytes
            ? $"body is {length} bytes, over the {MaxBufferedBodyBytes}-byte limit for body injection"
            : null;
    }

    /// <summary>
    /// Reads at most <paramref name="limit"/> bytes, reporting whether there is more to come.
    /// Reads one byte past the limit to tell "exactly at the limit" from "over" it.
    /// </summary>
    private static async ValueTask<(byte[] Buffered, bool ExceededLimit)> ReadCappedAsync(
        Stream body, long limit, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var total = 0L;

        while (true)
        {
            var read = await body.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            total += read;

            if (total > limit) return (buffer.ToArray(), true);
        }

        return (buffer.ToArray(), false);
    }

    /// <summary>
    /// Replays an already-read prefix, then continues from the stream it was read from.
    ///
    /// This is what makes reading-then-declining safe. Without it, discovering mid-read that a
    /// body is too large to buffer would leave those bytes consumed and the request truncated.
    /// </summary>
    private sealed class PrefixedStream(byte[] prefix, Stream rest) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= prefix.Length) return rest.Read(buffer);

            var take = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.AsSpan(_position, take).CopyTo(buffer);
            _position += take;

            return take;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= prefix.Length) return await rest.ReadAsync(buffer, cancellationToken);

            var take = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.AsMemory(_position, take).CopyTo(buffer);
            _position += take;

            return take;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static BodyKind Classify(string? contentType)
    {
        var mediaType = MediaTypeOf(contentType);

        if (mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return BodyKind.Form;
        }

        if (!mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            return BodyKind.Unsupported;
        }

        // The body is decoded as UTF-8 below. JSON is UTF-8 by default and an explicit
        // "charset=utf-8" is common, but anything else would be decoded wrongly and re-emitted
        // as mojibake, so leave those alone.
        var charset = ParameterOf(contentType, "charset");
        return charset is null
               || charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
               || charset.Equals("us-ascii", StringComparison.OrdinalIgnoreCase)
            ? BodyKind.Json
            : BodyKind.Unsupported;
    }

    private static string MediaTypeOf(string? contentType) =>
        contentType is null ? "" : contentType.Split(';')[0].Trim();

    private static string? ParameterOf(string? contentType, string name)
    {
        if (contentType is null) return null;

        foreach (var part in contentType.Split(';').Skip(1))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return pair[1].Trim().Trim('"');
            }
        }

        return null;
    }

    /// <summary>Sets the fields on a JSON object body, or returns null if the body is not one.</summary>
    private static string? RewriteJson(string body, IReadOnlyList<KeyValuePair<string, string>> fields)
    {
        JsonNode? parsed;
        try
        {
            // An empty body is legitimate here: a POST with "Content-Type: application/json" and
            // nothing in it becomes the object carrying just the credential.
            parsed = string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (parsed is not JsonObject json) return null;

        // Assignment, not Add: a caller-supplied field of the same name is overwritten rather
        // than duplicated, so the upstream cannot be shown two candidate credentials.
        foreach (var (fieldName, value) in fields)
        {
            json[fieldName] = JsonValue.Create(value);
        }

        return json.ToJsonString();
    }

    private static string RewriteForm(string body, IReadOnlyList<KeyValuePair<string, string>> fields)
    {
        var parsed = QueryHelpers.ParseQuery(body);
        foreach (var (fieldName, value) in fields)
        {
            parsed[fieldName] = new StringValues(value);
        }

        return string.Join("&", parsed.SelectMany(field =>
            field.Value.Select(v => $"{Uri.EscapeDataString(field.Key)}={Uri.EscapeDataString(v ?? "")}")));
    }

    /// <summary>
    /// Puts the rewritten body back where the forwarder will pick it up.
    ///
    /// Two edits, because the outgoing request is assembled from two places. The body itself has
    /// to go back on HttpContext.Request — YARP streams the outgoing content from there and
    /// throws outright ("Replacing the YARP outgoing request HttpContent is not supported") if a
    /// transform hands it a different HttpContent. The *content headers*, however, were already
    /// copied onto that content before this transform ran, so a Content-Length left at the
    /// caller's original value fails the forward with "More bytes received than the specified
    /// Content-Length" the moment the rewritten body turns out to be longer.
    /// </summary>
    private static void Apply(RequestTransformContext context, HttpRequest request, byte[] bytes, string? mediaType)
    {
        request.Body = new MemoryStream(bytes, writable: false);
        request.ContentLength = bytes.Length;
        if (mediaType is not null) request.ContentType = mediaType;

        if (context.ProxyRequest.Content is not { } content) return;

        // A body that arrived chunked now has a known length. Leaving the chunked marker in
        // place alongside a Content-Length is an illegal combination and makes the upstream
        // read the length prefix of the first chunk as body content.
        context.ProxyRequest.Headers.TransferEncodingChunked = null;

        content.Headers.ContentLength = bytes.Length;

        if (mediaType is not null)
        {
            content.Headers.Remove("Content-Type");
            content.Headers.TryAddWithoutValidation("Content-Type", mediaType);
        }
    }
}
