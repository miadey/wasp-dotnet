using Wasp.IcCdk;
using Wasp.Outcalls;

namespace WaspSample.HelloFetch;

// Outbound HTTPS demo: a canister method that hits an external URL via
// the IC's management canister and returns the response body.
//
// The handler uses Manual = true because the reply happens in the
// outcall's reply callback, not in the body of the method itself.

public static partial class HelloFetchCanister
{
    [CanisterUpdate(Manual = true)]
    public static void fetch()
    {
        var arg = MessageContext.ArgData();
        var url = new Candid.Reader(arg).ReadText();

        Reply.Print($"[hellofetch] fetch({url})");

        WaspOutcall.Get(
            url,
            onReply: resp =>
            {
                Reply.Print($"[hellofetch] reply: status={resp.Status} bytes={resp.Body.Length}");
                // Cap body to keep response small enough for query reply
                var text = resp.BodyAsText();
                if (text.Length > 4096) text = text.Substring(0, 4096) + "…[truncated]";
                Reply.Bytes(Candid.EncodeText($"[{resp.Status}] {text}"));
            },
            onReject: rej =>
            {
                Reply.Print($"[hellofetch] reject: code={rej.Code} message={rej.Message}");
                Reply.Bytes(Candid.EncodeText($"REJECT {rej.Code}: {rej.Message}"));
            });
    }
}
