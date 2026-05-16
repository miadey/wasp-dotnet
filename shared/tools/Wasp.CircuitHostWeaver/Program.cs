// Wasp.CircuitHostWeaver — Mono.Cecil tool that opens visibility on
// internal types in Microsoft.AspNetCore.Components.Server so the
// Wasp.AspNetCore.Blazor.Server package can construct a CircuitHost
// wired to our IcClientProxy (M4.S5) without vendoring the entire source.
//
// What it does:
//   1. Loads the framework Microsoft.AspNetCore.Components.Server.dll.
//   2. For every type in the namespaces
//        Microsoft.AspNetCore.Components.Server
//        Microsoft.AspNetCore.Components.Server.Circuits
//      whose visibility is currently `NotPublic` (i.e. `internal`), sets
//      the visibility flag to `Public`.
//   3. For every method/property/field on those types that's `Assembly`
//      (i.e. `internal`), promotes to `Public`.
//   4. Writes the modified DLL to the requested output path.
//
// Pairs with aot/Wasp.AspNetCore.Blazor.Server/Vendor/, referenced from
// aot/Wasp.AspNetCore.Blazor.Server/Wasp.AspNetCore.Blazor.Server.targets
// (analogous to Microsoft.AspNetCore.Components.dll handling in
// aot/Wasp.AspNetCore/Wasp.AspNetCore.targets).
//
// Why widen visibility rather than emit accessor surrogates? CircuitHost's
// constructor takes 12 parameters of internal types; reproducing surrogate
// constructors for every one would be a maintenance nightmare across
// .NET 10 minor versions. Visibility widening is the smaller-blast-radius
// change.
//
// Usage:
//   circuit-host-weaver \
//     /usr/share/dotnet/shared/Microsoft.AspNetCore.App/10.0.0/Microsoft.AspNetCore.Components.Server.dll \
//     aot/Wasp.AspNetCore.Blazor.Server/Vendor/Microsoft.AspNetCore.Components.Server.dll

using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: circuit-host-weaver <input.dll> <output.dll> [--rewrite-typenamehash]");
    return 1;
}

string inputPath = args[0];
string outputPath = args[1];
bool rewriteTypeNameHash = args.Length > 2 && args[2] == "--rewrite-typenamehash";

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"input not found: {inputPath}");
    return 2;
}

var inputDir = Path.GetDirectoryName(Path.GetFullPath(inputPath))!;
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(inputDir);

using var assembly = AssemblyDefinition.ReadAssembly(inputPath, new ReaderParameters
{
    AssemblyResolver = resolver,
    ReadWrite = false,
    InMemory = true,
});

var module = assembly.MainModule;

string[] targetNamespacePrefixes = rewriteTypeNameHash
    ? new[] {
        // When weaving Components.Endpoints.dll, widen visibility on the
        // internal types we need to construct from outside (IClearableStore,
        // RootComponentOperationBatch, ComponentMarker, etc.). Without this
        // CircuitHubFacade.UpdateRootComponents can't call CircuitHost's
        // internal-typed signature.
        "Microsoft.AspNetCore.Components.Endpoints",
        "Microsoft.AspNetCore.Components.Infrastructure",
    }
    : new[] {
        "Microsoft.AspNetCore.Components.Server",
    };

int typesPromoted = 0;
int membersPromoted = 0;

foreach (var type in module.GetAllTypes().ToArray())
{
    if (!IsInTargetNamespace(type, targetNamespacePrefixes)) continue;

    if (PromoteTypeVisibility(type)) typesPromoted++;

    foreach (var method in type.Methods)
    {
        if (method.IsAssembly || method.IsFamilyAndAssembly)
        {
            method.IsPublic = true;
            membersPromoted++;
        }
    }
    foreach (var field in type.Fields)
    {
        if (field.IsAssembly || field.IsFamilyAndAssembly)
        {
            field.IsPublic = true;
            membersPromoted++;
        }
    }
    // Properties / events derive their visibility from underlying methods,
    // which we already promoted above.
}

// Optionally rewrite framework methods that call into wasm32-wasi-unsupported
// crypto APIs (SHA256, RandomNumberGenerator). Used when weaving
// Microsoft.AspNetCore.Components.Endpoints.dll.
if (rewriteTypeNameHash)
{
    var typeNameHash = module.GetType("Microsoft.AspNetCore.Components.Endpoints.TypeNameHash");
    if (typeNameHash is not null)
    {
        var compute = typeNameHash.Methods.FirstOrDefault(m =>
            m.Name == "Compute"
            && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "System.Type");
        if (compute is not null && compute.ReturnType.FullName == "System.String")
        {
            compute.Body.Instructions.Clear();
            compute.Body.ExceptionHandlers.Clear();
            compute.Body.Variables.Clear();
            var il = compute.Body.GetILProcessor();
            var fullNameProp = module.ImportReference(
                typeof(System.Type).GetProperty("FullName")!.GetGetMethod()!);
            il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
            il.Emit(Mono.Cecil.Cil.OpCodes.Callvirt, fullNameProp);
            il.Emit(Mono.Cecil.Cil.OpCodes.Ret);
            Console.WriteLine("rewrote: TypeNameHash.Compute → type.FullName");
        }
    }

    // ServerComponentInvocationSequence..ctor() calls RandomNumberGenerator.Fill
    // to seed a per-invocation unique id. RNG is PlatformNotSupported on
    // wasm32-wasi. Rewrite the ctor to do nothing — the resulting all-zero
    // sequence id is stable per request (acceptable for canister model).
    var sequenceType = module.GetType("Microsoft.AspNetCore.Components.Endpoints.ServerComponentInvocationSequence");
    if (sequenceType is not null)
    {
        var ctor = sequenceType.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
        if (ctor is not null)
        {
            ctor.Body.Instructions.Clear();
            ctor.Body.ExceptionHandlers.Clear();
            ctor.Body.Variables.Clear();
            var il = ctor.Body.GetILProcessor();
            // call base ctor if it's a class (not struct)
            if (!sequenceType.IsValueType)
            {
                var objectCtor = module.ImportReference(
                    typeof(object).GetConstructor(System.Type.EmptyTypes)!);
                il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
                il.Emit(Mono.Cecil.Cil.OpCodes.Call, objectCtor);
            }
            il.Emit(Mono.Cecil.Cil.OpCodes.Ret);
            Console.WriteLine("rewrote: ServerComponentInvocationSequence..ctor() → no-op");
        }
    }

    // ResourceCollectionResolver type uses RandomNumberGenerator too for
    // resource asset IDs. The @Assets[...] Razor expression we already
    // dropped goes through this. Defensive stub.
    foreach (var t in module.GetAllTypes().ToArray())
    {
        if (t.Namespace?.StartsWith("Microsoft.AspNetCore.Components.Endpoints") != true) continue;
        foreach (var m in t.Methods)
        {
            if (!m.HasBody) continue;
            var body = m.Body;
            bool patched = false;
            for (int i = 0; i < body.Instructions.Count; i++)
            {
                var ins = body.Instructions[i];
                if (ins.OpCode.Code != Mono.Cecil.Cil.Code.Call &&
                    ins.OpCode.Code != Mono.Cecil.Cil.Code.Callvirt) continue;
                if (ins.Operand is not MethodReference mr) continue;
                if (mr.DeclaringType.FullName == "System.Security.Cryptography.SHA256"
                    && mr.Name == "HashData")
                {
                    // Replace the SHA256.HashData call with: pop args, push null byte[].
                    // For HashData(ReadOnlySpan, Span) → int: pop both spans, push 0.
                    // For HashData(byte[]) → byte[]: pop arg, push empty byte[].
                    // Conservative: leave for now and document.
                    patched = false;
                }
            }
            // No-op: we already replaced the callers above for the two known cases.
            _ = patched;
        }
    }
}

// Strip the [InternalsVisibleTo] attributes — once everything is public,
// keeping them clutters tooling output without affecting behavior.
var asmAttrs = assembly.CustomAttributes
    .Where(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute")
    .ToList();
foreach (var attr in asmAttrs)
{
    assembly.CustomAttributes.Remove(attr);
}

// Force ILOnly so Cecil can write the framework assembly back out — it's
// flagged mixed-mode in the PE header by the .NET shared-framework build,
// even though the body is pure IL.
module.Attributes |= ModuleAttributes.ILOnly;

// Drop the strong-name signature: once we've rewritten the assembly we
// cannot re-sign it, so leave it unsigned.
if (assembly.Name.HasPublicKey)
{
    assembly.Name.PublicKey = Array.Empty<byte>();
    assembly.Name.HasPublicKey = false;
    assembly.MainModule.Attributes &= ~ModuleAttributes.StrongNameSigned;
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
assembly.Write(outputPath);

Console.WriteLine($"promoted {typesPromoted} types and {membersPromoted} members");
Console.WriteLine($"removed {asmAttrs.Count} [InternalsVisibleTo] attributes");
Console.WriteLine($"wrote: {outputPath}");
return 0;

static bool IsInTargetNamespace(TypeDefinition type, string[] prefixes)
{
    string ns = type.Namespace ?? string.Empty;
    foreach (var p in prefixes)
    {
        if (ns == p) return true;
        if (ns.StartsWith(p + ".", StringComparison.Ordinal)) return true;
    }
    return false;
}

static bool PromoteTypeVisibility(TypeDefinition type)
{
    bool changed = false;
    if (type.IsNotPublic)
    {
        type.IsPublic = true;
        changed = true;
    }
    else if (type.IsNestedAssembly || type.IsNestedFamilyAndAssembly)
    {
        type.IsNestedPublic = true;
        changed = true;
    }

    // Recurse into nested types.
    foreach (var nested in type.NestedTypes.ToArray())
    {
        PromoteTypeVisibility(nested);
    }
    return changed;
}

internal static class ModuleExtensions
{
    public static System.Collections.Generic.IEnumerable<TypeDefinition> GetAllTypes(this ModuleDefinition module)
    {
        var stack = new System.Collections.Generic.Stack<TypeDefinition>();
        foreach (var t in module.Types) stack.Push(t);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            yield return t;
            foreach (var n in t.NestedTypes) stack.Push(n);
        }
    }
}
