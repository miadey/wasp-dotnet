using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using Xunit;

namespace Wasp.AspNetCore.Blazor.Server.Tests;

// Verifies the Vendor/Microsoft.AspNetCore.Components.Server.dll produced
// by shared/tools/Wasp.CircuitHostWeaver actually has the previously
// internal types accessible.
//
// This test loads the DLL into an isolated MetadataLoadContext (so it
// doesn't clash with the framework-resolved version) and asserts:
//  - The expected internal types now report IsPublic.
//  - [InternalsVisibleTo] attributes have been stripped.
//
// Test is skipped if the vendored DLL isn't present (a fresh clone might
// not have run the weaver yet).
public sealed class VendoredComponentsServerTests
{
    private static readonly string VendorPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory,
            "../../../../Wasp.AspNetCore.Blazor.Server/Vendor/Microsoft.AspNetCore.Components.Server.dll"));

    private static bool VendorMissing => !File.Exists(VendorPath);

    [Fact]
    public void Vendored_dll_exists_after_weaver_run()
    {
        // If this fails, run:
        //   dotnet run --project shared/tools/Wasp.CircuitHostWeaver -- \
        //     /usr/local/share/dotnet/shared/Microsoft.AspNetCore.App/10.0.*/Microsoft.AspNetCore.Components.Server.dll \
        //     aot/Wasp.AspNetCore.Blazor.Server/Vendor/Microsoft.AspNetCore.Components.Server.dll
        Assert.True(File.Exists(VendorPath),
            $"vendored DLL missing at {VendorPath}");
    }

    [Fact]
    public void CircuitFactory_is_public_after_weaving()
    {
        if (VendorMissing) return; // covered by Vendored_dll_exists_after_weaver_run
        using var mlc = NewMetadataLoadContext();
        var asm = mlc.LoadFromAssemblyPath(VendorPath);
        var type = asm.GetType("Microsoft.AspNetCore.Components.Server.Circuits.CircuitFactory");
        Assert.NotNull(type);
        Assert.True(type!.IsPublic, "CircuitFactory should be public after weaving");
    }

    [Fact]
    public void CircuitHost_is_public_after_weaving()
    {
        if (VendorMissing) return;
        using var mlc = NewMetadataLoadContext();
        var asm = mlc.LoadFromAssemblyPath(VendorPath);
        var type = asm.GetType("Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost");
        Assert.NotNull(type);
        Assert.True(type!.IsPublic, "CircuitHost should be public after weaving");
    }

    [Fact]
    public void CircuitClientProxy_is_public_after_weaving()
    {
        if (VendorMissing) return;
        using var mlc = NewMetadataLoadContext();
        var asm = mlc.LoadFromAssemblyPath(VendorPath);
        var type = asm.GetType("Microsoft.AspNetCore.Components.Server.Circuits.CircuitClientProxy");
        Assert.NotNull(type);
        Assert.True(type!.IsPublic, "CircuitClientProxy should be public after weaving");
    }

    [Fact]
    public void CircuitHost_CreateAsync_method_is_public_after_weaving()
    {
        if (VendorMissing) return;
        using var mlc = NewMetadataLoadContext();
        var asm = mlc.LoadFromAssemblyPath(VendorPath);
        var type = asm.GetType("Microsoft.AspNetCore.Components.Server.Circuits.CircuitFactory")!;
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "CreateCircuitHostAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void InternalsVisibleTo_attributes_stripped()
    {
        if (VendorMissing) return;
        using var mlc = NewMetadataLoadContext();
        var asm = mlc.LoadFromAssemblyPath(VendorPath);
        var asmAttrs = asm.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute")
            .ToList();
        Assert.Empty(asmAttrs);
    }

    private static MetadataLoadContext NewMetadataLoadContext()
    {
        // Use the runtime assemblies as resolution sources so Cecil-rewritten
        // type refs (System.*, Microsoft.AspNetCore.*) resolve cleanly.
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var aspnetDir = Path.GetFullPath(Path.Combine(runtimeDir,
            "../../../shared/Microsoft.AspNetCore.App"));
        var version = Directory.Exists(aspnetDir)
            ? Directory.GetDirectories(aspnetDir).OrderByDescending(p => p).First()
            : null;

        var resolverPaths = new System.Collections.Generic.List<string>();
        resolverPaths.AddRange(Directory.GetFiles(runtimeDir, "*.dll"));
        if (version is not null)
        {
            resolverPaths.AddRange(Directory.GetFiles(version, "*.dll")
                .Where(p => !Path.GetFileName(p).Equals(
                    "Microsoft.AspNetCore.Components.Server.dll", StringComparison.OrdinalIgnoreCase)));
        }
        resolverPaths.Add(VendorPath);

        var resolver = new PathAssemblyResolver(resolverPaths.Distinct().ToArray());
        return new MetadataLoadContext(resolver);
    }
}
