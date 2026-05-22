using Microsoft.AspNetCore.Mvc;

namespace WebApiOnIcp.Controllers;

[ApiController]
[Route("[controller]")]
public class WeatherForecastController : ControllerBase
{
    private static readonly string[] Summaries =
    {
        "Freezing", "Bracing", "Chilly", "Cool", "Mild",
        "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    };

    [HttpGet(Name = "GetWeatherForecast")]
    public IEnumerable<WeatherForecast> Get()
    {
        var rng = new Random((int)(Wasp.IcCdk.Ic0.time() >> 16));
        return Enumerable.Range(1, 5).Select(index => new WeatherForecast(
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(index)),
            rng.Next(-20, 55),
            Summaries[rng.Next(Summaries.Length)]
        )).ToArray();
    }
}
