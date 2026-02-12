using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Recall.Storage;

namespace Recall.Server.Tools;

[McpServerToolType]
public partial class HealthTools
{
    [McpServerTool(Name = "health_query")]
    [Description("Search health/fitness data (sleep, heart rate, steps, SpO2). " +
                 "Use natural language like 'sleep quality this week' or a date like '2026-02-10'.")]
    public static string Query(
        DiaryDatabase db,
        [Description("Search query or date (YYYY-MM-DD)")] string query,
        [Description("Max results (default 7)")] int limit = 7)
    {
        // Detect date pattern for exact lookup
        if (DatePattern().IsMatch(query.Trim()))
        {
            var entry = db.GetHealthByDate(query.Trim());
            if (entry is null)
                return $"No health data found for {query.Trim()}.";
            return entry.Summary;
        }

        var results = db.SearchHealth(query, limit);
        if (results.Count == 0)
            return "No health data found matching your query.";

        return FormatHealthEntries(results);
    }

    [McpServerTool(Name = "health_recent")]
    [Description("Show recent health/fitness summaries (sleep, heart rate, activity).")]
    public static string Recent(
        DiaryDatabase db,
        [Description("Number of days (default 7)")] int days = 7)
    {
        var results = db.GetRecentHealth(days);
        if (results.Count == 0)
            return "No health data available. Run fitbit-sync.py to import data.";

        return FormatHealthEntries(results);
    }

    private static string FormatHealthEntries(List<HealthEntry> entries)
    {
        return string.Join("\n\n---\n\n", entries.Select(e => e.Summary));
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DatePattern();
}
