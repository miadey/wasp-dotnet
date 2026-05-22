using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wasp.AspNetCore;
using Wasp.IcCdk;

namespace WaspSample.TodoEf;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Todo))]
[JsonSerializable(typeof(Todo[]))]
[JsonSerializable(typeof(CreateRequest))]
[JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, object>))]
[JsonSerializable(typeof(object))]
internal partial class TodoJsonCtx : JsonSerializerContext { }

public sealed record CreateRequest(string Title);

public static class Program
{
    private static TodoContext? _db;

    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                ContentRootPath = "/canister",
                ApplicationName = "TodoEf",
            });

            builder.UseInternetComputer();
            builder.Services.AddRoutingCore();    // minimal-API endpoints need this
            builder.Services.ConfigureHttpJsonOptions(o =>
            {
                o.SerializerOptions.TypeInfoResolverChain.Clear();
                o.SerializerOptions.TypeInfoResolverChain.Insert(0, TodoJsonCtx.Default);
            });

            // Single-tenant DbContext, eagerly hydrated from stable
            // memory. The render-as-query model means each request
            // gets the same DbContext singleton — single-threaded
            // canister + no per-request scoping needed.
            var jsonOpts = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                TypeInfoResolver = TodoJsonCtx.Default,
            };
            _db = new TodoContext(jsonOpts);
            _db.Load();
            builder.Services.AddSingleton(_db);

            var app = builder.Build();

            app.MapGet("/", () => Results.Text(
                "TodoEf — EF-Core-style DbContext on the IC.\n" +
                "  GET    /todos             list all\n" +
                "  POST   /todos             body { \"title\": \"...\" }\n" +
                "  POST   /todos/{id}/toggle\n" +
                "  DELETE /todos/{id}\n",
                "text/plain"));

            app.MapGet("/todos", (TodoContext db) => db.Todos.OrderBy(t => t.Id).ToArray());

            app.MapPost("/todos", async (HttpContext ctx, TodoContext db) =>
            {
                using var ms = new System.IO.MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms);
                var req = JsonSerializer.Deserialize(ms.ToArray(), TodoJsonCtx.Default.CreateRequest);
                return Results.Json(db.Create(req?.Title ?? "(no title)"));
            });

            app.MapPost("/todos/{id:int}/toggle", (int id, TodoContext db) =>
            {
                var todo = db.Todos.FirstOrDefault(t => t.Id == id);
                if (todo is null) return Results.NotFound();
                todo.Done = !todo.Done;
                db.SaveChanges();
                return Results.Json(todo);
            });

            app.MapDelete("/todos/{id:int}", (int id, TodoContext db) =>
            {
                var todo = db.Todos.FirstOrDefault(t => t.Id == id);
                if (todo is null) return Results.NotFound();
                db.Todos.Remove(todo);
                db.SaveChanges();
                return Results.NoContent();
            });

            app.RunOnIC();
        }
        catch (Exception ex)
        {
            var msg = ex.GetType().FullName + ": " + ex.Message
                + (ex.StackTrace is { } st ? "\n" + st : "");
            IcServer.InitFailureMessage = msg;
            Reply.Print("[init-fail] " + msg);
        }
    }
}
