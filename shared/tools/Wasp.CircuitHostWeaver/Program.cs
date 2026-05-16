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

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: circuit-host-weaver <input.dll> <output.dll>");
    return 1;
}

string inputPath = args[0];
string outputPath = args[1];

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

string[] targetNamespacePrefixes =
{
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
