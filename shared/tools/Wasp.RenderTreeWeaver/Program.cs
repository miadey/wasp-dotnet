// Wasp.RenderTreeWeaver: rewrites the Append* methods in
// Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameArrayBuilder
// to use in-place mutation of array slots instead of struct-copy via
// object initializer.
//
// Why: NativeAOT-LLVM compiling for wasm32-wasi miscompiles struct-copy
// assignment when the struct has [StructLayout(LayoutKind.Explicit)] and a
// string field at [FieldOffset(16)] — the string field is lost. The fix is
// to use in-place mutation:
//
//   ref var slot = ref _items[_itemsInUse++];
//   slot = default;
//   slot.SequenceField = sequence;
//   slot.FrameTypeField = RenderTreeFrameType.Element;
//   slot.ElementNameField = elementName;
//
// See aot/Wasp.AspNetCore/UNSUPPORTED.md > Razor SSR for diagnosis.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: render-tree-weaver <input.dll> <output.dll>");
    return 1;
}

var inputPath = args[0];
var outputPath = args[1];

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"input not found: {inputPath}");
    return 2;
}

// Load with default assembly resolver — Microsoft.AspNetCore.Components.dll
// depends on RenderTreeFrame which lives in the same assembly, so we don't
// need to load the shared framework.
var resolver = new DefaultAssemblyResolver();
var inputDir = Path.GetDirectoryName(Path.GetFullPath(inputPath))!;
resolver.AddSearchDirectory(inputDir);

using var assembly = AssemblyDefinition.ReadAssembly(inputPath, new ReaderParameters
{
    AssemblyResolver = resolver,
    ReadWrite = false,
    InMemory = true,
});

var module = assembly.MainModule;
var builderType = module.GetType("Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameArrayBuilder");
if (builderType is null)
{
    Console.Error.WriteLine($"type not found: RenderTreeFrameArrayBuilder in {inputPath}");
    return 3;
}

// The methods we need to rewrite. Each is shaped:
//   public void Append<X>(int seq, ...) {
//       if (_itemsInUse == _items.Length) GrowBuffer(_items.Length * 2);
//       _items[_itemsInUse++] = new RenderTreeFrame {
//           SequenceField = seq, FrameTypeField = ..., XField = arg, [optional: YField = arg]
//       };
//   }
//
// We replace the body with the in-place pattern.
var targetMethods = new[]
{
    "AppendElement",      // (int, string elementName)
    "AppendText",         // (int, string textContent)
    "AppendMarkup",       // (int, string markupContent)
    "AppendAttribute",    // (int, string name, object value)
    "AppendComponent",    // (int, Type componentType)
    "AppendRegion",       // (int)
    "AppendElementReferenceCapture",
    "AppendComponentReferenceCapture",
    "AppendNamedEvent",
    "AppendComponentRenderMode",
};

var rewroteCount = 0;
foreach (var method in builderType.Methods.Where(m => targetMethods.Contains(m.Name) && m.HasBody))
{
    if (TryRewriteAppend(method, module))
    {
        rewroteCount++;
        Console.WriteLine($"rewrote: {method.Name}");
    }
    else
    {
        Console.WriteLine($"skipped: {method.Name} (did not match expected pattern)");
    }
}

if (rewroteCount == 0)
{
    Console.Error.WriteLine("no methods rewritten — pattern may have changed");
    return 4;
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
assembly.Write(outputPath);
Console.WriteLine($"wrote: {outputPath} (rewrote {rewroteCount} methods)");
return 0;

static bool TryRewriteAppend(MethodDefinition method, ModuleDefinition module)
{
    // Find:
    //   stelem.any RenderTreeFrame    (with stack: _items, idx, frameValue)
    //   OR
    //   ldelema RenderTreeFrame + stobj RenderTreeFrame   (older codegen)
    //
    // The full sequence we look for at the END of the method:
    //   ldarg.0 ldfld _items
    //   ldarg.0 dup ldfld _itemsInUse dup ldc.i4.1 add stfld _itemsInUse
    //   <object initializer: newobj or initobj on local, then setters>
    //   stelem.any RenderTreeFrame
    //   ret
    //
    // Rewrite to:
    //   ldarg.0 ldfld _items
    //   ldarg.0 dup ldfld _itemsInUse dup ldc.i4.1 add stfld _itemsInUse
    //   ldelema RenderTreeFrame           // ref var slot
    //   dup initobj RenderTreeFrame       // slot = default
    //   <setters using ref slot>
    //   ret
    //
    // Instead of decoding existing IL, we synthesize a fresh body that
    // matches the method's parameter list. This is brittle if framework
    // signatures change, but is much simpler.

    var declaringType = method.DeclaringType;
    // _items and _itemsInUse live on ArrayBuilder<T> (the generic base).
    // We need closed-generic FieldReferences with DeclaringType = the closed
    // base type (ArrayBuilder<RenderTreeFrame>), otherwise ILverify rejects.
    var baseTypeRef = declaringType.BaseType; // GenericInstanceType
    var baseTypeDef = baseTypeRef.Resolve();
    var itemsFieldDef = baseTypeDef.Fields.FirstOrDefault(f => f.Name == "_items");
    var inUseFieldDef = baseTypeDef.Fields.FirstOrDefault(f => f.Name == "_itemsInUse");
    if (itemsFieldDef is null || inUseFieldDef is null) return false;

    // Build closed-generic field references.
    var importedBaseType = method.Module.ImportReference(baseTypeRef);
    var itemsField = new FieldReference(itemsFieldDef.Name, method.Module.ImportReference(itemsFieldDef.FieldType, method), importedBaseType);
    var inUseField = new FieldReference(inUseFieldDef.Name, method.Module.ImportReference(inUseFieldDef.FieldType, method), importedBaseType);

    var frameType = module.GetType("Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrame");
    if (frameType is null) return false;
    var frameTypeRef = method.Module.ImportReference(frameType);

    // The set of field-store mappings per method: maps parameter index → field name.
    // SequenceField is always parameter 1 (index 1; this=arg0).
    var frameTypeFieldName = "FrameTypeField";
    (string field, int paramIdx)[] mapping;
    string frameTypeEnumValue;

    switch (method.Name)
    {
        case "AppendElement":
            mapping = new[] { ("SequenceField", 1), ("ElementNameField", 2) };
            frameTypeEnumValue = "Element";
            break;
        case "AppendText":
            mapping = new[] { ("SequenceField", 1), ("TextContentField", 2) };
            frameTypeEnumValue = "Text";
            break;
        case "AppendMarkup":
            mapping = new[] { ("SequenceField", 1), ("MarkupContentField", 2) };
            frameTypeEnumValue = "Markup";
            break;
        case "AppendAttribute":
            mapping = new[] { ("SequenceField", 1), ("AttributeNameField", 2), ("AttributeValueField", 3) };
            frameTypeEnumValue = "Attribute";
            break;
        case "AppendComponent":
            mapping = new[] { ("SequenceField", 1), ("ComponentTypeField", 2) };
            frameTypeEnumValue = "Component";
            break;
        case "AppendRegion":
            mapping = new[] { ("SequenceField", 1) };
            frameTypeEnumValue = "Region";
            break;
        case "AppendElementReferenceCapture":
            mapping = new[] { ("SequenceField", 1), ("ElementReferenceCaptureActionField", 2) };
            frameTypeEnumValue = "ElementReferenceCapture";
            break;
        case "AppendComponentReferenceCapture":
            mapping = new[] { ("SequenceField", 1), ("ComponentReferenceCaptureActionField", 2), ("ComponentReferenceCaptureParentFrameIndexField", 3) };
            frameTypeEnumValue = "ComponentReferenceCapture";
            break;
        case "AppendNamedEvent":
            mapping = new[] { ("SequenceField", 1), ("NamedEventTypeField", 2), ("NamedEventAssignedNameField", 3) };
            frameTypeEnumValue = "NamedEvent";
            break;
        case "AppendComponentRenderMode":
            mapping = new[] { ("SequenceField", 1), ("ComponentRenderModeField", 2) };
            frameTypeEnumValue = "ComponentRenderMode";
            break;
        default:
            return false;
    }

    var frameTypeTypeDef = frameType.Resolve();
    var frameTypeField = frameTypeTypeDef.Fields.FirstOrDefault(f => f.Name == frameTypeFieldName);
    if (frameTypeField is null) return false;
    var frameTypeFieldRef = method.Module.ImportReference(frameTypeField);

    // Resolve the RenderTreeFrameType enum value.
    var frameTypeEnum = module.GetType("Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType");
    if (frameTypeEnum is null) return false;
    var enumField = frameTypeEnum.Fields.FirstOrDefault(f => f.Name == frameTypeEnumValue);
    if (enumField is null || enumField.Constant is null) return false;
    var enumValue = Convert.ToInt32(enumField.Constant);

    // Resolve the target fields on RenderTreeFrame.
    var fieldRefs = new List<(FieldReference fieldRef, int paramIdx)>();
    foreach (var (fieldName, paramIdx) in mapping)
    {
        var f = frameTypeTypeDef.Fields.FirstOrDefault(x => x.Name == fieldName);
        if (f is null) return false;
        fieldRefs.Add((method.Module.ImportReference(f), paramIdx));
    }

    // Resolve the GrowBuffer method on the (open) base class — then make
    // a closed-generic MethodReference matching the base type's instantiation.
    var growBufferDef = ResolveMethod(declaringType, "GrowBuffer", 1);
    if (growBufferDef is null) return false;
    var growBufferRef = new MethodReference(growBufferDef.Name, method.Module.ImportReference(growBufferDef.ReturnType, method), importedBaseType)
    {
        HasThis = growBufferDef.HasThis,
        ExplicitThis = growBufferDef.ExplicitThis,
        CallingConvention = growBufferDef.CallingConvention,
    };
    foreach (var p in growBufferDef.Parameters)
        growBufferRef.Parameters.Add(new ParameterDefinition(method.Module.ImportReference(p.ParameterType, method)));

    var itemsFieldRef = itemsField;
    var inUseFieldRef = inUseField;

    // Build new method body.
    method.Body.Instructions.Clear();
    method.Body.ExceptionHandlers.Clear();
    method.Body.Variables.Clear();
    method.Body.InitLocals = true;

    var il = method.Body.GetILProcessor();

    // if (_itemsInUse == _items.Length) GrowBuffer(_items.Length * 2);
    var skipGrow = il.Create(OpCodes.Nop);
    il.Append(il.Create(OpCodes.Ldarg_0));
    il.Append(il.Create(OpCodes.Ldfld, inUseFieldRef));
    il.Append(il.Create(OpCodes.Ldarg_0));
    il.Append(il.Create(OpCodes.Ldfld, itemsFieldRef));
    il.Append(il.Create(OpCodes.Ldlen));
    il.Append(il.Create(OpCodes.Conv_I4));
    il.Append(il.Create(OpCodes.Bne_Un_S, skipGrow));
    il.Append(il.Create(OpCodes.Ldarg_0));
    il.Append(il.Create(OpCodes.Ldarg_0));
    il.Append(il.Create(OpCodes.Ldfld, itemsFieldRef));
    il.Append(il.Create(OpCodes.Ldlen));
    il.Append(il.Create(OpCodes.Conv_I4));
    il.Append(il.Create(OpCodes.Ldc_I4_2));
    il.Append(il.Create(OpCodes.Mul));
    il.Append(il.Create(OpCodes.Call, growBufferRef));
    il.Append(skipGrow);

    // ref var slot = ref _items[_itemsInUse]; _itemsInUse++;
    // We load the address by element first, then bump _itemsInUse.
    // Actually do: load _items, load idx (post-increment _itemsInUse), ldelema
    //
    //   ldarg.0
    //   ldfld _items
    //   ldarg.0
    //   dup
    //   ldfld _itemsInUse
    //   dup        ; (stack: items, this, idx, idx)
    //   ; need to bump _itemsInUse — re-order: load idx, then bump after
    // Simpler: compute slot ref directly:
    //   ldarg.0; ldfld _items                  -> [items]
    //   ldarg.0; ldfld _itemsInUse              -> [items, idx]
    //   ldelema RenderTreeFrame                 -> [&slot]
    //   ; then bump _itemsInUse separately
    il.Append(il.Create(OpCodes.Ldarg_0));
    il.Append(il.Create(OpCodes.Ldfld, itemsFieldRef));
    il.Append(il.Create(OpCodes.Ldarg_0));
    il.Append(il.Create(OpCodes.Ldfld, inUseFieldRef));
    il.Append(il.Create(OpCodes.Ldelema, frameTypeRef));
    // dup; initobj RenderTreeFrame   (slot = default)
    il.Append(il.Create(OpCodes.Dup));
    il.Append(il.Create(OpCodes.Initobj, frameTypeRef));
    // Bump _itemsInUse: this._itemsInUse++;
    il.Append(il.Create(OpCodes.Ldarg_0));
    il.Append(il.Create(OpCodes.Ldarg_0));
    il.Append(il.Create(OpCodes.Ldfld, inUseFieldRef));
    il.Append(il.Create(OpCodes.Ldc_I4_1));
    il.Append(il.Create(OpCodes.Add));
    il.Append(il.Create(OpCodes.Stfld, inUseFieldRef));

    // slot.FrameTypeField = <enumValue>;
    il.Append(il.Create(OpCodes.Dup));
    il.Append(il.Create(OpCodes.Ldc_I4, enumValue));
    il.Append(il.Create(OpCodes.Stfld, frameTypeFieldRef));

    // For each mapped field: slot.<field> = arg[paramIdx];
    foreach (var (fieldRef, paramIdx) in fieldRefs)
    {
        if (paramIdx - 1 >= method.Parameters.Count)
        {
            // Skip — the method signature doesn't match our mapping. We
            // bail rather than producing wrong IL.
            method.Body.Instructions.Clear();
            return false;
        }
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Ldarg, method.Parameters[paramIdx - 1]));
        il.Append(il.Create(OpCodes.Stfld, fieldRef));
    }

    // pop the ref-slot we kept dup'ing
    il.Append(il.Create(OpCodes.Pop));
    il.Append(il.Create(OpCodes.Ret));

    method.Body.OptimizeMacros();
    return true;
}

static MethodDefinition? ResolveMethod(TypeDefinition type, string name, int paramCount)
{
    var current = type;
    while (current is not null)
    {
        var m = current.Methods.FirstOrDefault(x => x.Name == name && x.Parameters.Count == paramCount);
        if (m is not null) return m;
        current = current.BaseType?.Resolve();
    }
    return null;
}
