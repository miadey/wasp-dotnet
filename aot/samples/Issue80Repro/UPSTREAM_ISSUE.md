# [NativeAOT-LLVM] Struct-copy zeros reference fields in `[StructLayout(Explicit)]` value types on wasm32-wasi

**Repo**: dotnet/runtimelab @ `9d025fa5e9d07e3e93061b4457ba6279b178df98`
**Package**: `runtime.linux-x64.Microsoft.DotNet.ILCompiler.LLVM` 10.0.0-alpha.1.25162.1
**Target**: wasm32-wasi (`IlcLlvmTarget=wasm32-wasi`)

## Summary

When a value of a struct with `[StructLayout(LayoutKind.Explicit)]` and reference-type fields is copied through `GT_STORE_BLK` (which the JIT lowers from `stelem.any T`, `stobj T`, and other struct-copy IL shapes), the LLVM codegen path `Llvm::storeObjAtAddress` overwrites the just-stored reference field with stale bytes. The reference field reads as `null` from the destination.

This is the root cause of the [Microsoft.AspNetCore.Components.Server `RenderBatchWriter` empty-strings-table bug](https://github.com/dotnet/runtimelab/issues?q=RenderBatchWriter) seen when running Blazor Server on wasm32-wasi: `RenderTreeFrame` uses `[StructLayout(Explicit)]` with reference unions at `FieldOffset(16)`, and the JIT loses those references whenever a frame is copied into the diff's `ReferenceFrames` array.

## Minimal repro (50 lines)

```csharp
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Explicit, Pack = 4)]
public struct MiniFrame
{
    [FieldOffset(0)]  public int Sequence;
    [FieldOffset(4)]  public short Tag;
    [FieldOffset(8)]  public long IntegerUnion;
    [FieldOffset(16)] public string? StringField;   // overlay 1 at offset 16
    [FieldOffset(16)] public object? ObjectField;   // overlay 2 at offset 16
    [FieldOffset(24)] public object? SecondaryRef;
}

public static class Repro
{
    public static (string? offset16, object? offset24) Run()
    {
        var src = new MiniFrame
        {
            Sequence = 42,
            Tag = 7,
            IntegerUnion = unchecked((long)0xDEADBEEFCAFEBABE),
            StringField = "PRESERVED-AT-OFFSET-16",
            SecondaryRef = "PRESERVED-AT-OFFSET-24",
        };

        var arr = new MiniFrame[1];
        arr[0] = src;

        return (arr[0].StringField, arr[0].SecondaryRef);
        // Actual on wasm32-wasi:   (null, "PRESERVED-AT-OFFSET-24")
        // Expected:                ("PRESERVED-AT-OFFSET-16", "PRESERVED-AT-OFFSET-24")
    }
}
```

A control struct with default sequential layout preserves both refs. Three IL shapes all trigger the bug identically (ref-load + `stelem.any`, direct `arr[0] = src`, by-value method arg copy). Field-by-field assignment (`arr[0].StringField = src.StringField; …`) preserves both — it bypasses `GT_STORE_BLK` entirely.

Full canister-form repro: https://github.com/miadey/wasp-dotnet/tree/main/aot/samples/Issue80Repro

## Root cause

`Llvm::storeObjAtAddress` in `src/coreclr/jit/llvmcodegen.cpp` iterates the struct's deduplicated field list, storing each field at its offset, with the `bytesStored` cursor advanced after each store so the next iteration's padding-fill `memcpy` (gated on `fieldOffset > bytesStored`) knows where unfilled bytes start.

At line 2468 (commit `9d025fa`):

```cpp
bytesStored += static_cast<unsigned>(
    fieldData->getType()->getPrimitiveSizeInBits() / BITS_PER_BYTE);
```

`llvm::Type::getPrimitiveSizeInBits()` returns **0** for `PointerType` — LLVM's API treats pointers as non-primitive. So after `emitHelperCall(CORINFO_HELP_CHECKED_ASSIGN_REF, …)` writes a reference field at offset 16, `bytesStored` stays at 16 instead of advancing to 20 (or wherever the pointer ends).

The next iteration (offset 24 field, when `hasSignificantPadding == true`) sees `fieldOffset (24) > bytesStored (16)` and runs:

```cpp
if (structDesc->hasSignificantPadding() && fieldOffset > bytesStored)
{
    bytesStored += buildMemCpy(baseAddress, bytesStored, fieldOffset, address);
}
```

which does `memcpy(dst=base+16, src=base+24, 8)`. The destination is the array slot's offset 16, which we **just stored** the string pointer into. The source is the destination's offset 24 — still-uninitialized memory at this point in the iteration. Net effect: the string pointer at offset 16 is overwritten with the destination's stale bytes, reading back as `null`.

The offset-24 field survives because no further iteration runs to overwrite it (it's the last field). With multiple field offsets after a reference field, all but the last would be corrupted.

The bug is silent on:
- Sequential-layout structs: `hasSignificantPadding == false`, so the padding-fill `memcpy` never fires.
- Structs without `GC` references: `storeObjAtAddress` isn't called; `_builder.CreateStore(dataValue, addrValue)` writes the struct value verbatim.
- Field-by-field assignment: emits individual `stind.ref` / `stfld` per field, no `GT_STORE_BLK`, no `storeObjAtAddress`.

## Proposed fix

```cpp
unsigned fieldByteSize;
if (fieldData->getType()->isPointerTy())
{
    fieldByteSize = m_context->Module.getDataLayout().getPointerSize();
}
else
{
    fieldByteSize = static_cast<unsigned>(
        fieldData->getType()->getPrimitiveSizeInBits() / BITS_PER_BYTE);
}
bytesStored += fieldByteSize;
```

The same fix should be considered at line 2471 for `llvmStructSize` if a final-padding memcpy ever needs to run after a struct ending in a pointer field — though no repro found for that case yet.

A unified-diff version is in `proposed-runtimelab.patch` alongside this file.

## Impact

Beyond the Issue80Repro test case, this defect makes Microsoft.AspNetCore.Components.Server's `RenderBatchWriter` unusable on wasm32-wasi: the renderer's `Edits` array stores `RenderTreeFrame` values whose `AttributeName`, `TextContent`, `ElementName`, and `AttributeValue` fields all overlap at offset 16, and the AOT-LLVM struct-copy zeros them on each push into the diff's `ReferenceFrames` array. The serialized render batch then references string indices that were never appended to the strings table — Blazor's client-side `_renderBatch_applyEdit` reads `""` for every text update, so `@onclick` handlers run server-side correctly but no visible UI change reaches the browser.

## Verified workaround at user-level

Replace any `arr[idx] = *refSrc` shape with explicit field-by-field assignment. The JIT lowers each field to an individual `stind` and the buggy `storeObjAtAddress` is bypassed entirely. We've applied this pattern via a Cecil rewriter to `RenderTreeFrameArrayBuilder.AppendElement / .AppendText / .AppendAttribute / …` and it correctly preserves all reference fields. The same rewrite needs to be applied to the generic `ArrayBuilder<RenderTreeFrame>.Append(ref T)` used in the diff path to fix Blazor end-to-end without an upstream change.
