using System;

namespace WaspSample.WebApiVanilla;

// Stock `dotnet new webapi` model — copied verbatim except for the
// namespace.
public sealed record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
