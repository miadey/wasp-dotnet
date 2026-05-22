using System;
using System.Text;
using Wasp.AspNetCore.Blazor.Server;
using Xunit;

namespace Wasp.AspNetCore.Blazor.Server.Tests;

// Locks the marker-descriptor format that BlazorMarkerMiddleware emits
// and WaspComponentRecordParser.Parse consumes. If either side drifts,
// these tests break.
public class ParseWaspComponentRecordsTests
{
    private static string EncodeDescriptor(string assemblyName, string typeName)
    {
        var json = $"{{\"componentAssembly\":\"{assemblyName}\",\"componentType\":\"{typeName}\"}}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public void EmptyInput_ReturnsEmptyList()
        => Assert.Empty(WaspComponentRecordParser.Parse(""));

    [Fact]
    public void NotAnArray_ReturnsEmptyList()
        => Assert.Empty(WaspComponentRecordParser.Parse("{\"type\":\"server\"}"));

    [Fact]
    public void NonServerMarker_IsSkipped()
    {
        var json = "[{\"type\":\"webassembly\",\"sequence\":0,\"descriptor\":\"" +
                   EncodeDescriptor("Wasp.AspNetCore.Blazor.Server.Tests", typeof(SampleComponent).FullName!) +
                   "\"}]";
        Assert.Empty(WaspComponentRecordParser.Parse(json));
    }

    [Fact]
    public void ValidServerMarker_ResolvesType()
    {
        var typeName = typeof(SampleComponent).FullName!;
        var asmName = typeof(SampleComponent).Assembly.GetName().Name!;
        var json = $"[{{\"type\":\"server\",\"sequence\":0,\"descriptor\":\"{EncodeDescriptor(asmName, typeName)}\"}}]";

        var result = WaspComponentRecordParser.Parse(json);

        Assert.Single(result);
        Assert.Equal(typeof(SampleComponent), result[0].Type);
        Assert.Equal(0, result[0].Sequence);
    }

    [Fact]
    public void TwoMarkers_PreservesSequence()
    {
        var asmName = typeof(SampleComponent).Assembly.GetName().Name!;
        var typeName = typeof(SampleComponent).FullName!;
        var d = EncodeDescriptor(asmName, typeName);
        var json = "[" +
            $"{{\"type\":\"server\",\"sequence\":0,\"descriptor\":\"{d}\"}}," +
            $"{{\"type\":\"server\",\"sequence\":1,\"descriptor\":\"{d}\"}}" +
            "]";

        var result = WaspComponentRecordParser.Parse(json);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].Sequence);
        Assert.Equal(1, result[1].Sequence);
    }

    [Fact]
    public void UnknownType_IsSkipped()
    {
        var json = "[{\"type\":\"server\",\"sequence\":0,\"descriptor\":\"" +
                   EncodeDescriptor("NoSuchAssembly", "NoSuchType") +
                   "\"}]";
        Assert.Empty(WaspComponentRecordParser.Parse(json));
    }

    [Fact]
    public void MalformedJson_ReturnsEmptyList()
        => Assert.Empty(WaspComponentRecordParser.Parse("not json at all"));

    [Fact]
    public void MissingDescriptor_IsSkipped()
        => Assert.Empty(WaspComponentRecordParser.Parse("[{\"type\":\"server\",\"sequence\":0}]"));

    public sealed class SampleComponent : Microsoft.AspNetCore.Components.ComponentBase { }
}
