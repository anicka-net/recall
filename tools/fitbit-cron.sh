#!/bin/bash
# Fitbit hourly sync: fetch data locally, push health_data rows to remote server
# Cron: 0 * * * * /path/to/recall/tools/fitbit-cron.sh >> ~/.recall/fitbit-cron.log 2>&1

set -euo pipefail

SCRIPT_DIR="$(dirname "$(readlink -f "$0")")"
VENV="$SCRIPT_DIR/venv/bin/python3"
SYNC="$SCRIPT_DIR/fitbit-sync.py"
LOCAL_DB="$HOME/.recall/recall.db"
REMOTE="twilight.ucw.cz"
REMOTE_DB=".recall/recall.db"

echo "--- $(date -Iseconds) ---"

# 1. Fetch from Fitbit API, write to local DB
"$VENV" "$SYNC" sync --days 2
echo "Local sync done."

# 2. Push health_data rows to remote (INSERT OR REPLACE = idempotent)
sqlite3 "$LOCAL_DB" "
    SELECT 'INSERT OR REPLACE INTO health_data (date, summary, sleep_json, heart_json, activity_json, spo2_json, embedding, synced_at) VALUES ('
        || quote(date) || ',' || quote(summary) || ',' || quote(sleep_json) || ','
        || quote(heart_json) || ',' || quote(activity_json) || ',' || quote(spo2_json) || ','
        || quote(embedding) || ',' || quote(synced_at) || ');'
    FROM health_data;
" | ssh "$REMOTE" "sqlite3 ~/$REMOTE_DB"

echo "Pushed health_data to remote."

# 3. Push cycle_starts to remote (if table exists locally)
if sqlite3 "$LOCAL_DB" "SELECT 1 FROM cycle_starts LIMIT 1" 2>/dev/null; then
    {
        echo "CREATE TABLE IF NOT EXISTS cycle_starts (date TEXT PRIMARY KEY, notes TEXT, created_at TEXT NOT NULL);"
        sqlite3 "$LOCAL_DB" "
            SELECT 'INSERT OR REPLACE INTO cycle_starts (date, notes, created_at) VALUES ('
                || quote(date) || ',' || quote(notes) || ',' || quote(created_at) || ');'
            FROM cycle_starts;
        "
    } | ssh "$REMOTE" "sqlite3 ~/$REMOTE_DB"
    echo "Pushed cycle_starts to remote."
fi
