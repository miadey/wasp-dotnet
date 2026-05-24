using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Crm.Models;

namespace Crm.Services;

/// <summary>
/// Typed API client for /api/crm/* endpoints on razoronic. Attaches
/// the current principal (when signed in) so the server can attribute
/// activities + presence to the caller.
/// </summary>
public sealed class ApiClient
{
    private readonly HttpClient _http;
    private readonly AuthService _auth;

    public ApiClient(HttpClient http, AuthService auth)
    {
        _http = http; _auth = auth;
    }

    private void AttachPrincipal(HttpRequestMessage req)
    {
        if (_auth.IsAuthenticated)
            req.Headers.Add("X-Wasp-Principal", _auth.Principal!);
    }

    // ─── Contacts ────────────────────────────────────────────────
    public async Task<Contact[]> ListContactsAsync(string? search = null)
    {
        try
        {
            var url = "/api/crm/contacts" + (string.IsNullOrEmpty(search) ? "" : "?q=" + Uri.EscapeDataString(search));
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            AttachPrincipal(req);
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return Array.Empty<Contact>();
            var page = await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.ContactList);
            return page?.Items ?? Array.Empty<Contact>();
        }
        catch { return Array.Empty<Contact>(); }
    }

    public async Task<Contact?> GetContactAsync(long id)
    {
        try
        {
            // Canister query handlers are exact-path; the canister
            // exposes /api/crm/contact-get?id=N for fast (~300 ms) read.
            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/crm/contact-get?id={id}");
            AttachPrincipal(req);
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.Contact);
        }
        catch { return null; }
    }

    public async Task<Contact?> CreateContactAsync(Contact c)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/crm/contacts")
            {
                Content = JsonContent.Create(c, CrmJsonContext.Default.Contact),
            };
            AttachPrincipal(req);
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.Contact);
        }
        catch { return null; }
    }

    public async Task<Contact?> UpdateContactAsync(Contact c)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Put, $"/api/crm/contacts/{c.Id}")
            {
                Content = JsonContent.Create(c, CrmJsonContext.Default.Contact),
            };
            AttachPrincipal(req);
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.Contact);
        }
        catch { return null; }
    }

    public async Task<bool> DeleteContactAsync(long id)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/crm/contacts/{id}");
            AttachPrincipal(req);
            var resp = await _http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ─── Presence ────────────────────────────────────────────────
    public async Task HeartbeatAsync(string recordId)
    {
        try
        {
            var name = _auth.IsAuthenticated ? _auth.ShortPrincipal : "Guest";
            var principal = _auth.Principal ?? "anon-" + Random.Shared.Next(1000, 9999);
            var body = JsonContent.Create(new PresenceEntry { Principal = principal, Name = name }, CrmJsonContext.Default.PresenceEntry);
            var req = new HttpRequestMessage(HttpMethod.Post, $"/api/crm/presence-heartbeat?recordId={Uri.EscapeDataString(recordId)}") { Content = body };
            AttachPrincipal(req);
            await _http.SendAsync(req);
        }
        catch { /* best-effort */ }
    }

    public async Task<PresenceEntry[]> GetPresenceAsync(string recordId)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/crm/presence-get?recordId={Uri.EscapeDataString(recordId)}");
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return Array.Empty<PresenceEntry>();
            var page = await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.PresenceList);
            return page?.Viewers ?? Array.Empty<PresenceEntry>();
        }
        catch { return Array.Empty<PresenceEntry>(); }
    }

    // ─── Companies ────────────────────────────────────────────────
    public async Task<Company[]> ListCompaniesAsync()
    {
        try
        {
            var resp = await _http.GetAsync("/api/crm/companies");
            if (!resp.IsSuccessStatusCode) return Array.Empty<Company>();
            var page = await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.CompanyList);
            return page?.Items ?? Array.Empty<Company>();
        } catch { return Array.Empty<Company>(); }
    }
    public async Task<Company?> CreateCompanyAsync(Company c)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/crm/companies")
        { Content = JsonContent.Create(c, CrmJsonContext.Default.Company) };
        AttachPrincipal(req);
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.Company);
    }
    public async Task<Company?> UpdateCompanyAsync(Company c)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/crm/companies/{c.Id}")
        { Content = JsonContent.Create(c, CrmJsonContext.Default.Company) };
        AttachPrincipal(req);
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.Company);
    }
    public async Task<bool> DeleteCompanyAsync(long id)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/crm/companies/{id}");
        AttachPrincipal(req);
        var resp = await _http.SendAsync(req);
        return resp.IsSuccessStatusCode;
    }

    // ─── Deals ────────────────────────────────────────────────────
    public async Task<Deal[]> ListDealsAsync()
    {
        try
        {
            var resp = await _http.GetAsync("/api/crm/deals");
            if (!resp.IsSuccessStatusCode) return Array.Empty<Deal>();
            var page = await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.DealList);
            return page?.Items ?? Array.Empty<Deal>();
        } catch { return Array.Empty<Deal>(); }
    }
    public async Task<Deal?> CreateDealAsync(Deal d)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/crm/deals")
        { Content = JsonContent.Create(d, CrmJsonContext.Default.Deal) };
        AttachPrincipal(req);
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.Deal);
    }
    public async Task<Deal?> UpdateDealAsync(Deal d)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/crm/deals/{d.Id}")
        { Content = JsonContent.Create(d, CrmJsonContext.Default.Deal) };
        AttachPrincipal(req);
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.Deal);
    }
    public async Task<bool> DeleteDealAsync(long id)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/crm/deals/{id}");
        AttachPrincipal(req);
        var resp = await _http.SendAsync(req);
        return resp.IsSuccessStatusCode;
    }
    public async Task<Deal?> MoveDealStageAsync(long id, int stage)
    {
        var payload = "{\"id\":" + id + ",\"stage\":" + stage + "}";
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/crm/deal-stage")
        { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        AttachPrincipal(req);
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.Deal);
    }

    // ─── Activities ───────────────────────────────────────────────
    public async Task<Activity[]> ListActivitiesAsync(long contactId = 0, long dealId = 0)
    {
        try
        {
            var url = $"/api/crm/activities?contactId={contactId}&dealId={dealId}";
            var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return Array.Empty<Activity>();
            var page = await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.ActivityList);
            return page?.Items ?? Array.Empty<Activity>();
        } catch { return Array.Empty<Activity>(); }
    }
    public async Task<Activity?> CreateActivityAsync(Activity a)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/crm/activities")
        { Content = JsonContent.Create(a, CrmJsonContext.Default.Activity) };
        AttachPrincipal(req);
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.Activity);
    }
    public async Task<bool> DeleteActivityAsync(long id)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/crm/activities/{id}");
        AttachPrincipal(req);
        var resp = await _http.SendAsync(req);
        return resp.IsSuccessStatusCode;
    }

    // ─── Tasks ────────────────────────────────────────────────────
    public async Task<TaskItem[]> ListTasksAsync()
    {
        try
        {
            var resp = await _http.GetAsync("/api/crm/tasks");
            if (!resp.IsSuccessStatusCode) return Array.Empty<TaskItem>();
            var page = await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.TaskList);
            return page?.Items ?? Array.Empty<TaskItem>();
        } catch { return Array.Empty<TaskItem>(); }
    }
    public async Task<TaskItem?> CreateTaskAsync(TaskItem t)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/crm/tasks")
        { Content = JsonContent.Create(t, CrmJsonContext.Default.TaskItem) };
        AttachPrincipal(req);
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.TaskItem);
    }
    public async Task<TaskItem?> UpdateTaskAsync(TaskItem t)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/crm/tasks/{t.Id}")
        { Content = JsonContent.Create(t, CrmJsonContext.Default.TaskItem) };
        AttachPrincipal(req);
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.TaskItem);
    }
    public async Task<bool> DeleteTaskAsync(long id)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/crm/tasks/{id}");
        AttachPrincipal(req);
        var resp = await _http.SendAsync(req);
        return resp.IsSuccessStatusCode;
    }

    // ─── Lead-score, Dashboard, Search ───────────────────────────
    public async Task<int> LeadScoreAsync(long contactId)
    {
        try
        {
            var resp = await _http.GetAsync($"/api/crm/lead-score?contactId={contactId}");
            if (!resp.IsSuccessStatusCode) return 0;
            var s = await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.LeadScore);
            return s?.Score ?? 0;
        } catch { return 0; }
    }

    public async Task<DashboardData?> GetDashboardAsync()
    {
        try
        {
            var resp = await _http.GetAsync("/api/crm/dashboard");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.DashboardData);
        } catch { return null; }
    }

    public async Task<SearchResults?> SearchAsync(string q)
    {
        try
        {
            var resp = await _http.GetAsync($"/api/crm/search?q={Uri.EscapeDataString(q)}");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync(CrmJsonContext.Default.SearchResults);
        } catch { return null; }
    }
}
