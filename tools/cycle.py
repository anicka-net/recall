#!/usr/bin/env python3
"""
Menstrual Cycle Tracking for Recall Health System

Stores period start dates, calculates cycle day/phase for any date,
predicts next period using exponentially weighted recent cycles,
and generates summary blocks for fitbit-sync.py health summaries.

Usage:
    python3 cycle.py import ~/.mesicky       # one-time import from flat file
    python3 cycle.py add 2026-02-15          # record period start
    python3 cycle.py add 2026-02-15 "short, stress-induced"  # with note
    python3 cycle.py status                  # current cycle day + prediction
    python3 cycle.py history                 # recent cycles with lengths
"""

import argparse
import math
import sqlite3
import sys
from datetime import datetime, date, timedelta
from pathlib import Path

DB_PATH = Path.home() / ".recall" / "recall.db"

PHASES = [
    (5, "Menstrual"),
    (13, "Follicular"),
    (16, "Ovulatory"),
    (99, "Luteal"),
]


# ── Database ──────────────────────────────────────────────────

def ensure_table(conn: sqlite3.Connection):
    conn.execute("""
        CREATE TABLE IF NOT EXISTS cycle_starts (
            date TEXT PRIMARY KEY,
            notes TEXT,
            created_at TEXT NOT NULL
        )
    """)
    conn.commit()


def get_starts(conn: sqlite3.Connection) -> list[date]:
    """Return all period start dates, sorted ascending."""
    rows = conn.execute(
        "SELECT date FROM cycle_starts ORDER BY date ASC"
    ).fetchall()
    return [date.fromisoformat(r[0]) for r in rows]


def get_cycle_lengths(starts: list[date]) -> list[tuple[date, int]]:
    """Return list of (start_date, cycle_length_days) for each completed cycle."""
    lengths = []
    for i in range(len(starts) - 1):
        days = (starts[i + 1] - starts[i]).days
        lengths.append((starts[i], days))
    return lengths


# ── Import ────────────────────────────────────────────────────

def import_mesicky(conn: sqlite3.Connection, path: str):
    """Import period starts from .mesicky flat file (dd. mm. yyyy format)."""
    ensure_table(conn)
    now = datetime.now().isoformat()
    count = 0

    with open(path) as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            parts = line.split(". ")
            if len(parts) != 3:
                print(f"  Skipping malformed line: {line!r}")
                continue
            try:
                day, month, year = int(parts[0]), int(parts[1]), int(parts[2])
                d = date(year, month, day)
            except (ValueError, IndexError):
                print(f"  Skipping invalid date: {line!r}")
                continue

            conn.execute(
                "INSERT OR IGNORE INTO cycle_starts (date, notes, created_at) VALUES (?, ?, ?)",
                (d.isoformat(), None, now),
            )
            count += 1

    conn.commit()
    print(f"Imported {count} entries from {path}")


# ── Add ───────────────────────────────────────────────────────

def add_start(conn: sqlite3.Connection, date_str: str, notes: str | None = None):
    """Record a new period start date."""
    ensure_table(conn)
    d = date.fromisoformat(date_str)
    now = datetime.now().isoformat()
    conn.execute(
        "INSERT OR REPLACE INTO cycle_starts (date, notes, created_at) VALUES (?, ?, ?)",
        (d.isoformat(), notes, now),
    )
    conn.commit()
    print(f"Recorded period start: {d.isoformat()}" + (f" ({notes})" if notes else ""))


# ── Prediction ────────────────────────────────────────────────

def predict_cycle_length(starts: list[date], n_recent: int = 8) -> float | None:
    """Exponentially weighted average of recent cycle lengths.

    More recent cycles get higher weight. Uses last n_recent completed cycles.
    """
    lengths = get_cycle_lengths(starts)
    if not lengths:
        return None

    recent = lengths[-n_recent:]
    n = len(recent)

    # Exponential weights: most recent gets highest weight
    # decay factor 0.7 means each older cycle gets 70% of the next one's weight
    decay = 0.7
    weights = [decay ** (n - 1 - i) for i in range(n)]
    total_weight = sum(weights)

    weighted_sum = sum(w * length for w, (_, length) in zip(weights, recent))
    return weighted_sum / total_weight


def detect_trend(starts: list[date], n_recent: int = 10) -> str:
    """Detect if cycles are shortening, lengthening, or stable."""
    lengths = get_cycle_lengths(starts)
    if len(lengths) < 4:
        return "insufficient data"

    recent = [l for _, l in lengths[-n_recent:]]
    n = len(recent)

    # Simple linear regression: slope of cycle length over time
    x_mean = (n - 1) / 2.0
    y_mean = sum(recent) / n
    numerator = sum((i - x_mean) * (y - y_mean) for i, y in enumerate(recent))
    denominator = sum((i - x_mean) ** 2 for i in range(n))

    if denominator == 0:
        return "stable"

    slope = numerator / denominator

    if slope > 0.3:
        return "lengthening"
    elif slope < -0.3:
        return "shortening"
    return "stable"


# ── Cycle Info ────────────────────────────────────────────────

def get_phase(cycle_day: int) -> str:
    """Map cycle day to phase name."""
    for threshold, name in PHASES:
        if cycle_day <= threshold:
            return name
    return "Luteal"


def get_cycle_info(conn: sqlite3.Connection, for_date: date | None = None) -> dict | None:
    """Get cycle context for a given date.

    Returns dict with: cycle_day, phase, cycle_length_estimate,
    predicted_next, trend, recent_range, note
    """
    ensure_table(conn)
    starts = get_starts(conn)
    if not starts:
        return None

    if for_date is None:
        for_date = date.today()

    # Find the most recent period start on or before for_date
    last_start = None
    for s in reversed(starts):
        if s <= for_date:
            last_start = s
            break

    if last_start is None:
        return None

    cycle_day = (for_date - last_start).days + 1  # day 1 = first day of period

    est_length = predict_cycle_length(starts)
    if est_length is None:
        est_length = 28.0  # fallback

    predicted_next = last_start + timedelta(days=round(est_length))

    # Recent cycle length range
    lengths = get_cycle_lengths(starts)
    recent_lengths = [l for _, l in lengths[-5:]]
    recent_range = (min(recent_lengths), max(recent_lengths)) if recent_lengths else None

    trend = detect_trend(starts)

    # Check for note on this cycle's start
    row = conn.execute(
        "SELECT notes FROM cycle_starts WHERE date = ?", (last_start.isoformat(),)
    ).fetchone()
    note = row[0] if row and row[0] else None

    # Flag if current cycle is running long
    anomaly = None
    if cycle_day > est_length + 5:
        anomaly = f"cycle running long (day {cycle_day} vs ~{est_length:.0f} expected)"
    elif recent_range and est_length < recent_range[0] - 3:
        anomaly = f"recent cycles shorter than usual"

    return {
        "cycle_day": cycle_day,
        "phase": get_phase(cycle_day),
        "cycle_length_estimate": round(est_length, 1),
        "predicted_next": predicted_next.isoformat(),
        "trend": trend,
        "recent_range": recent_range,
        "anomaly": anomaly,
        "note": note,
    }


# ── Summary for fitbit-sync ──────────────────────────────────

def build_cycle_summary(conn: sqlite3.Connection, for_date: date | None = None) -> str | None:
    """Build cycle summary text block for health summary integration."""
    info = get_cycle_info(conn, for_date)
    if not info:
        return None

    lines = ["Cycle:"]
    est = info["cycle_length_estimate"]
    lines.append(f"- Day {info['cycle_day']} of ~{est:.0f} ({info['phase']} phase)")
    lines.append(f"- Predicted next: {info['predicted_next']}")

    if info["recent_range"]:
        lo, hi = info["recent_range"]
        lines.append(f"- Recent trend: {info['trend']} (last 5 range {lo}-{hi})")

    if info["anomaly"]:
        lines.append(f"- Note: {info['anomaly']}")

    if info["note"]:
        lines.append(f"- Cycle note: {info['note']}")

    return "\n".join(lines)


# ── CLI ───────────────────────────────────────────────────────

def cmd_import(args):
    conn = sqlite3.connect(str(DB_PATH))
    import_mesicky(conn, args.path)
    starts = get_starts(conn)
    lengths = get_cycle_lengths(starts)
    print(f"Total entries: {len(starts)}, completed cycles: {len(lengths)}")
    conn.close()


def cmd_add(args):
    conn = sqlite3.connect(str(DB_PATH))
    add_start(conn, args.date, args.note)
    conn.close()


def cmd_today(args):
    conn = sqlite3.connect(str(DB_PATH))
    add_start(conn, date.today().isoformat(), args.note)
    conn.close()


def cmd_status(args):
    conn = sqlite3.connect(str(DB_PATH))
    ensure_table(conn)

    info = get_cycle_info(conn)
    if not info:
        print("No cycle data found. Import with: cycle.py import ~/.mesicky")
        conn.close()
        return

    summary = build_cycle_summary(conn)
    print(summary)
    print()

    # Extra detail for CLI — recent cycles only
    starts = get_starts(conn)
    lengths = get_cycle_lengths(starts)
    if lengths:
        recent = [l for _, l in lengths[-10:]]
        print(f"Recent 10 avg: {sum(recent)/len(recent):.1f} days (range {min(recent)}-{max(recent)})")
        print(f"Total tracked: {len(lengths)} cycles")

    conn.close()


def cmd_history(args):
    conn = sqlite3.connect(str(DB_PATH))
    ensure_table(conn)
    starts = get_starts(conn)
    lengths = get_cycle_lengths(starts)

    n = args.count or 20
    recent = lengths[-n:]

    if not recent:
        print("No completed cycles found.")
        conn.close()
        return

    all_lens = [l for _, l in lengths]
    avg = sum(all_lens) / len(all_lens)

    print(f"Last {len(recent)} cycles (avg {avg:.1f} days):\n")
    for start, length in recent:
        marker = ""
        if length < avg - 4:
            marker = " << short"
        elif length > avg + 4:
            marker = " >> long"
        print(f"  {start.isoformat()}  {length:2d} days{marker}")

    # Current incomplete cycle
    if starts:
        current_day = (date.today() - starts[-1]).days + 1
        print(f"\n  {starts[-1].isoformat()}  day {current_day} (current)")

    conn.close()


def main():
    parser = argparse.ArgumentParser(description="Menstrual cycle tracking for Recall")
    sub = parser.add_subparsers(dest="command")

    imp = sub.add_parser("import", help="Import from .mesicky flat file")
    imp.add_argument("path", help="Path to .mesicky file")

    add = sub.add_parser("add", help="Record period start")
    add.add_argument("date", help="Start date (YYYY-MM-DD)")
    add.add_argument("note", nargs="?", default=None, help="Optional note")

    today_p = sub.add_parser("today", help="Record period start as today")
    today_p.add_argument("note", nargs="?", default=None, help="Optional note")

    sub.add_parser("status", help="Current cycle day and prediction")

    hist = sub.add_parser("history", help="Recent cycle history")
    hist.add_argument("-n", "--count", type=int, default=20, help="Number of cycles to show")

    args = parser.parse_args()

    if not args.command:
        parser.print_help()
        sys.exit(1)

    {"import": cmd_import, "add": cmd_add, "today": cmd_today, "status": cmd_status, "history": cmd_history}[args.command](args)


if __name__ == "__main__":
    main()
