using Microsoft.AspNetCore.Mvc;

namespace MvcOnIcp.Controllers;

public sealed class HomeController : Controller
{
    public IActionResult Index() => View();
    public IActionResult Privacy() => View();
}
