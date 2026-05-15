using System.Runtime.InteropServices;
using Wasp.IcCdk;

namespace WaspSample.RazorOnIc.Components;

// Reproduce the RenderTreeFrame layout pattern and probe whether
// reference-type fields at [FieldOffset(16)] survive a write/read round-trip
// under NativeAOT-LLVM wasm32-wasi.
// Pack=4 matches RenderTreeFrame's layout (which targets 32-bit wasm).
[StructLayout(LayoutKind.Explicit, Pack = 4)]
internal struct LayoutProbeFrame
{
    [FieldOffset(0)] internal int IntField0;
    [FieldOffset(4)] internal short ShortField4;
    [FieldOffset(8)] internal int IntField8;
    [FieldOffset(16)] internal string StringField16;
    [FieldOffset(24)] internal object ObjectField24;
}

// Same shape WITHOUT Pack=4 — natural alignment.
[StructLayout(LayoutKind.Explicit)]
internal struct LayoutProbeFrameNoPack
{
    [FieldOffset(0)] internal int IntField0;
    [FieldOffset(16)] internal string StringField16;
    [FieldOffset(24)] internal object ObjectField24;
}

public static class StructLayoutProbe
{
    public static string Run()
    {
        // Pattern A: in-place field mutation through array index (what my
        //            first probe did — works).
        var arrA = new LayoutProbeFrame[3];
        for (int i = 0; i < arrA.Length; i++)
        {
            arrA[i].IntField0 = 100 + i;
            arrA[i].StringField16 = $"A-{i}";
        }

        // Pattern B: object-initializer struct copy assignment (what
        //            RenderTreeFrameArrayBuilder.AppendElement actually does).
        var arrB = new LayoutProbeFrame[3];
        for (int i = 0; i < arrB.Length; i++)
        {
            arrB[i] = new LayoutProbeFrame
            {
                IntField0 = 100 + i,
                StringField16 = $"B-{i}",
                ObjectField24 = (object)("OB-" + i),
            };
        }

        // Pattern C: no-Pack version of B
        var arrC = new LayoutProbeFrameNoPack[3];
        for (int i = 0; i < arrC.Length; i++)
        {
            arrC[i] = new LayoutProbeFrameNoPack
            {
                IntField0 = 100 + i,
                StringField16 = $"C-{i}",
                ObjectField24 = (object)("OC-" + i),
            };
        }

        // Pattern D: in-place mutation (workaround pattern) on Pack=4 struct
        var arrD = new LayoutProbeFrame[3];
        for (int i = 0; i < arrD.Length; i++)
        {
            ref var slot = ref arrD[i];
            slot = default;
            slot.IntField0 = 100 + i;
            slot.StringField16 = $"D-{i}";
            slot.ObjectField24 = (object)("OD-" + i);
        }

        var report = new System.Text.StringBuilder();
        for (int i = 0; i < arrA.Length; i++)
        {
            ref var fA = ref arrA[i];
            ref var fB = ref arrB[i];
            ref var fC = ref arrC[i];
            ref var fD = ref arrD[i];
            report.AppendLine($"A[{i}] in-place Pack=4:           int0={fA.IntField0} str16={(fA.StringField16 ?? "(null)")}");
            report.AppendLine($"B[{i}] struct-copy Pack=4:         int0={fB.IntField0} str16={(fB.StringField16 ?? "(null)")} obj24={(fB.ObjectField24 ?? "(null)")}");
            report.AppendLine($"C[{i}] struct-copy default Pack:   int0={fC.IntField0} str16={(fC.StringField16 ?? "(null)")} obj24={(fC.ObjectField24 ?? "(null)")}");
            report.AppendLine($"D[{i}] in-place-after-default:     int0={fD.IntField0} str16={(fD.StringField16 ?? "(null)")} obj24={(fD.ObjectField24 ?? "(null)")}");
        }
        Reply.Print("[probe]\n" + report.ToString());
        return report.ToString();
    }
}
