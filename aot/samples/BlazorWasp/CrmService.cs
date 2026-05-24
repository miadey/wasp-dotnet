using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// CRM persistence layer — Contacts, Companies, Deals, Activities,
/// Tasks. Each entity gets its own stable-memory section so any one
/// can grow without affecting others; all five use the same magic +
/// version + count + records layout.
///
/// Stable-memory offsets (no collision with existing data):
///   3_000_000  Contacts    "CRMC"
///   3_500_000  Companies   "CRMO"
///   4_000_000  Deals       "CRMD"
///   4_500_000  Activities  "CRMA"
///   5_000_000  Tasks       "CRMT"
///
/// Pattern modelled on TetrisService / ChatService.
/// </summary>
public sealed unsafe class CrmService
{
    private const ulong ContactsOffset   = 3_000_000;
    private const ulong CompaniesOffset  = 3_500_000;
    private const ulong DealsOffset      = 4_000_000;
    private const ulong ActivitiesOffset = 4_500_000;
    private const ulong TasksOffset      = 5_000_000;
    private const uint  ContactsMagic    = 0x43524D43;   // "CRMC"
    private const uint  CompaniesMagic   = 0x43524D4F;   // "CRMO"
    private const uint  DealsMagic       = 0x43524D44;   // "CRMD"
    private const uint  ActivitiesMagic  = 0x43524D41;   // "CRMA"
    private const uint  TasksMagic       = 0x43524D54;   // "CRMT"
    private const uint  Version          = 1;

    public sealed record Contact(
        long   Id,
        string FirstName, string LastName,
        string Email,     string Phone,
        string Company,   string Title,
        string Notes,     string Tags,
        long   CreatedAtMs, long UpdatedAtMs);

    public sealed record Company(
        long Id, string Name, string Industry, string Website,
        string Size, string Notes,
        long CreatedAtMs, long UpdatedAtMs);

    /// <summary>Pipedrive-style 6-stage pipeline.</summary>
    public enum DealStage { New, Qualified, Proposal, Negotiation, Won, Lost }
    public static readonly string[] StageNames = { "New", "Qualified", "Proposal", "Negotiation", "Won", "Lost" };
    /// <summary>Default close probability per stage (Pipedrive convention).</summary>
    public static readonly int[] StageProbability = { 10, 25, 50, 75, 100, 0 };
    /// <summary>Days a deal can sit in a stage before it's "rotting".</summary>
    public static readonly int[] StageRotDays = { 14, 21, 30, 30, 0, 0 };

    public sealed record Deal(
        long   Id,
        string Title,
        long   ContactId,   // 0 = none
        long   CompanyId,   // 0 = none
        long   ValueCents,
        int    Stage,
        long   ExpectedCloseAtMs,
        long   StageChangedAtMs,
        long   CreatedAtMs, long UpdatedAtMs);

    public enum ActivityType { Note, Call, Email, Meeting }
    public static readonly string[] ActivityTypeNames = { "Note", "Call", "Email", "Meeting" };

    public sealed record Activity(
        long   Id,
        int    Type,
        string Subject,
        string Body,
        long   ContactId,   // 0 = none
        long   DealId,      // 0 = none
        string CreatedBy,
        long   AtMs);

    public enum TaskPriority { Low, Medium, High }
    public static readonly string[] TaskPriorityNames = { "Low", "Medium", "High" };

    public sealed record TaskItem(
        long   Id,
        string Title,
        string Notes,
        long   ContactId,
        long   DealId,
        long   DueAtMs,
        bool   Done,
        int    Priority,
        string CreatedBy,
        long   CreatedAtMs, long UpdatedAtMs);

    private List<Contact>?   _contacts;
    private List<Company>?   _companies;
    private List<Deal>?      _deals;
    private List<Activity>?  _activities;
    private List<TaskItem>?  _tasks;
    private long _nextId = 1;
    private long _nextCompanyId = 1;
    private long _nextDealId = 1;
    private long _nextActivityId = 1;
    private long _nextTaskId = 1;

    // ─── Public API ──────────────────────────────────────────────
    public IReadOnlyList<Contact> All()
    {
        EnsureLoaded();
        return _contacts!;
    }

    public IReadOnlyList<Contact> Search(string? q)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(q)) return _contacts!;
        var needle = q.Trim().ToLowerInvariant();
        return _contacts!.Where(c =>
            c.FirstName.ToLowerInvariant().Contains(needle) ||
            c.LastName.ToLowerInvariant().Contains(needle) ||
            c.Email.ToLowerInvariant().Contains(needle) ||
            c.Company.ToLowerInvariant().Contains(needle) ||
            c.Title.ToLowerInvariant().Contains(needle) ||
            c.Tags.ToLowerInvariant().Contains(needle)
        ).ToList();
    }

    public Contact? Find(long id)
    {
        EnsureLoaded();
        return _contacts!.FirstOrDefault(c => c.Id == id);
    }

    public Contact Create(Contact draft)
    {
        EnsureLoaded();
        var now = NowMs();
        var c = draft with
        {
            Id = _nextId++,
            FirstName = San(draft.FirstName),
            LastName  = San(draft.LastName),
            Email     = San(draft.Email),
            Phone     = San(draft.Phone),
            Company   = San(draft.Company),
            Title     = San(draft.Title),
            Notes     = San(draft.Notes, allowMultiline: true),
            Tags      = San(draft.Tags),
            CreatedAtMs = now,
            UpdatedAtMs = now,
        };
        _contacts!.Add(c);
        Persist();
        return c;
    }

    public Contact? Update(long id, Contact draft)
    {
        EnsureLoaded();
        var i = _contacts!.FindIndex(c => c.Id == id);
        if (i < 0) return null;
        var existing = _contacts[i];
        var updated = existing with
        {
            FirstName = San(draft.FirstName),
            LastName  = San(draft.LastName),
            Email     = San(draft.Email),
            Phone     = San(draft.Phone),
            Company   = San(draft.Company),
            Title     = San(draft.Title),
            Notes     = San(draft.Notes, allowMultiline: true),
            Tags      = San(draft.Tags),
            UpdatedAtMs = NowMs(),
        };
        _contacts[i] = updated;
        Persist();
        return updated;
    }

    public bool Delete(long id)
    {
        EnsureLoaded();
        var n = _contacts!.RemoveAll(c => c.Id == id);
        if (n > 0) Persist();
        return n > 0;
    }

    // ─── Storage ─────────────────────────────────────────────────
    private void EnsureLoaded()
    {
        if (_contacts is not null) return;
        EnsurePages(ContactsOffset + 16);
        var hdr = new byte[20];
        ReadBytes(ContactsOffset, hdr);
        var magic = BitConverter.ToUInt32(hdr, 0);
        var ver   = BitConverter.ToUInt32(hdr, 4);
        if (magic != ContactsMagic || ver != Version)
        {
            _contacts = new List<Contact>();
            _nextId = 1;
            Persist();
            return;
        }
        _nextId = BitConverter.ToInt64(hdr, 8);
        var count = (int)BitConverter.ToUInt32(hdr, 16);
        var list = new List<Contact>(count);
        var r = new MemoryReader(ContactsOffset + 20);
        for (int i = 0; i < count; i++)
        {
            var id = r.ReadI64();
            var ca = r.ReadI64();
            var ua = r.ReadI64();
            var fn = r.ReadString();
            var ln = r.ReadString();
            var em = r.ReadString();
            var ph = r.ReadString();
            var co = r.ReadString();
            var ti = r.ReadString();
            var no = r.ReadString();
            var ta = r.ReadString();
            list.Add(new Contact(id, fn, ln, em, ph, co, ti, no, ta, ca, ua));
        }
        _contacts = list;
    }

    private void Persist()
    {
        var w = new MemoryWriter();
        w.WriteU32(ContactsMagic);
        w.WriteU32(Version);
        w.WriteI64(_nextId);
        w.WriteU32((uint)_contacts!.Count);
        foreach (var c in _contacts)
        {
            w.WriteI64(c.Id);
            w.WriteI64(c.CreatedAtMs);
            w.WriteI64(c.UpdatedAtMs);
            w.WriteString(c.FirstName);
            w.WriteString(c.LastName);
            w.WriteString(c.Email);
            w.WriteString(c.Phone);
            w.WriteString(c.Company);
            w.WriteString(c.Title);
            w.WriteString(c.Notes);
            w.WriteString(c.Tags);
        }
        WriteBytes(ContactsOffset, w.ToArray());
    }

    // ─── Companies storage ──────────────────────────────────────
    private void EnsureCompaniesLoaded()
    {
        if (_companies is not null) return;
        EnsurePages(CompaniesOffset + 20);
        var hdr = new byte[20];
        ReadBytes(CompaniesOffset, hdr);
        var magic = BitConverter.ToUInt32(hdr, 0);
        var ver   = BitConverter.ToUInt32(hdr, 4);
        if (magic != CompaniesMagic || ver != Version)
        {
            _companies = new List<Company>();
            _nextCompanyId = 1;
            PersistCompanies();
            return;
        }
        _nextCompanyId = BitConverter.ToInt64(hdr, 8);
        var count = (int)BitConverter.ToUInt32(hdr, 16);
        var list = new List<Company>(count);
        var r = new MemoryReader(CompaniesOffset + 20);
        for (int i = 0; i < count; i++)
        {
            var id = r.ReadI64();
            var ca = r.ReadI64();
            var ua = r.ReadI64();
            var nm = r.ReadString();
            var ind = r.ReadString();
            var web = r.ReadString();
            var sz = r.ReadString();
            var no = r.ReadString();
            list.Add(new Company(id, nm, ind, web, sz, no, ca, ua));
        }
        _companies = list;
    }

    private void PersistCompanies()
    {
        var w = new MemoryWriter();
        w.WriteU32(CompaniesMagic);
        w.WriteU32(Version);
        w.WriteI64(_nextCompanyId);
        w.WriteU32((uint)_companies!.Count);
        foreach (var c in _companies)
        {
            w.WriteI64(c.Id);
            w.WriteI64(c.CreatedAtMs);
            w.WriteI64(c.UpdatedAtMs);
            w.WriteString(c.Name);
            w.WriteString(c.Industry);
            w.WriteString(c.Website);
            w.WriteString(c.Size);
            w.WriteString(c.Notes);
        }
        WriteBytes(CompaniesOffset, w.ToArray());
    }

    // ─── Deals storage ──────────────────────────────────────────
    private void EnsureDealsLoaded()
    {
        if (_deals is not null) return;
        EnsurePages(DealsOffset + 20);
        var hdr = new byte[20];
        ReadBytes(DealsOffset, hdr);
        var magic = BitConverter.ToUInt32(hdr, 0);
        var ver   = BitConverter.ToUInt32(hdr, 4);
        if (magic != DealsMagic || ver != Version)
        {
            _deals = new List<Deal>();
            _nextDealId = 1;
            PersistDeals();
            return;
        }
        _nextDealId = BitConverter.ToInt64(hdr, 8);
        var count = (int)BitConverter.ToUInt32(hdr, 16);
        var list = new List<Deal>(count);
        var r = new MemoryReader(DealsOffset + 20);
        for (int i = 0; i < count; i++)
        {
            var id = r.ReadI64();
            var ca = r.ReadI64();
            var ua = r.ReadI64();
            var sc = r.ReadI64();
            var ec = r.ReadI64();
            var val = r.ReadI64();
            var stg = r.ReadI32();
            var cid = r.ReadI64();
            var coid = r.ReadI64();
            var title = r.ReadString();
            list.Add(new Deal(id, title, cid, coid, val, stg, ec, sc, ca, ua));
        }
        _deals = list;
    }

    private void PersistDeals()
    {
        var w = new MemoryWriter();
        w.WriteU32(DealsMagic);
        w.WriteU32(Version);
        w.WriteI64(_nextDealId);
        w.WriteU32((uint)_deals!.Count);
        foreach (var d in _deals)
        {
            w.WriteI64(d.Id);
            w.WriteI64(d.CreatedAtMs);
            w.WriteI64(d.UpdatedAtMs);
            w.WriteI64(d.StageChangedAtMs);
            w.WriteI64(d.ExpectedCloseAtMs);
            w.WriteI64(d.ValueCents);
            w.WriteI32(d.Stage);
            w.WriteI64(d.ContactId);
            w.WriteI64(d.CompanyId);
            w.WriteString(d.Title);
        }
        WriteBytes(DealsOffset, w.ToArray());
    }

    // ─── Activities storage ─────────────────────────────────────
    private void EnsureActivitiesLoaded()
    {
        if (_activities is not null) return;
        EnsurePages(ActivitiesOffset + 20);
        var hdr = new byte[20];
        ReadBytes(ActivitiesOffset, hdr);
        var magic = BitConverter.ToUInt32(hdr, 0);
        var ver   = BitConverter.ToUInt32(hdr, 4);
        if (magic != ActivitiesMagic || ver != Version)
        {
            _activities = new List<Activity>();
            _nextActivityId = 1;
            PersistActivities();
            return;
        }
        _nextActivityId = BitConverter.ToInt64(hdr, 8);
        var count = (int)BitConverter.ToUInt32(hdr, 16);
        var list = new List<Activity>(count);
        var r = new MemoryReader(ActivitiesOffset + 20);
        for (int i = 0; i < count; i++)
        {
            var id = r.ReadI64();
            var at = r.ReadI64();
            var type = r.ReadI32();
            var cid = r.ReadI64();
            var did = r.ReadI64();
            var subj = r.ReadString();
            var body = r.ReadString();
            var by = r.ReadString();
            list.Add(new Activity(id, type, subj, body, cid, did, by, at));
        }
        _activities = list;
    }

    private void PersistActivities()
    {
        var w = new MemoryWriter();
        w.WriteU32(ActivitiesMagic);
        w.WriteU32(Version);
        w.WriteI64(_nextActivityId);
        w.WriteU32((uint)_activities!.Count);
        foreach (var a in _activities)
        {
            w.WriteI64(a.Id);
            w.WriteI64(a.AtMs);
            w.WriteI32(a.Type);
            w.WriteI64(a.ContactId);
            w.WriteI64(a.DealId);
            w.WriteString(a.Subject);
            w.WriteString(a.Body);
            w.WriteString(a.CreatedBy);
        }
        WriteBytes(ActivitiesOffset, w.ToArray());
    }

    // ─── Tasks storage ──────────────────────────────────────────
    private void EnsureTasksLoaded()
    {
        if (_tasks is not null) return;
        EnsurePages(TasksOffset + 20);
        var hdr = new byte[20];
        ReadBytes(TasksOffset, hdr);
        var magic = BitConverter.ToUInt32(hdr, 0);
        var ver   = BitConverter.ToUInt32(hdr, 4);
        if (magic != TasksMagic || ver != Version)
        {
            _tasks = new List<TaskItem>();
            _nextTaskId = 1;
            PersistTasks();
            return;
        }
        _nextTaskId = BitConverter.ToInt64(hdr, 8);
        var count = (int)BitConverter.ToUInt32(hdr, 16);
        var list = new List<TaskItem>(count);
        var r = new MemoryReader(TasksOffset + 20);
        for (int i = 0; i < count; i++)
        {
            var id = r.ReadI64();
            var ca = r.ReadI64();
            var ua = r.ReadI64();
            var due = r.ReadI64();
            var cid = r.ReadI64();
            var did = r.ReadI64();
            var prio = r.ReadI32();
            var done = r.ReadI32() != 0;
            var title = r.ReadString();
            var notes = r.ReadString();
            var by = r.ReadString();
            list.Add(new TaskItem(id, title, notes, cid, did, due, done, prio, by, ca, ua));
        }
        _tasks = list;
    }

    private void PersistTasks()
    {
        var w = new MemoryWriter();
        w.WriteU32(TasksMagic);
        w.WriteU32(Version);
        w.WriteI64(_nextTaskId);
        w.WriteU32((uint)_tasks!.Count);
        foreach (var t in _tasks)
        {
            w.WriteI64(t.Id);
            w.WriteI64(t.CreatedAtMs);
            w.WriteI64(t.UpdatedAtMs);
            w.WriteI64(t.DueAtMs);
            w.WriteI64(t.ContactId);
            w.WriteI64(t.DealId);
            w.WriteI32(t.Priority);
            w.WriteI32(t.Done ? 1 : 0);
            w.WriteString(t.Title);
            w.WriteString(t.Notes);
            w.WriteString(t.CreatedBy);
        }
        WriteBytes(TasksOffset, w.ToArray());
    }

    // ─── Companies ───────────────────────────────────────────────
    public IReadOnlyList<Company> AllCompanies() { EnsureCompaniesLoaded(); return _companies!; }
    public Company? FindCompany(long id) { EnsureCompaniesLoaded(); return _companies!.FirstOrDefault(c => c.Id == id); }
    public Company CreateCompany(Company d)
    {
        EnsureCompaniesLoaded();
        var now = NowMs();
        var c = d with
        {
            Id = _nextCompanyId++,
            Name = San(d.Name), Industry = San(d.Industry),
            Website = San(d.Website), Size = San(d.Size),
            Notes = San(d.Notes, true),
            CreatedAtMs = now, UpdatedAtMs = now,
        };
        _companies!.Add(c);
        PersistCompanies();
        return c;
    }
    public Company? UpdateCompany(long id, Company d)
    {
        EnsureCompaniesLoaded();
        var i = _companies!.FindIndex(c => c.Id == id);
        if (i < 0) return null;
        var updated = _companies[i] with
        {
            Name = San(d.Name), Industry = San(d.Industry),
            Website = San(d.Website), Size = San(d.Size),
            Notes = San(d.Notes, true),
            UpdatedAtMs = NowMs(),
        };
        _companies[i] = updated;
        PersistCompanies();
        return updated;
    }
    public bool DeleteCompany(long id)
    {
        EnsureCompaniesLoaded();
        var n = _companies!.RemoveAll(c => c.Id == id);
        if (n > 0) PersistCompanies();
        return n > 0;
    }

    // ─── Deals ───────────────────────────────────────────────────
    public IReadOnlyList<Deal> AllDeals() { EnsureDealsLoaded(); return _deals!; }
    public Deal? FindDeal(long id) { EnsureDealsLoaded(); return _deals!.FirstOrDefault(d => d.Id == id); }
    public Deal CreateDeal(Deal d)
    {
        EnsureDealsLoaded();
        var now = NowMs();
        var stage = Math.Max(0, Math.Min(5, d.Stage));
        var deal = d with
        {
            Id = _nextDealId++,
            Title = San(d.Title),
            Stage = stage,
            StageChangedAtMs = now,
            CreatedAtMs = now, UpdatedAtMs = now,
        };
        _deals!.Add(deal);
        PersistDeals();
        return deal;
    }
    public Deal? UpdateDeal(long id, Deal d)
    {
        EnsureDealsLoaded();
        var i = _deals!.FindIndex(x => x.Id == id);
        if (i < 0) return null;
        var existing = _deals[i];
        var stage = Math.Max(0, Math.Min(5, d.Stage));
        var stageChanged = stage != existing.Stage ? NowMs() : existing.StageChangedAtMs;
        var updated = existing with
        {
            Title = San(d.Title),
            ContactId = d.ContactId,
            CompanyId = d.CompanyId,
            ValueCents = d.ValueCents,
            Stage = stage,
            ExpectedCloseAtMs = d.ExpectedCloseAtMs,
            StageChangedAtMs = stageChanged,
            UpdatedAtMs = NowMs(),
        };
        _deals[i] = updated;
        PersistDeals();
        return updated;
    }
    public bool DeleteDeal(long id)
    {
        EnsureDealsLoaded();
        var n = _deals!.RemoveAll(d => d.Id == id);
        if (n > 0) PersistDeals();
        return n > 0;
    }

    // ─── Activities ──────────────────────────────────────────────
    public IReadOnlyList<Activity> AllActivities() { EnsureActivitiesLoaded(); return _activities!; }
    public IReadOnlyList<Activity> ActivitiesFor(long contactId, long dealId)
    {
        EnsureActivitiesLoaded();
        return _activities!.Where(a =>
            (contactId == 0 || a.ContactId == contactId) &&
            (dealId == 0 || a.DealId == dealId)).OrderByDescending(a => a.AtMs).ToList();
    }
    public Activity CreateActivity(Activity a)
    {
        EnsureActivitiesLoaded();
        var t = Math.Max(0, Math.Min(3, a.Type));
        var act = a with
        {
            Id = _nextActivityId++,
            Type = t,
            Subject = San(a.Subject),
            Body = San(a.Body, true),
            CreatedBy = San(a.CreatedBy),
            AtMs = a.AtMs == 0 ? NowMs() : a.AtMs,
        };
        _activities!.Add(act);
        PersistActivities();
        return act;
    }
    public bool DeleteActivity(long id)
    {
        EnsureActivitiesLoaded();
        var n = _activities!.RemoveAll(a => a.Id == id);
        if (n > 0) PersistActivities();
        return n > 0;
    }

    // ─── Tasks ───────────────────────────────────────────────────
    public IReadOnlyList<TaskItem> AllTasks() { EnsureTasksLoaded(); return _tasks!; }
    public TaskItem CreateTask(TaskItem t)
    {
        EnsureTasksLoaded();
        var p = Math.Max(0, Math.Min(2, t.Priority));
        var now = NowMs();
        var task = t with
        {
            Id = _nextTaskId++,
            Title = San(t.Title),
            Notes = San(t.Notes, true),
            Priority = p,
            CreatedBy = San(t.CreatedBy),
            CreatedAtMs = now, UpdatedAtMs = now,
        };
        _tasks!.Add(task);
        PersistTasks();
        return task;
    }
    public TaskItem? UpdateTask(long id, TaskItem t)
    {
        EnsureTasksLoaded();
        var i = _tasks!.FindIndex(x => x.Id == id);
        if (i < 0) return null;
        var p = Math.Max(0, Math.Min(2, t.Priority));
        var updated = _tasks[i] with
        {
            Title = San(t.Title),
            Notes = San(t.Notes, true),
            ContactId = t.ContactId,
            DealId = t.DealId,
            DueAtMs = t.DueAtMs,
            Done = t.Done,
            Priority = p,
            UpdatedAtMs = NowMs(),
        };
        _tasks[i] = updated;
        PersistTasks();
        return updated;
    }
    public bool DeleteTask(long id)
    {
        EnsureTasksLoaded();
        var n = _tasks!.RemoveAll(t => t.Id == id);
        if (n > 0) PersistTasks();
        return n > 0;
    }

    // ─── Lead-score (HubSpot-lite) ──────────────────────────────
    /// <summary>
    /// Score = recent-90-day activity count × recency weight. Higher
    /// = hotter lead. Computed on-the-fly; no extra schema.
    /// </summary>
    public int LeadScore(long contactId)
    {
        EnsureActivitiesLoaded();
        var now = NowMs();
        var ninetyDays = 90L * 24 * 3600 * 1000;
        int recent = 0;
        long lastTouchedMs = 0;
        foreach (var a in _activities!)
        {
            if (a.ContactId != contactId) continue;
            if (now - a.AtMs <= ninetyDays) recent++;
            if (a.AtMs > lastTouchedMs) lastTouchedMs = a.AtMs;
        }
        if (lastTouchedMs == 0) return 0;
        var daysSince = Math.Max(1, (now - lastTouchedMs) / (24L * 3600 * 1000));
        // Recency-weighted score, decays like 1/sqrt(days).
        return (int)(recent * 100.0 / Math.Sqrt(daysSince));
    }

    // ─── helpers ─────────────────────────────────────────────────
    private static long NowMs() => (long)(Ic0.time() / 1_000_000UL);

    private static string San(string? raw, bool allowMultiline = false)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch == '\n' && allowMultiline) { sb.Append(ch); continue; }
            if (ch < 0x20) continue;
            sb.Append(ch);
        }
        var s = sb.ToString().Trim();
        return s.Length > 4000 ? s.Substring(0, 4000) : s;
    }

    private static void EnsurePages(ulong needed)
    {
        ulong have = Ic0.stable64_size() * 65536UL;
        if (needed > have) Ic0.stable64_grow(((needed - have) + 65535UL) / 65536UL);
    }
    private static void ReadBytes(ulong offset, byte[] buf)
    { fixed (byte* p = buf) Ic0.stable64_read((ulong)(nint)p, offset, (ulong)buf.Length); }
    private static void WriteBytes(ulong offset, byte[] buf)
    {
        EnsurePages(offset + (ulong)buf.Length);
        fixed (byte* p = buf) Ic0.stable64_write(offset, (ulong)(nint)p, (ulong)buf.Length);
    }

    private sealed class MemoryWriter
    {
        private readonly List<byte> _b = new();
        public byte[] ToArray() => _b.ToArray();
        public void WriteU32(uint v) => _b.AddRange(BitConverter.GetBytes(v));
        public void WriteI32(int v)  => _b.AddRange(BitConverter.GetBytes(v));
        public void WriteI64(long v) => _b.AddRange(BitConverter.GetBytes(v));
        public void WriteString(string s)
        {
            var u = Encoding.UTF8.GetBytes(s ?? "");
            _b.AddRange(BitConverter.GetBytes(u.Length));
            _b.AddRange(u);
        }
    }

    private sealed class MemoryReader
    {
        public ulong Cursor;
        public MemoryReader(ulong off) { Cursor = off; }
        public uint  ReadU32() { var b = new byte[4]; ReadBytes(Cursor, b); Cursor += 4; return BitConverter.ToUInt32(b, 0); }
        public int   ReadI32() { var b = new byte[4]; ReadBytes(Cursor, b); Cursor += 4; return BitConverter.ToInt32(b, 0); }
        public long  ReadI64() { var b = new byte[8]; ReadBytes(Cursor, b); Cursor += 8; return BitConverter.ToInt64(b, 0); }
        public string ReadString()
        {
            var len = ReadI32();
            if (len <= 0) return "";
            var b = new byte[len];
            ReadBytes(Cursor, b);
            Cursor += (ulong)len;
            return Encoding.UTF8.GetString(b);
        }
    }
}
