using System.Runtime.CompilerServices;
using Wasp.IcCdk;
using Wasp.WebSockets;

namespace WaspSample.HelloChat;

// Echo canister with WebSocket support.
//
// The ic-websocket-js SDK Candid-encodes every payload it sends as
// `vec nat8`, and Candid-decodes every payload it receives the same
// way. So our message handlers need to (a) read the incoming bytes as
// a Candid blob and (b) wrap the outgoing reply in a Candid blob.

public static class HelloChatCanister
{
    [ModuleInitializer]
    internal static void RegisterHandlers()
    {
        WaspWs.Init(new WsHandlers
        {
            OnOpen = principal =>
                Reply.Print($"[chat] open: {principal.Length}-byte principal"),

            OnMessage = (principal, payload) =>
            {
                // payload is a Candid-encoded `vec nat8` (blob). Decode the
                // inner bytes, prepend "echo: ", re-encode and send back.
                byte[] inner;
                try
                {
                    var reader = new Candid.Reader(payload);
                    inner = reader.ReadBlob();
                }
                catch
                {
                    // Fallback for raw senders.
                    inner = payload;
                }
                var prefix = "echo: "u8.ToArray();
                var combined = new byte[prefix.Length + inner.Length];
                System.Buffer.BlockCopy(prefix, 0, combined, 0, prefix.Length);
                System.Buffer.BlockCopy(inner, 0, combined, prefix.Length, inner.Length);
                Reply.Print($"[chat] echo: \"{System.Text.Encoding.UTF8.GetString(combined)}\"");
                WaspWs.Send(principal, Candid.EncodeBlob(combined));
            },

            OnClose = principal =>
                Reply.Print($"[chat] close: {principal.Length}-byte principal"),
        });
    }
}
