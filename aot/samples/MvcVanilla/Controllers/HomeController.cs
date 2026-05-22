using Microsoft.AspNetCore.Mvc;

namespace MvcVanilla.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => View();

    public IActionResult Privacy() => View();
}
