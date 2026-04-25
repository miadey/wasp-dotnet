using System;
using System.Collections.Generic;

namespace Wasp.Outcalls;

public enum OutcallMethod : byte
{
    Get = 0,
    Head = 1,
    Post = 2,
}

public sealed class OutcallResponse
{
    public OutcallResponse(ushort status, IReadOnlyList<KeyValuePair<string, string>> headers, byte[] body)
    {
        Status = status;
        Headers = headers;
        Body = body;
    }
    public ushort Status { get; }
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }
    public byte[] Body { get; }

    public string BodyAsText() => System.Text.Encoding.UTF8.GetString(Body);
}

public sealed class OutcallReject
{
    public OutcallReject(uint code, string message)
    {
        Code = code;
        Message = message;
    }
    public uint Code { get; }
    public string Message { get; }
}
