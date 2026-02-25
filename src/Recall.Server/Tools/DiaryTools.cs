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

    [McpServerTool(Name = "diary_write")]
    [Description("Write a diary entry. Record thoughts, events, decisions, insights, or anything worth remembering. Be specific and detailed.")]
    public static string Write(
        DiaryDatabase db,
        RecallConfig config,
        [Description("The diary entry text")] string content,
        [Description("Optional comma-separated tags (e.g. 'work,decision,project-x')")] string? tags = null,
        [Description("Optional conversation ID to group related entries")] string? conversationId = null,
        [Description("Set false to make entry visible to all sessions (default: restricted for authenticated sessions, unrestricted for stdio)")] bool restricted = true,
        [Description("Access secret")] string? secret = null)
    {
        var (access, userScope) = db.ResolveAccess(secret, config.GuardianSecretHash, config.CodingSecretHash, config.Scopes);
        if (access == AccessLevel.None)
            return "Access denied. Provide a valid secret.";

        // Auto-prepend date header if not already present
        if (!content.StartsWith("**Date:", StringComparison.OrdinalIgnoreCase)
            && !content.StartsWith("Date:", StringComparison.OrdinalIgnoreCase))
        {
            var now = DateTimeOffset.Now;
            var dateHeader = $"**Date: {now:MMMM d, yyyy} ({now:dddd} {now:HH:mm})**";
            content = $"{dateHeader}\n\n{content}";
        }

        // Only guardian can write restricted entries
        if (access != AccessLevel.Guardian)
            restricted = false;

        // Scoped users always write to their scope
        string? scope = access == AccessLevel.Scoped ? userScope : null;

        var id = db.WriteEntry(content, tags, conversationId, restricted: restricted, scope: scope);
        var scopeNote = scope != null ? $" [scope: {scope}]" : "";
        return $"Entry #{id} saved at {DateTimeOffset.Now:yyyy-MM-dd HH:mm}{(restricted ? " [restricted]" : "")}{scopeNote}.";
    }

    [McpServerTool(Name = "diary_update")]
    [Description("Update an existing diary entry. Replaces the content and tags of the specified entry. The created_at timestamp is preserved.")]
    public static string Update(
        DiaryDatabase db,
        RecallConfig config,
        [Description("The ID of the entry to update")] int id,
        [Description("The new content for the entry")] string content,
        [Description("Optional new tags (replaces existing tags)")] string? tags = null,
        [Description("Access secret")] string? secret = null)
    {
        var (access, userScope) = db.ResolveAccess(secret, config.GuardianSecretHash, config.CodingSecretHash, config.Scopes);
        if (access == AccessLevel.None)
            return "Access denied. Provide a valid secret.";

        // Coding can't edit restricted entries
        if (access == AccessLevel.Coding && db.IsEntryRestricted(id))
            return $"Entry #{id} is restricted. Guardian access required to edit.";

        // Scoped users can only edit entries in their scope
        if (access == AccessLevel.Scoped)
        {
            var entryScope = db.GetEntryScope(id);
            if (entryScope != userScope)
                return $"Entry #{id} is not in your scope.";
        }

        var success = db.UpdateEntry(id, content, tags);
        if (!success)
            return $"Entry #{id} not found.";

        return $"Entry #{id} updated at {DateTimeOffset.Now:yyyy-MM-dd HH:mm}.";
    }

    [McpServerTool(Name = "diary_get")]
    [Description("Get a specific diary entry by its ID number.")]
    public static string GetEntry(
        DiaryDatabase db,
        RecallConfig config,
        [Description("The entry ID number")] int id,
        [Description("Access secret")] string? secret = null)
    {
        var (access, userScope) = db.ResolveAccess(secret, config.GuardianSecretHash, config.CodingSecretHash, config.Scopes);
        if (access == AccessLevel.None)
            return "Access denied. Provide a valid secret.";

        var entry = db.GetEntry(id);
        if (entry == null)
            return $"Entry #{id} not found.";

        // Access control: check scope and restricted
        if (access == AccessLevel.Scoped)
        {
            var entryScope = db.GetEntryScope(id);
            if (entryScope != userScope)
                return $"Entry #{id} is not in your scope.";
        }
        else if (access == AccessLevel.Coding && db.IsEntryRestricted(id))
        {
            return $"Entry #{id} is restricted. Guardian access required.";
        }

        return FormatEntries([entry]);
    }

    [McpServerTool(Name = "diary_query")]
    [Description("Search past diary entries using natural language. Use keywords or phrases to find specific topics, events, or decisions.")]

    public static string Query(
        DiaryDatabase db,
        RecallConfig config,
        [Description("Search words or phrase")] string query,
        [Description("Max results to return (default: from config)")] int limit = 0,
        [Description("Access secret")] string? secret = null)
    {
        var (access, userScope) = db.ResolveAccess(secret, config.GuardianSecretHash, config.CodingSecretHash, config.Scopes);
        if (access == AccessLevel.None)
            return "Access denied. Provide a valid secret.";

        // Scoped users always filter to their scope; others see global (unscoped)
        var scope = access == AccessLevel.Scoped ? userScope : null;

        var effectiveLimit = limit > 0 ? limit : config.SearchResultLimit;
        var results = db.Search(query, effectiveLimit, access, scope);
        if (results.Count == 0)
            return "No entries found matching your query.";

        return FormatEntries(results);
    }

    [McpServerTool(Name = "diary_context")]
    [Description("Get relevant diary context for the current conversation. Call this at the START of every conversation with a brief topic summary. Returns recent entries plus entries matching the topic.")]
    public static string GetContext(
        DiaryDatabase db,
        RecallConfig config,
        [Description("Brief summary of what this conversation is about")] string topic,
        [Description("Access secret")] string? secret = null)
    {
        var (access, userScope) = db.ResolveAccess(secret, config.GuardianSecretHash, config.CodingSecretHash, config.Scopes);
        if (access == AccessLevel.None)
            return "Access denied. Provide a valid secret.";

        var conversationId = Guid.NewGuid().ToString("N")[..12];
        var limit = config.AutoContextLimit;
        var scope = access == AccessLevel.Scoped ? userScope : null;

        var recent = db.GetRecent(3, access, scope);
        var relevant = db.Search(topic, limit, access, scope);

        // Merge and deduplicate
        var seen = new HashSet<int>();
        var merged = new List<DiaryEntry>();
        foreach (var e in recent.Concat(relevant))
        {
            if (seen.Add(e.Id))
                merged.Add(e);
        }

        var sorted = merged
            .OrderByDescending(e => e.CreatedAt)
            .Take(limit + 3) // recent + relevant
            .ToList();

        var totalEntries = db.GetEntryCount(access, scope);

        if (sorted.Count == 0)
            return $"No diary entries yet. This is a fresh start.\nConversation ID: {conversationId}\nCurrent time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} ({DateTimeOffset.Now:dddd})";

        var header = $"Current time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} ({DateTimeOffset.Now:dddd})\n" +
                     $"Diary has {totalEntries} entries total. Showing {sorted.Count} relevant:\n" +
                     $"Conversation ID: {conversationId}\n\n";
        return header + FormatEntries(sorted);
    }

    [McpServerTool(Name = "diary_list_recent")]
    [Description("List the most recent diary entries in chronological order.")]
    public static string ListRecent(
        DiaryDatabase db,
        RecallConfig config,
        [Description("Number of entries to return (default: 10)")] int count = 10,
        [Description("Access secret")] string? secret = null)
    {
        var (access, userScope) = db.ResolveAccess(secret, config.GuardianSecretHash, config.CodingSecretHash, config.Scopes);
        if (access == AccessLevel.None)
            return "Access denied. Provide a valid secret.";

        var scope = access == AccessLevel.Scoped ? userScope : null;
        var entries = db.GetRecent(count, access, scope);
        if (entries.Count == 0)
            return "No diary entries yet.";

        return FormatEntries(entries);
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
