namespace Wasp;

// The single entry point Mono invokes from Rust via
// `mono_wasm_invoke_jsexport` (or the equivalent embedding API used by
// the canister host).
//
// `methodName` arrives pre-decoded from `ic0.msg_method_name`; this
// function is responsible for routing it to the right user-defined
// `[CanisterQuery]` / `[CanisterUpdate]` method and returning the
// reply payload as raw bytes (caller will hand them to
// `wasp_reply`).
//
// Phase B v0.1: stub. Real reflection-based dispatch lands with
// issues #15 / #16; for now we just echo back a sentinel string so
// the host integration tests can verify the round-trip works.
public static class Bridge
{
    public static byte[] Dispatch(string methodName)
    {
        return System.Text.Encoding.UTF8.GetBytes(
            $"hello from managed code; method={methodName}, time={MessageContext.Now()}"
        );
    }
}
