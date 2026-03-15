#!/bin/bash
# Fitbit hourly sync: fetch data locally, push health_data rows to remote server
# Cron: 0 * * * * /path/to/recall/tools/fitbit-cron.sh >> ~/.recall/fitbit-cron.log 2>&1

set -euo pipefail

SCRIPT_DIR="$(dirname "$(readlink -f "$0")")"
VENV="$SCRIPT_DIR/venv/bin/python3"
SYNC="$SCRIPT_DIR/fitbit-sync.py"
LOCAL_DB="$HOME/.recall/recall.db"
REMOTE="${RECALL_REMOTE_HOST:?Set RECALL_REMOTE_HOST}"
REMOTE_DB=".recall/recall.db"

echo "--- $(date -Iseconds) ---"

# 1. Pull cycle_starts from remote (entries logged via claude.ai)
#    Must happen before fitbit sync so summaries have correct cycle data.
ssh "$REMOTE" "sqlite3 ~/$REMOTE_DB \"
    SELECT 'INSERT OR REPLACE INTO cycle_starts (date, notes, created_at) VALUES ('
        || quote(date) || ',' || quote(notes) || ',' || quote(created_at) || ');'
    FROM cycle_starts;
\"" | sqlite3 "$LOCAL_DB"
echo "Pulled cycle_starts from remote."

# 2. Fetch from Fitbit API, write to local DB
"$VENV" "$SYNC" sync --days 2
echo "Local sync done."

# 3. Ensure weather_json column exists on remote, then push health_data
ssh "$REMOTE" "sqlite3 ~/$REMOTE_DB 'ALTER TABLE health_data ADD COLUMN weather_json TEXT'" 2>/dev/null || true
sqlite3 "$LOCAL_DB" "
    SELECT 'INSERT OR REPLACE INTO health_data (date, summary, sleep_json, heart_json, activity_json, spo2_json, weather_json, embedding, synced_at) VALUES ('
        || quote(date) || ',' || quote(summary) || ',' || quote(sleep_json) || ','
        || quote(heart_json) || ',' || quote(activity_json) || ',' || quote(spo2_json) || ','
        || quote(weather_json) || ',' || quote(embedding) || ',' || quote(synced_at) || ');'
    FROM health_data;
" | ssh "$REMOTE" "sqlite3 ~/$REMOTE_DB"

echo "Pushed health_data to remote."

# 3b. Update migraine risk forecast in calendar
"$VENV" "$SCRIPT_DIR/migraine.py" predict --days 7 --quiet
echo "Migraine forecast updated."

# 4. Push cycle_starts to remote (entries logged locally)
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
