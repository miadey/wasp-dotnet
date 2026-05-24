using Microsoft.JSInterop;

namespace Crm.Services;

/// <summary>
/// Thin C# wrapper around the @dfinity/auth-client JS module
/// (auth.js in wwwroot). Provides the principal (when authenticated)
/// that ApiClient sticks onto requests via X-Wasp-Principal so the
/// canister knows who's calling — for the demo this is server-
/// trusted; production-grade auth would use Candid-signed ingress.
/// </summary>
public sealed class AuthService
{
    private readonly IJSRuntime _js;
    private bool _initialized;

    public AuthService(IJSRuntime js) { _js = js; }

    public string? Principal { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrEmpty(Principal);
    public string ShortPrincipal => string.IsNullOrEmpty(Principal)
        ? ""
        : (Principal!.Length > 12 ? Principal.Substring(0, 5) + "…" + Principal.Substring(Principal.Length - 4) : Principal);

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            Principal = await _js.InvokeAsync<string?>("crmAuth.init");
        }
        catch { /* II not loaded — anonymous browsing still works */ }
    }

    public string? LastError { get; private set; }

    public async Task LoginAsync()
    {
        LastError = null;
        try
        {
            Principal = await _js.InvokeAsync<string?>("crmAuth.login");
            if (string.IsNullOrEmpty(Principal))
            {
                // login returned null — pull the last error string the JS
                // module captured so the user sees something more useful
                // than a button that did nothing.
                try { LastError = await _js.InvokeAsync<string?>("crmAuth.lastError"); } catch { }
                if (string.IsNullOrEmpty(LastError)) LastError = "Internet Identity sign-in was cancelled or blocked.";
            }
        }
        catch (Exception ex)
        {
            Principal = null;
            LastError = "JS interop failed: " + ex.Message;
        }
    }

    public async Task LogoutAsync()
    {
        try { await _js.InvokeVoidAsync("crmAuth.logout"); }
        catch { }
        Principal = null;
    }
}
