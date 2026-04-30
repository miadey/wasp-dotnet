using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Wasp.AspNetCore;

/// <summary>
/// Extension methods for wiring <see cref="IcServer"/> into a
/// <see cref="IWebHostBuilder"/>.
/// </summary>
public static class HostingExtensions
{
    /// <summary>
    /// Replaces the default Kestrel <see cref="IServer"/> with
    /// <see cref="IcServer"/>, which routes IC HTTP gateway requests through
    /// the ASP.NET Core middleware pipeline instead of a TCP socket.
    /// </summary>
    public static IWebHostBuilder UseIcCanister(this IWebHostBuilder builder)
        => builder.ConfigureServices(services =>
            services.AddSingleton<IServer, IcServer>());
}
