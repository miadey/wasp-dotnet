using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Wasp.Http;
using Wasp.IcCdk;

namespace WaspSample.Issue80Repro;

// Repro for gh #80: NativeAOT-LLVM wasm32-wasi miscompiles struct-copy
// when the struct uses [StructLayout(Explicit)] with reference fields
// overlaid at the same FieldOffset. Mirrors the layout that breaks
// RenderTreeFrame in Microsoft.AspNetCore.Components.
//
// The hypothesis: when a value of MiniFrame is stored into an array via
// `arr[idx] = *refToMiniFrame`, the JIT lowers this to GT_STORE_BLK
// which the LLVM codegen handles via storeObjAtAddress — and that
// iteration drops the reference field at offset 16. If reproduced, the
// /test endpoint will return "BUG: field went null".

[StructLayout(LayoutKind.Explicit, Pack = 4)]
public struct MiniFrame
{
    [FieldOffset(0)] public int Sequence;
    [FieldOffset(4)] public short Tag;
    [FieldOffset(8)] public long IntegerUnion;
    // Two overlay reference fields at offset 16 — same shape as
    // RenderTreeFrame's ElementName/TextContent/AttributeName/etc.
    [FieldOffset(16)] public string? StringField;
    [FieldOffset(16)] public object? ObjectField;
    [FieldOffset(24)] public object? SecondaryRef;
}

// Control: sequential layout, same shape.
public struct ControlFrame
{
    public int Sequence;
    public short Tag;
    public long IntegerUnion;
    public string? StringField;
    public object? SecondaryRef;
}

public static class Issue80ReproCanister
{
    [ModuleInitializer]
    internal static void RegisterRoutes()
    {
        WaspHttp.RequireUpdateForAll();

        WaspHttp.Get("/", _ => HttpResponse.Text(RunTest()));
        WaspHttp.Get("/test", _ => HttpResponse.Text(RunTest()));
    }

    private static string RunTest()
    {
        // Build the source frame.
        var src = new MiniFrame
        {
            Sequence = 42,
            Tag = 7,
            IntegerUnion = unchecked((long)0xDEADBEEFCAFEBABE),
            StringField = "PRESERVED-AT-OFFSET-16",
            SecondaryRef = "PRESERVED-AT-OFFSET-24",
        };

        // Copy via the buggy pattern: `arr[i] = *refToFrame`.
        // C# emits: ldarg arr; ldc.i4 0; ldarg.s refSrc; ldobj MiniFrame;
        // stelem.any MiniFrame — exactly the pattern flagged in gh #80.
        var arr = new MiniFrame[1];
        ref MiniFrame refSrc = ref src;
        arr[0] = refSrc;

        // Variant B: direct local-to-array assign (no ref). Different IL.
        var arrDirect = new MiniFrame[1];
        arrDirect[0] = src;

        // Variant C: passing struct by value through a method.
        var arrViaMethod = new MiniFrame[1];
        CopyToSlot(src, arrViaMethod, 0);

        // Read back. If the AOT-LLVM struct-copy lost offset 16, the
        // string is null.
        var roundTripped = arr[0];

        var lines = new System.Text.StringBuilder();
        lines.AppendLine($"src.Sequence       = {src.Sequence}");
        lines.AppendLine($"arr[0].Sequence    = {roundTripped.Sequence}");
        lines.AppendLine($"src.Tag            = {src.Tag}");
        lines.AppendLine($"arr[0].Tag         = {roundTripped.Tag}");
        lines.AppendLine($"src.IntegerUnion   = 0x{src.IntegerUnion:X}");
        lines.AppendLine($"arr[0].IntegerUnion= 0x{roundTripped.IntegerUnion:X}");
        lines.AppendLine($"src.StringField    = {Describe(src.StringField)}");
        lines.AppendLine($"arr[0].StringField = {Describe(roundTripped.StringField)}");
        lines.AppendLine($"src.SecondaryRef   = {Describe(src.SecondaryRef)}");
        lines.AppendLine($"arr[0].SecondaryRef= {Describe(roundTripped.SecondaryRef)}");

        string? sb = src.StringField;
        string? rb = roundTripped.StringField;
        object? sr = src.SecondaryRef;
        object? rr = roundTripped.SecondaryRef;
        lines.AppendLine($"sb==null? {sb==null}  rb==null? {rb==null}  sr==null? {sr==null}  rr==null? {rr==null}");

        // Variant B/C readback
        lines.AppendLine();
        lines.AppendLine($"arrDirect[0].StringField    = {Describe(arrDirect[0].StringField)}");
        lines.AppendLine($"arrDirect[0].SecondaryRef   = {Describe(arrDirect[0].SecondaryRef)}");
        lines.AppendLine($"arrViaMethod[0].StringField = {Describe(arrViaMethod[0].StringField)}");
        lines.AppendLine($"arrViaMethod[0].SecondaryRef= {Describe(arrViaMethod[0].SecondaryRef)}");

        bool stringLost = sb != null && rb == null;
        bool secondaryLost = sr != null && rr == null;

        // Control: sequential-layout struct, same fields.
        lines.AppendLine();
        lines.AppendLine("=== Control (LayoutKind.Auto) ===");
        var ctrlSrc = new ControlFrame
        {
            Sequence = 99,
            Tag = 11,
            IntegerUnion = 0x1234567812345678,
            StringField = "CTRL-OFFSET-16",
            SecondaryRef = "CTRL-OFFSET-24",
        };
        var ctrlArr = new ControlFrame[1];
        ref ControlFrame ctrlRef = ref ctrlSrc;
        ctrlArr[0] = ctrlRef;
        string? csb = ctrlSrc.StringField;
        string? crb = ctrlArr[0].StringField;
        object? csr = ctrlSrc.SecondaryRef;
        object? crr = ctrlArr[0].SecondaryRef;
        lines.AppendLine($"ctrl src.StringField    = {Describe(csb)}");
        lines.AppendLine($"ctrl arr[0].StringField = {Describe(crb)}");
        lines.AppendLine($"ctrl src.SecondaryRef   = {Describe(csr)}");
        lines.AppendLine($"ctrl arr[0].SecondaryRef= {Describe(crr)}");
        lines.AppendLine();
        lines.AppendLine(stringLost
            ? "==> BUG REPRODUCED: offset-16 string field went null after struct-copy."
            : "==> offset-16 string field preserved.");
        lines.AppendLine(secondaryLost
            ? "==> BUG REPRODUCED: offset-24 reference field went null after struct-copy."
            : "==> offset-24 reference field preserved.");
        return lines.ToString();
    }

    private static void CopyToSlot(MiniFrame frame, MiniFrame[] arr, int idx)
    {
        arr[idx] = frame;
    }

    private static string Describe(object? value)
    {
        if (value is null) return "<null>";
        if (value is string s) return $"<string len={s.Length}> '{s}'";
        return $"<{value.GetType().Name}> '{value}'";
    }
}
