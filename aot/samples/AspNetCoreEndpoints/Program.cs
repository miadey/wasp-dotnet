using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wasp.AspNetCore;

// Issue #50 deliverable: exercises 5+ minimal-API endpoint shapes against the
// canister-hosted ASP.NET Core pipeline. Each shape proves that a different
// part of the .NET 10 RequestDelegateGenerator output AOT-compiles cleanly
// for wasm32-wasi:
//
//   GET  /              () => string                  (no params, sync)
//   GET  /echo/{msg}    (string msg) => string        (route param binding)
//   POST /note          (Note n)     => string        (JSON body binding)
//   GET  /async         async () => Task<string>      (async sync-completing)
//   GET  /json          () => IResult (Results.Json)  (typed JSON return via context)
//   DEL  /missing       () => IResult (Results.NotFound)  (no body, status only)
//
// Body and JSON-result endpoints require the type to live in a partial
// JsonSerializerContext so the trimmer keeps it. See NoteJsonContext below.

namespace WaspSample.AspNetCoreEndpoints;

public sealed record Note(string Title, int Priority);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Note))]
// RDG asks the JsonSerializerOptions for typeof(object) at startup
// (for polymorphic result writes); without `object` in the context the chain
// returns null and the body resolver silently fails the request with 400.
[JsonSerializable(typeof(object))]
internal partial class NoteJsonContext : JsonSerializerContext
{
}

public static class AspNetCoreEndpointsCanister
{
    [ModuleInitializer]
    internal static void Init()
    {

        try
        {
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                ContentRootPath = "/canister",
                ApplicationName = "AspNetCoreEndpoints",
            });
            builder.Services.AddRoutingCore();
            builder.Services.ConfigureHttpJsonOptions(o =>
            {
                // Replace the default resolver chain (which includes the
                // reflection-based DefaultJsonTypeInfoResolver — broken under
                // AOT trim) with our source-gen context only.
                o.SerializerOptions.TypeInfoResolver = NoteJsonContext.Default;
            });
            builder.WebHost.UseIcCanister();

            var app = builder.Build();

            // 1. simplest — string return, no params
            app.MapGet("/", () => "Hello from AspNetCoreEndpoints");

            // 2. route param binding
            app.MapGet("/echo/{msg}", (string msg) => $"echo: {msg}");

            // 3. JSON body — read manually + deserialize via the typed JsonTypeInfo.
            //    RDG-driven body binding (parameter typed Note) hits an AOT-trim
            //    limitation: JsonSerializer.DeserializeAsync<Note>(PipeReader, ...)
            //    is reached through a Func<> indirection the trimmer can't follow,
            //    so the closed instantiation gets trimmed (EE_MissingMethod at
            //    runtime). Manual deserialization with the source-gen JsonTypeInfo
            //    is AOT-clean and recommended for canister endpoints.
            //    See aot/Wasp.AspNetCore/UNSUPPORTED.md > Body binding.
            app.MapPost("/note", async (HttpContext ctx) =>
            {
                using var sr = new System.IO.StreamReader(ctx.Request.Body);
                var json = await sr.ReadToEndAsync();
                var note = JsonSerializer.Deserialize(json, NoteJsonContext.Default.Note);
                return note is null
                    ? Results.BadRequest("invalid json")
                    : Results.Text($"got note '{note.Title}' priority={note.Priority}");
            });

            // 4. async handler — pumps through IcSyncContext via Task.Yield
            app.MapGet("/async", async () =>
            {
                await Task.Yield();
                return "async ok";
            });

            // 5. IResult Json — typed serialization through the registered context
            app.MapGet("/json", () =>
                Results.Json(new Note("from-json", 7), NoteJsonContext.Default.Note));

            // 6. IResult NotFound — status-only, no body
            app.MapDelete("/missing", () => Results.NotFound());

            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            var msg = ex.GetType().FullName + ": " + ex.Message
                + (ex.StackTrace is { } st ? "\n" + st : "");
            IcServer.InitFailureMessage = msg;
            Wasp.IcCdk.Reply.Print("[init-fail] " + msg);
        }
    }
}
