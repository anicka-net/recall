using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Recall.Storage;

namespace Recall.Server.Tools;

[McpServerToolType]
public partial class HealthTools
{
    [McpServerTool(Name = "health")]
    [Description("Health & fitness data. Actions: 'recent' (last N days), 'query' (search by keywords or date), 'log_migraine' (record migraine event), 'log_period' (record period start).")]
    public static string Health(
        DiaryDatabase db,
        RecallConfig config,
        [Description("Action: recent, query, log_migraine, log_period")] string action = "recent",
        [Description("Search query or date YYYY-MM-DD (for query)")] string? query = null,
        [Description("Number of days (for recent, default 7) or max results (for query)")] int limit = 7,
        [Description("Date YYYY-MM-DD (for log_migraine/log_period, default today)")] string? date = null,
        [Description("Severity 1-10 (for log_migraine)")] int? severity = null,
        [Description("Medication taken (for log_migraine)")] string? medication = null,
        [Description("Notes (for log_migraine/log_period)")] string? notes = null,
        [Description("Access secret")] string? secret = null)
    {
        var (access, _) = db.ResolveAccess(secret, config.GuardianSecretHash, config.CodingSecretHash, config.Scopes);
        if (access == AccessLevel.None)
            return "Access denied. Provide a valid secret.";

        var isWrite = action is "log_migraine" or "log_period";
        if (isWrite && access != AccessLevel.Guardian)
            return "Logging health events requires guardian access.";

        return action switch
        {
            "recent" => DoRecent(db, limit),
            "query" when query != null => DoQuery(db, query, limit),
            "query" => "Provide query to search.",
            "log_migraine" => DoLogMigraine(db, date, severity, medication, notes),
            "log_period" => DoLogPeriod(db, date, notes),
            _ => $"Unknown action '{action}'. Use: recent, query, log_migraine, log_period"
        };
    }

    private static string DoRecent(DiaryDatabase db, int days)
    {
        var results = db.GetRecentHealth(days);
        if (results.Count == 0)
            return "No health data available. Run fitbit-sync.py to import data.";
        return FormatHealthEntries(results);
    }

    private static string DoQuery(DiaryDatabase db, string query, int limit)
    {
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

    private static string DoLogMigraine(DiaryDatabase db,
        string? date, int? severity, string? medication, string? notes)
    {
        var migraineDate = date ?? DateTimeOffset.Now.ToString("yyyy-MM-dd");

        if (!DatePattern().IsMatch(migraineDate))
            return "Invalid date format. Use YYYY-MM-DD.";

        db.LogMigraine(migraineDate, severity, medication, notes);

        var details = new List<string>();
        if (severity != null) details.Add($"severity {severity}");
        if (medication != null) details.Add(medication);
        var extra = details.Count > 0 ? $" ({string.Join(", ", details)})" : "";
        return $"Migraine logged for {migraineDate}{extra}.";
    }

    private static string DoLogPeriod(DiaryDatabase db, string? date, string? notes)
    {
        var periodDate = date ?? DateTimeOffset.Now.ToString("yyyy-MM-dd");

        if (!DatePattern().IsMatch(periodDate))
            return "Invalid date format. Use YYYY-MM-DD.";

        db.LogPeriodStart(periodDate, notes);

        var extra = !string.IsNullOrEmpty(notes) ? $" ({notes})" : "";
        return $"Period start logged for {periodDate}{extra}.";
    }

    private static string FormatHealthEntries(List<HealthEntry> entries)
    {
        return string.Join("\n\n---\n\n", entries.Select(e => e.Summary));
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DatePattern();
}
