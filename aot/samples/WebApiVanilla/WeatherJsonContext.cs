using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WaspSample.WebApiVanilla;

// gh #95 (M4.S9.5a) — STJ source-gen context for the WeatherForecast
// surface. NativeAOT-LLVM trims the reflection-based JsonTypeInfoResolver,
// so MVC's SystemTextJsonOutputFormatter cannot serialize an action's
// IEnumerable<WeatherForecast> return without an explicit resolver.
//
// Wired in Program.cs via:
//   builder.Services.AddControllers().AddJsonOptions(o =>
//       o.JsonSerializerOptions.TypeInfoResolverChain.Insert(
//           0, WeatherJsonContext.Default));
[JsonSerializable(typeof(WeatherForecast))]
[JsonSerializable(typeof(WeatherForecast[]))]
[JsonSerializable(typeof(IEnumerable<WeatherForecast>))]
internal partial class WeatherJsonContext : JsonSerializerContext
{
}
