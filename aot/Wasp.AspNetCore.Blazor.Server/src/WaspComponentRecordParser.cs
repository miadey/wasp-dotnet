using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Wasp.AspNetCore.Blazor.Server;

// Walks the JSON array of marker records that blazor.web.js ships in
// StartCircuit's `serializedComponentRecords` argument. Each entry is
// one <!--Blazor:{...}--> marker. For type=="server" entries we decode
// the descriptor (base64 of our synthetic JSON) and resolve a Type.
//
// JsonDocument uses Utf8JsonReader which is reflection-free — safe
// under wasm32-wasi without JsonSerializerIsReflectionEnabledByDefault.
//
// Returns plain (Type, sequence) pairs so the caller can construct
// framework-internal ComponentDescriptor instances. Keeps this parser
// dependency-free of the Components.Server.dll internals (which lets
// tests run against the framework's stock DLL).
internal static class WaspComponentRecordParser
{
    public static IReadOnlyList<(Type Type, int Sequence)> Parse(string serializedComponentRecords)
    {
        var list = new List<(Type, int)>();
        if (string.IsNullOrEmpty(serializedComponentRecords)) return list;

        try
        {
            using var doc = JsonDocument.Parse(serializedComponentRecords);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("type", out var typeEl) ||
                    typeEl.ValueKind != JsonValueKind.String ||
                    typeEl.GetString() != "server")
                {
                    continue;
                }
                if (!entry.TryGetProperty("descriptor", out var descEl) ||
                    descEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                int sequence = 0;
                if (entry.TryGetProperty("sequence", out var seqEl) &&
                    seqEl.ValueKind == JsonValueKind.Number)
                {
                    sequence = seqEl.GetInt32();
                }

                var descBase64 = descEl.GetString();
                if (string.IsNullOrEmpty(descBase64)) continue;

                byte[] descBytes;
                try { descBytes = Convert.FromBase64String(descBase64); }
                catch { continue; }
                var descJson = Encoding.UTF8.GetString(descBytes);

                using var descDoc = JsonDocument.Parse(descJson);
                if (descDoc.RootElement.ValueKind != JsonValueKind.Object) continue;
                if (!descDoc.RootElement.TryGetProperty("componentAssembly", out var asmEl) ||
                    !descDoc.RootElement.TryGetProperty("componentType", out var ctypeEl))
                {
                    continue;
                }

                var asmName = asmEl.GetString();
                var typeName = ctypeEl.GetString();
                if (string.IsNullOrEmpty(asmName) || string.IsNullOrEmpty(typeName)) continue;

                var resolved = Type.GetType($"{typeName}, {asmName}", throwOnError: false);
                if (resolved is null) continue;

                list.Add((resolved, sequence));
            }
        }
        catch
        {
            // Caller handles empty result as "no descriptors recognized";
            // a parse failure here is not fatal — the circuit just starts
            // with no root components and waits for UpdateRootComponents.
        }

        return list;
    }
}
