using Recall.Storage;

namespace Recall.Tests;

public class CalendarTests : IDisposable
{
    private readonly DiaryDatabase _db;
    private readonly string _dbPath;

    // Secrets: hash with SHA256 to match ResolveAccess
    private const string GuardianSecret = "test-guardian";
    private const string CodingSecret = "test-coding";
    private const string ScopeSecret = "test-scope-alpha";

    private readonly string _guardianHash;
    private readonly string _codingHash;
    private readonly ScopeEntry _scopeAlpha;

    public CalendarTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"recall-test-{Guid.NewGuid():N}.db");
        _db = new DiaryDatabase(_dbPath);

        // Pre-compute hashes (same algo as DiaryDatabase.HashKey)
        _guardianHash = HashSecret(GuardianSecret);
        _codingHash = HashSecret(CodingSecret);
        _scopeAlpha = new ScopeEntry { Name = "alpha", SecretHash = HashSecret(ScopeSecret) };
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        // WAL/SHM cleanup
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
    }

    private static string HashSecret(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    private (AccessLevel, string?) Resolve(string secret) =>
        _db.ResolveAccess(secret, _guardianHash, _codingHash, [_scopeAlpha]);

    // ── Schema ─────────────────────────────────────────────────

    [Fact]
    public void Migration_creates_calendar_table()
    {
        // If we got here without exception, table exists.
        // Verify by inserting and reading back.
        _db.UpsertCalendarPlans("2026-01-15", "Test plan", null, false);
        var day = _db.GetCalendarDay("2026-01-15", AccessLevel.Guardian, null);
        Assert.Single(day);
        Assert.Equal("Test plan", day[0].Plans);
    }

    // ── Upsert Plans ───────────────────────────────────────────

    [Fact]
    public void UpsertPlans_creates_and_updates()
    {
        _db.UpsertCalendarPlans("2026-04-01", "Original plan", null, false);
        var day = _db.GetCalendarDay("2026-04-01", AccessLevel.Guardian, null);
        Assert.Single(day);
        Assert.Equal("Original plan", day[0].Plans);

        // Update
        _db.UpsertCalendarPlans("2026-04-01", "Updated plan", null, false);
        day = _db.GetCalendarDay("2026-04-01", AccessLevel.Guardian, null);
        Assert.Single(day);
        Assert.Equal("Updated plan", day[0].Plans);
    }

    [Fact]
    public void UpsertPlans_restricted_and_unrestricted_coexist()
    {
        _db.UpsertCalendarPlans("2026-04-01", "Public plan", null, false);
        _db.UpsertCalendarPlans("2026-04-01", "Private plan", null, true);

        var day = _db.GetCalendarDay("2026-04-01", AccessLevel.Guardian, null);
        Assert.Equal(2, day.Count);
        Assert.Contains(day, c => c.Plans == "Public plan" && !c.Restricted);
        Assert.Contains(day, c => c.Plans == "Private plan" && c.Restricted);
    }

    // ── Upsert Summary ─────────────────────────────────────────

    [Fact]
    public void UpsertSummary_creates_and_updates()
    {
        _db.UpsertCalendarSummary("2026-04-01", "First summary", null, false);
        var day = _db.GetCalendarDay("2026-04-01", AccessLevel.Guardian, null);
        Assert.Single(day);
        Assert.Equal("First summary", day[0].Summary);

        _db.UpsertCalendarSummary("2026-04-01", "Revised summary", null, false);
        day = _db.GetCalendarDay("2026-04-01", AccessLevel.Guardian, null);
        Assert.Single(day);
        Assert.Equal("Revised summary", day[0].Summary);
    }

    [Fact]
    public void Plans_and_summary_share_same_row()
    {
        _db.UpsertCalendarPlans("2026-04-02", "The plan", null, false);
        _db.UpsertCalendarSummary("2026-04-02", "The summary", null, false);

        var day = _db.GetCalendarDay("2026-04-02", AccessLevel.Guardian, null);
        Assert.Single(day);
        Assert.Equal("The plan", day[0].Plans);
        Assert.Equal("The summary", day[0].Summary);
    }

    // ── Access Control ─────────────────────────────────────────

    [Fact]
    public void Coding_cannot_see_restricted_calendar()
    {
        _db.UpsertCalendarPlans("2026-04-03", "Public plan", null, false);
        _db.UpsertCalendarPlans("2026-04-03", "Restricted plan", null, true);

        var codingView = _db.GetCalendarDay("2026-04-03", AccessLevel.Coding, null);
        Assert.Single(codingView);
        Assert.Equal("Public plan", codingView[0].Plans);
        Assert.False(codingView[0].Restricted);
    }

    [Fact]
    public void Guardian_sees_all_scopes()
    {
        _db.UpsertCalendarPlans("2026-04-04", "Global plan", null, false);
        _db.UpsertCalendarPlans("2026-04-04", "Alpha plan", "alpha", false);

        var guardianView = _db.GetCalendarDay("2026-04-04", AccessLevel.Guardian, null);
        Assert.Equal(2, guardianView.Count);
    }

    [Fact]
    public void Coding_cannot_see_scoped_calendar()
    {
        _db.UpsertCalendarPlans("2026-04-04", "Global plan", null, false);
        _db.UpsertCalendarPlans("2026-04-04", "Alpha plan", "alpha", false);

        var codingView = _db.GetCalendarDay("2026-04-04", AccessLevel.Coding, null);
        Assert.Single(codingView);
        Assert.Null(codingView[0].Scope);
    }

    [Fact]
    public void Scoped_sees_only_own_scope()
    {
        _db.UpsertCalendarPlans("2026-04-04", "Global plan", null, false);
        _db.UpsertCalendarPlans("2026-04-04", "Alpha plan", "alpha", false);
        _db.UpsertCalendarPlans("2026-04-04", "Beta plan", "beta", false);

        var alphaView = _db.GetCalendarDay("2026-04-04", AccessLevel.Scoped, "alpha");
        Assert.Single(alphaView);
        Assert.Equal("Alpha plan", alphaView[0].Plans);
    }

    [Fact]
    public void None_access_sees_nothing()
    {
        _db.UpsertCalendarPlans("2026-04-05", "Some plan", null, false);
        var noneView = _db.GetCalendarDay("2026-04-05", AccessLevel.None, null);
        Assert.Empty(noneView);
    }

    // ── Date Range ─────────────────────────────────────────────

    [Fact]
    public void GetCalendarRange_filters_by_date_and_access()
    {
        _db.UpsertCalendarPlans("2026-04-01", "Day 1", null, false);
        _db.UpsertCalendarPlans("2026-04-02", "Day 2", null, false);
        _db.UpsertCalendarPlans("2026-04-03", "Day 3", null, false);
        _db.UpsertCalendarPlans("2026-04-03", "Day 3 restricted", null, true);
        _db.UpsertCalendarPlans("2026-04-04", "Day 4", null, false);

        // Guardian range
        var guardianRange = _db.GetCalendarRange("2026-04-02", "2026-04-03", AccessLevel.Guardian, null);
        Assert.Equal(3, guardianRange.Count); // Day 2 + Day 3 + Day 3 restricted

        // Coding range — no restricted
        var codingRange = _db.GetCalendarRange("2026-04-02", "2026-04-03", AccessLevel.Coding, null);
        Assert.Equal(2, codingRange.Count);
    }

    // ── Entry Linking by Date ──────────────────────────────────

    [Fact]
    public void GetEntriesByDate_links_entries_by_created_at()
    {
        // Write entries — they get UTC timestamps
        var id1 = _db.WriteEntry("Entry on day X", "test", scope: null);
        var id2 = _db.WriteEntry("Another entry on day X", "test", scope: null);

        // The entries were just created, so their date is today
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        var entries = _db.GetEntriesByDate(today, AccessLevel.Guardian, null);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Id == id1);
        Assert.Contains(entries, e => e.Id == id2);
    }

    [Fact]
    public void GetEntriesByDate_respects_access_control()
    {
        _db.WriteEntry("Public entry", scope: null, restricted: false);
        _db.WriteEntry("Restricted entry", scope: null, restricted: true);
        _db.WriteEntry("Scoped entry", scope: "alpha");

        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

        // Guardian sees unscoped (global) entries
        var guardianEntries = _db.GetEntriesByDate(today, AccessLevel.Guardian, null);
        Assert.Equal(2, guardianEntries.Count); // public + restricted (scope IS NULL)

        // Coding sees only unrestricted, unscoped
        var codingEntries = _db.GetEntriesByDate(today, AccessLevel.Coding, null);
        Assert.Single(codingEntries);
        Assert.Contains("Public entry", codingEntries[0].Content);

        // Scoped sees only their scope
        var scopedEntries = _db.GetEntriesByDate(today, AccessLevel.Scoped, "alpha");
        Assert.Single(scopedEntries);
        Assert.Contains("Scoped entry", scopedEntries[0].Content);
    }

    [Fact]
    public void GetEntriesByDate_returns_empty_for_no_entries()
    {
        var entries = _db.GetEntriesByDate("1999-01-01", AccessLevel.Guardian, null);
        Assert.Empty(entries);
    }

    // ── GetDatesWithEntries ────────────────────────────────────

    [Fact]
    public void GetDatesWithEntries_returns_distinct_dates()
    {
        _db.WriteEntry("Entry 1");
        _db.WriteEntry("Entry 2");

        var dates = _db.GetDatesWithEntries(AccessLevel.Guardian, null);
        Assert.Single(dates); // Both today, so one distinct date
    }

    // ── Scoped Calendar Isolation ──────────────────────────────

    [Fact]
    public void Scoped_plans_do_not_leak_between_scopes()
    {
        _db.UpsertCalendarPlans("2026-05-01", "Alpha work", "alpha", false);
        _db.UpsertCalendarPlans("2026-05-01", "Beta work", "beta", false);

        var alpha = _db.GetCalendarDay("2026-05-01", AccessLevel.Scoped, "alpha");
        Assert.Single(alpha);
        Assert.Equal("Alpha work", alpha[0].Plans);

        var beta = _db.GetCalendarDay("2026-05-01", AccessLevel.Scoped, "beta");
        Assert.Single(beta);
        Assert.Equal("Beta work", beta[0].Plans);
    }

    // ── ResolveAccess Integration ──────────────────────────────

    [Fact]
    public void ResolveAccess_guardian_can_write_restricted()
    {
        var (level, scope) = Resolve(GuardianSecret);
        Assert.Equal(AccessLevel.Guardian, level);
        Assert.Null(scope);
    }

    [Fact]
    public void ResolveAccess_coding_gets_coding()
    {
        var (level, scope) = Resolve(CodingSecret);
        Assert.Equal(AccessLevel.Coding, level);
        Assert.Null(scope);
    }

    [Fact]
    public void ResolveAccess_scope_secret_returns_scoped()
    {
        var (level, scope) = Resolve(ScopeSecret);
        Assert.Equal(AccessLevel.Scoped, level);
        Assert.Equal("alpha", scope);
    }

    [Fact]
    public void ResolveAccess_bad_secret_returns_none()
    {
        var (level, _) = Resolve("wrong-secret");
        Assert.Equal(AccessLevel.None, level);
    }

    // ── Edge Cases ─────────────────────────────────────────────

    [Fact]
    public void Summary_without_plans_leaves_plans_null()
    {
        _db.UpsertCalendarSummary("2026-06-01", "Just a summary", null, false);
        var day = _db.GetCalendarDay("2026-06-01", AccessLevel.Guardian, null);
        Assert.Single(day);
        Assert.Equal("Just a summary", day[0].Summary);
        Assert.Null(day[0].Plans);
    }

    [Fact]
    public void Plans_without_summary_leaves_summary_null()
    {
        _db.UpsertCalendarPlans("2026-06-02", "Just plans", null, false);
        var day = _db.GetCalendarDay("2026-06-02", AccessLevel.Guardian, null);
        Assert.Single(day);
        Assert.Null(day[0].Summary);
        Assert.Equal("Just plans", day[0].Plans);
    }

    [Fact]
    public void Future_date_plans_work()
    {
        _db.UpsertCalendarPlans("2030-12-31", "Future plans", null, false);
        var day = _db.GetCalendarDay("2030-12-31", AccessLevel.Guardian, null);
        Assert.Single(day);
        Assert.Equal("Future plans", day[0].Plans);
    }
}
