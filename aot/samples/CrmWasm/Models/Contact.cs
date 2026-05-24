using System.Text.Json.Serialization;

namespace Crm.Models;

public sealed class Contact
{
    public long Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Company { get; set; } = "";
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Tags { get; set; } = "";  // comma-separated for v1
    public long CreatedAtMs { get; set; }
    public long UpdatedAtMs { get; set; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(FirstName) && string.IsNullOrWhiteSpace(LastName)
            ? (string.IsNullOrWhiteSpace(Email) ? "(unnamed)" : Email)
            : $"{FirstName} {LastName}".Trim();

    public string Initials
    {
        get
        {
            char a = !string.IsNullOrWhiteSpace(FirstName) ? char.ToUpper(FirstName[0]) : ' ';
            char b = !string.IsNullOrWhiteSpace(LastName)  ? char.ToUpper(LastName[0])  : ' ';
            var s = $"{a}{b}".Trim();
            return string.IsNullOrEmpty(s) ? "?" : s;
        }
    }

    public IEnumerable<string> TagList =>
        string.IsNullOrWhiteSpace(Tags)
            ? Array.Empty<string>()
            : Tags.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0);
}

public sealed class ContactList
{
    public Contact[] Items { get; set; } = Array.Empty<Contact>();
    public int Total { get; set; }
    public long Cursor { get; set; }
}

public sealed class PresenceEntry
{
    public string Principal { get; set; } = "";
    public string Name { get; set; } = "";
    public long LastSeenMs { get; set; }
}

public sealed class PresenceList
{
    public PresenceEntry[] Viewers { get; set; } = Array.Empty<PresenceEntry>();
}

public sealed class Company
{
    public long   Id { get; set; }
    public string Name { get; set; } = "";
    public string Industry { get; set; } = "";
    public string Website { get; set; } = "";
    public string Size { get; set; } = "";
    public string Notes { get; set; } = "";
    public long CreatedAtMs { get; set; }
    public long UpdatedAtMs { get; set; }
}

public sealed class CompanyList { public Company[] Items { get; set; } = Array.Empty<Company>(); public int Total { get; set; } }

public sealed class Deal
{
    public long   Id { get; set; }
    public string Title { get; set; } = "";
    public long   ContactId { get; set; }
    public long   CompanyId { get; set; }
    public long   ValueCents { get; set; }
    public int    Stage { get; set; }
    public string StageName { get; set; } = "";
    public int    Probability { get; set; }
    public long   ExpectedCloseAtMs { get; set; }
    public long   StageChangedAtMs { get; set; }
    public long   CreatedAtMs { get; set; }
    public long   UpdatedAtMs { get; set; }
    public string ValueDisplay => $"${ValueCents / 100m:N0}";
}

public sealed class DealList { public Deal[] Items { get; set; } = Array.Empty<Deal>(); public int Total { get; set; } }

public sealed class Activity
{
    public long   Id { get; set; }
    public int    Type { get; set; }
    public string TypeName { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public long   ContactId { get; set; }
    public long   DealId { get; set; }
    public string CreatedBy { get; set; } = "";
    public long   AtMs { get; set; }
}

public sealed class ActivityList { public Activity[] Items { get; set; } = Array.Empty<Activity>(); public int Total { get; set; } }

public sealed class TaskItem
{
    public long   Id { get; set; }
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    public long   ContactId { get; set; }
    public long   DealId { get; set; }
    public long   DueAtMs { get; set; }
    public bool   Done { get; set; }
    public int    Priority { get; set; }
    public string PriorityName { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public long   CreatedAtMs { get; set; }
    public long   UpdatedAtMs { get; set; }
}

public sealed class TaskList { public TaskItem[] Items { get; set; } = Array.Empty<TaskItem>(); public int Total { get; set; } }

public sealed class DashboardStage
{
    public string Name { get; set; } = "";
    public int    Count { get; set; }
    public long   ValueCents { get; set; }
    public int    Probability { get; set; }
}

public sealed class HotLead
{
    public long   Id { get; set; }
    public string Name { get; set; } = "";
    public int    Score { get; set; }
}

public sealed class DashboardData
{
    public DashboardStage[] Stages { get; set; } = Array.Empty<DashboardStage>();
    public long WeightedCents { get; set; }
    public long WonThisMonthCents { get; set; }
    public int  OverdueTasks { get; set; }
    public int  TodayTasks { get; set; }
    public int  ContactCount { get; set; }
    public HotLead[] HotLeads { get; set; } = Array.Empty<HotLead>();
}

public sealed class LeadScore { public long ContactId { get; set; } public int Score { get; set; } }

public sealed class SearchHit { public long Id { get; set; } public string Label { get; set; } = ""; public string Sub { get; set; } = ""; }
public sealed class SearchResults
{
    public SearchHit[] Contacts { get; set; } = Array.Empty<SearchHit>();
    public SearchHit[] Companies { get; set; } = Array.Empty<SearchHit>();
    public SearchHit[] Deals { get; set; } = Array.Empty<SearchHit>();
}

[JsonSerializable(typeof(Contact))]
[JsonSerializable(typeof(Contact[]))]
[JsonSerializable(typeof(ContactList))]
[JsonSerializable(typeof(PresenceEntry))]
[JsonSerializable(typeof(PresenceList))]
[JsonSerializable(typeof(Company))]
[JsonSerializable(typeof(CompanyList))]
[JsonSerializable(typeof(Deal))]
[JsonSerializable(typeof(DealList))]
[JsonSerializable(typeof(Activity))]
[JsonSerializable(typeof(ActivityList))]
[JsonSerializable(typeof(TaskItem))]
[JsonSerializable(typeof(TaskList))]
[JsonSerializable(typeof(DashboardData))]
[JsonSerializable(typeof(LeadScore))]
[JsonSerializable(typeof(SearchResults))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class CrmJsonContext : JsonSerializerContext { }
