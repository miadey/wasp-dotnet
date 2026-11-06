using System;
using System.Collections.Generic;

namespace Wasp.Http;

// IC HTTP gateway types (mirroring the canonical `http_request` /
// `http_request_update` Candid signatures used by the boundary nodes).
//
//   type HttpRequest = record {
//     method: text;
//     url: text;
//     headers: vec record { text; text };
//     body: blob;
//     certificate_version: opt nat16;
//   };
//
//   type HttpResponse = record {
//     status_code: nat16;
//     headers: vec record { text; text };
//     body: blob;
//     upgrade: opt bool;
//     streaming_strategy: opt StreamingStrategy;   // not yet supported
//   };

public sealed class HttpRequest
{
    public string Method { get; init; } = "";
    public string Url { get; init; } = "";
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } =
        Array.Empty<KeyValuePair<string, string>>();
    public byte[] Body { get; init; } = Array.Empty<byte>();
    public ushort? CertificateVersion { get; init; }

    /// <summary>The path portion of <see cref="Url"/> (everything before '?').</summary>
    public string Path
    {
        get
        {
            int q = Url.IndexOf('?');
            return q < 0 ? Url : Url.Substring(0, q);
        }
    }

    /// <summary>The raw query string (everything after '?'), or empty.</summary>
    public string Query
    {
        get
        {
            int q = Url.IndexOf('?');
            return q < 0 ? "" : Url.Substring(q + 1);
        }
    }
}

public sealed class HttpResponse
{
    public ushort StatusCode { get; init; } = 200;
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } =
        Array.Empty<KeyValuePair<string, string>>();
    public byte[] Body { get; init; } = Array.Empty<byte>();
    public bool? Upgrade { get; init; }

    public static HttpResponse Text(string body, ushort status = 200)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        return new HttpResponse
        {
            StatusCode = status,
            Body = bytes,
            Headers = new[]
            {
                new KeyValuePair<string, string>("content-type", "text/plain; charset=utf-8"),
                new KeyValuePair<string, string>("content-length", bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            },
        };
    }

    public static HttpResponse Json(string body, ushort status = 200)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        return new HttpResponse
        {
            StatusCode = status,
            Body = bytes,
            Headers = new[]
            {
                new KeyValuePair<string, string>("content-type", "application/json"),
                new KeyValuePair<string, string>("content-length", bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            },
        };
    }

    public static HttpResponse Html(string body, ushort status = 200)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        return new HttpResponse
        {
            StatusCode = status,
            Body = bytes,
            Headers = new[]
            {
                new KeyValuePair<string, string>("content-type", "text/html; charset=utf-8"),
                new KeyValuePair<string, string>("content-length", bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            },
        };
    }

    public static HttpResponse NotFound(string message = "Not Found")
        => Text(message, 404);

    public static HttpResponse Upgrading()
        => new() { StatusCode = 200, Upgrade = true };
}
