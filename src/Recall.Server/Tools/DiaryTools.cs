using System.ComponentModel;
using ModelContextProtocol.Server;
using Recall.Storage;

namespace Recall.Server.Tools;

[McpServerToolType]
public class DiaryTools
{
    [McpServerTool(Name = "diary_time")]
    [Description("Returns the current date, time, and day of week. Call this when you need to know what time it is.")]
    public static string GetTime()
    {
        var now = DateTimeOffset.Now;
        return $"{now:yyyy-MM-dd HH:mm:ss zzz} ({now:dddd})";
    }

    // ── diary (write/update/get/pin) ──────────────────────────

    [McpServerTool(Name = "diary")]
    [Description("Diary entry operations. Actions: 'write' (new entry), 'update' (edit existing), 'get' (by ID), 'pin' (pin/unpin entry).")]
    public static string Entry(
        DiaryDatabase db,
        RecallConfig config,
        [Description("Action: write, update, get, pin")] string action = "write",
        [Description("Entry ID (for get/update/pin)")] int? id = null,
        [Description("Entry text (for write/update)")] string? content = null,
        [Description("Comma-separated tags (for write/update)")] string? tags = null,
        [Description("Conversation ID (for write)")] string? conversationId = null,
        [Description("Restrict entry to guardian (for write)")] bool restricted = true,
        [Description("Pin the entry (for pin)")] bool pin = true,
        [Description("Mark as foundational (for pin)")] bool foundational = false,
        [Description("Access secret")] string? secret = null)
    {
        var (access, userScope) = db.ResolveAccess(secret, config.GuardianSecretHash, config.CodingSecretHash, config.Scopes);
        if (access == AccessLevel.None)
            return "Access denied. Provide a valid secret.";

        return action switch
        {
            "write" => DoWrite(db, access, userScope, content, tags, conversationId, restricted),
            "update" when id != null => DoUpdate(db, access, userScope, id.Value, content ?? "", tags),
            "update" => "Provide id to update.",
            "get" when id != null => DoGet(db, access, userScope, id.Value),
            "get" => "Provide id.",
            "pin" when id != null => DoPin(db, access, id.Value, pin, foundational),
            "pin" => "Provide id to pin.",
            _ => $"Unknown action '{action}'. Use: write, update, get, pin"
        };
    }

    private static string DoWrite(DiaryDatabase db, AccessLevel access, string? userScope,
        string? content, string? tags, string? conversationId, bool restricted)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "Provide content to write.";

        // Auto-prepend date header if not already present
        if (!content.StartsWith("**Date:", StringComparison.OrdinalIgnoreCase)
            && !content.StartsWith("Date:", StringComparison.OrdinalIgnoreCase))
        {
            var now = DateTimeOffset.Now;
            var dateHeader = $"**Date: {now:MMMM d, yyyy} ({now:dddd} {now:HH:mm})**";
            content = $"{dateHeader}\n\n{content}";
        }

        if (access != AccessLevel.Guardian)
            restricted = false;

        string? scope = access == AccessLevel.Scoped ? userScope : null;
        var id = db.WriteEntry(content, tags, conversationId, restricted: restricted, scope: scope);
        var scopeNote = scope != null ? $" [scope: {scope}]" : "";
        return $"Entry #{id} saved at {DateTimeOffset.Now:yyyy-MM-dd HH:mm}{(restricted ? " [restricted]" : "")}{scopeNote}.";
    }

    private static string DoUpdate(DiaryDatabase db, AccessLevel access, string? userScope,
        int id, string content, string? tags)
    {
        if (access == AccessLevel.Coding && db.IsEntryRestricted(id))
            return $"Entry #{id} is restricted. Guardian access required to edit.";

        if (access == AccessLevel.Scoped)
        {
            var entryScope = db.GetEntryScope(id);
            if (entryScope != userScope)
                return $"Entry #{id} is not in your scope.";
        }

        var success = db.UpdateEntry(id, content, tags);
        if (!success) return $"Entry #{id} not found.";
        return $"Entry #{id} updated at {DateTimeOffset.Now:yyyy-MM-dd HH:mm}.";
    }

    private static string DoGet(DiaryDatabase db, AccessLevel access, string? userScope, int id)
    {
        var entry = db.GetEntry(id);
        if (entry == null) return $"Entry #{id} not found.";

        if (access == AccessLevel.Scoped)
        {
            var entryScope = db.GetEntryScope(id);
            if (entryScope != userScope)
                return $"Entry #{id} is not in your scope.";
        }
        else if (access == AccessLevel.Coding && db.IsEntryRestricted(id))
            return $"Entry #{id} is restricted. Guardian access required.";

        return FormatEntries([entry]);
    }

    private static string DoPin(DiaryDatabase db, AccessLevel access, int id, bool pin, bool foundational)
    {
        if (access != AccessLevel.Guardian)
            return "Only guardian can pin entries.";

        var success = db.SetPin(id, pin, foundational);
        if (!success) return $"Entry #{id} not found.";

        var status = foundational ? "foundational + pinned"
            : pin ? "pinned" : "unpinned";
        return $"Entry #{id} marked as {status}.";
    }

    // ── diary_search (query/list/context) ─────────────────────

    [McpServerTool(Name = "diary_search")]
    [Description("Find diary entries. Actions: 'context' (call at conversation START with topic), 'query' (search by keywords), 'list' (recent entries).")]
    public static string Search(
        DiaryDatabase db,
        RecallConfig config,
        [Description("Action: context, query, list")] string action = "context",
        [Description("Search query or conversation topic")] string? query = null,
        [Description("Max results (for query/list)")] int limit = 0,
        [Description("Access secret")] string? secret = null)
    {
        var (access, userScope) = db.ResolveAccess(secret, config.GuardianSecretHash, config.CodingSecretHash, config.Scopes);
        if (access == AccessLevel.None)
            return "Access denied. Provide a valid secret.";

        var scope = access == AccessLevel.Scoped ? userScope : null;

        var memory = config.GetMemoryFeatures(scope);

        return action switch
        {
            "context" => DoContext(db, config, access, scope, query ?? "general", memory),
            "query" when query != null => DoQuery(db, config, access, scope, query, limit, memory),
            "query" => "Provide query to search.",
            "list" => DoList(db, access, scope, limit > 0 ? limit : 10),
            _ => $"Unknown action '{action}'. Use: context, query, list"
        };
    }

    private static string DoContext(DiaryDatabase db, RecallConfig config,
        AccessLevel access, string? scope, string topic, MemoryFeatures memory)
    {
        var conversationId = Guid.NewGuid().ToString("N")[..12];
        var limit = config.AutoContextLimit;

        db.RunAging(config.TierHotDays, config.TierWarmDays);

        string foundationalSection = "";
        if (access == AccessLevel.Guardian)
        {
            var found = db.GetFoundational();
            if (found.Count > 0)
                foundationalSection = FormatFoundationalIndex(found) + "\n";
        }

        var recent = db.GetRecent(3, access, scope, maxTier: 0);
        var relevant = db.Search(topic, limit, access, scope, maxTier: 1, memory: memory);

        var seen = new HashSet<int>();
        var merged = new List<DiaryEntry>();
        foreach (var e in recent.Concat(relevant))
        {
            if (seen.Add(e.Id))
                merged.Add(e);
        }

        var sorted = merged
            .OrderByDescending(e => e.CreatedAt)
            .Take(limit + 3)
            .ToList();

        var (hot, warm, cold) = db.GetTierCounts(access, scope);

        if (sorted.Count == 0 && string.IsNullOrEmpty(foundationalSection))
            return $"No diary entries yet. This is a fresh start.\nConversation ID: {conversationId}\nCurrent time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} ({DateTimeOffset.Now:dddd})";

        var header = $"Current time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} ({DateTimeOffset.Now:dddd})\n" +
                     $"Diary: {hot} hot / {warm} warm / {cold} cold entries. Showing {sorted.Count} relevant:\n" +
                     $"Conversation ID: {conversationId}\n\n";
        return header + foundationalSection + FormatEntries(sorted);
    }

    private static string DoQuery(DiaryDatabase db, RecallConfig config,
        AccessLevel access, string? scope, string query, int limit, MemoryFeatures memory)
    {
        var effectiveLimit = limit > 0 ? limit : config.SearchResultLimit;
        var results = db.Search(query, effectiveLimit, access, scope, maxTier: 2, memory: memory);
        if (results.Count == 0)
            return "No entries found matching your query.";
        return FormatEntries(results);
    }

    private static string DoList(DiaryDatabase db, AccessLevel access, string? scope, int count)
    {
        var entries = db.GetRecent(count, access, scope, maxTier: 0);
        if (entries.Count == 0)
            return "No diary entries yet.";
        return FormatEntries(entries);
    }

    // ── diary_day (view/plan/summarize) ───────────────────────

    [McpServerTool(Name = "diary_day")]
    [Description("Day-level operations. Actions: 'view' (plans + summary + entries for a date), 'plan' (set plans), 'summarize' (store daily summary).")]
    public static string Day(
        DiaryDatabase db,
        RecallConfig config,
        [Description("Date in YYYY-MM-DD format")] string date,
        [Description("Action: view, plan, summarize")] string action = "view",
        [Description("Plans or summary text (for plan/summarize)")] string? content = null,
        [Description("Restrict to guardian (for plan/summarize)")] bool restricted = false,
        [Description("Access secret")] string? secret = null)
    {
        var (access, userScope) = db.ResolveAccess(secret, config.GuardianSecretHash, config.CodingSecretHash, config.Scopes);
        if (access == AccessLevel.None)
            return "Access denied. Provide a valid secret.";

        var scope = access == AccessLevel.Scoped ? userScope : null;
        if (access != AccessLevel.Guardian) restricted = false;

        return action switch
        {
            "view" => DoView(db, access, scope, date),
            "plan" when content != null => DoPlan(db, date, content, scope, restricted),
            "plan" => "Provide content for the plan.",
            "summarize" when content != null => DoSummarize(db, date, content, scope, restricted),
            "summarize" => "Provide content for the summary.",
            _ => $"Unknown action '{action}'. Use: view, plan, summarize"
        };
    }

    private static string DoView(DiaryDatabase db, AccessLevel access, string? scope, string date)
    {
        var calendar = db.GetCalendarDay(date, access, scope);
        var entries = db.GetEntriesByDate(date, access, scope);

        var lines = new List<string> { $"=== {date} ===" };

        var plans = calendar.Where(c => !string.IsNullOrEmpty(c.Plans)).ToList();
        if (plans.Count > 0)
        {
            lines.Add("\n-- Plans --");
            foreach (var p in plans)
            {
                var label = p.Restricted ? " [restricted]" : "";
                if (p.Scope != null) label += $" [scope: {p.Scope}]";
                lines.Add($"{p.Plans}{label}");
            }
        }

        var summaries = calendar.Where(c => !string.IsNullOrEmpty(c.Summary)).ToList();
        if (summaries.Count > 0)
        {
            lines.Add("\n-- Summary --");
            foreach (var s in summaries)
            {
                var label = s.Restricted ? " [restricted]" : "";
                if (s.Scope != null) label += $" [scope: {s.Scope}]";
                lines.Add($"{s.Summary}{label}");
            }
        }

        if (entries.Count > 0)
        {
            lines.Add($"\n-- Diary entries ({entries.Count}) --");
            lines.Add(FormatEntries(entries));
        }
        else
        {
            lines.Add("\nNo diary entries for this date.");
        }

        return string.Join("\n", lines);
    }

    private static string DoPlan(DiaryDatabase db, string date, string plans, string? scope, bool restricted)
    {
        db.UpsertCalendarPlans(date, plans, scope, restricted);
        var rNote = restricted ? " [restricted]" : "";
        return $"Plans for {date} saved{rNote}.";
    }

    private static string DoSummarize(DiaryDatabase db, string date, string summary, string? scope, bool restricted)
    {
        db.UpsertCalendarSummary(date, summary, scope, restricted);
        var rNote = restricted ? " [restricted]" : "";
        return $"Summary for {date} saved{rNote}.";
    }

    // ── Formatting ────────────────────────────────────────────

    private static string FormatFoundationalIndex(List<DiaryEntry> entries)
    {
        var lines = new List<string> { "Foundation:" };
        foreach (var e in entries)
        {
            var tagStr = string.IsNullOrEmpty(e.Tags) ? "" : $" [{e.Tags}]";
            var contentLines = e.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var summary = "";
            foreach (var line in contentLines)
            {
                if (!line.StartsWith("**Date:") && !line.StartsWith("Date:"))
                {
                    summary = line.Length > 120 ? line[..120] + "..." : line;
                    break;
                }
            }
            lines.Add($"  #{e.Id}{tagStr} {summary}");
        }
        lines.Add("  (Use diary action=get id=N to read full content)");
        return string.Join("\n", lines);
    }

    private static string FormatEntries(List<DiaryEntry> entries)
    {
        var lines = new List<string>();
        foreach (var e in entries)
        {
            var tagStr = string.IsNullOrEmpty(e.Tags) ? "" : $" [{e.Tags}]";
            var dateStr = e.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            lines.Add($"--- Entry #{e.Id} ({dateStr}){tagStr} ---");
            lines.Add(e.Content);
            lines.Add("");
        }
        return string.Join("\n", lines);
    }
}
