#!/usr/bin/env python3
"""
Migraine Tracking & Prediction for Recall

Logs migraine events, shows history with weather/cycle context,
and predicts risk for upcoming days based on pressure forecast + cycle phase.

Usage:
    migraine.py log [DATE]                        # Log migraine (default: today)
    migraine.py log [DATE] --severity N --med X   # With details
    migraine.py history [-n 10]                   # Past migraines + context
    migraine.py predict [--days 7] [--quiet]      # 7-day risk → terminal + calendar
"""

import argparse
import json
import sqlite3
import sys
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

import requests

from cycle import get_cycle_info

RECALL_DIR = Path.home() / ".recall"
DB_PATH = RECALL_DIR / "recall.db"
CONFIG_PATH = RECALL_DIR / "fitbit.json"


# ── Database ──────────────────────────────────────────────────

def ensure_table(conn: sqlite3.Connection):
    conn.execute("""
        CREATE TABLE IF NOT EXISTS migraine_log (
            date TEXT PRIMARY KEY,
            severity INTEGER,
            medication TEXT,
            notes TEXT,
            created_at TEXT NOT NULL
        )
    """)
    conn.commit()


# ── Weather ───────────────────────────────────────────────────

WMO_CODES = {
    0: "clear", 1: "mostly clear", 2: "partly cloudy", 3: "overcast",
    45: "fog", 48: "freezing fog",
    51: "light drizzle", 53: "drizzle", 55: "heavy drizzle",
    61: "light rain", 63: "rain", 65: "heavy rain",
    71: "light snow", 73: "snow", 75: "heavy snow",
    80: "rain showers", 81: "moderate showers", 82: "heavy showers",
    95: "thunderstorm", 96: "thunderstorm + hail",
}


def fetch_weather_range(start: str, end: str, lat: float, lon: float) -> dict:
    """Fetch daily weather for a date range. Returns {date_str: weather_dict}."""
    resp = requests.get("https://api.open-meteo.com/v1/forecast", params={
        "latitude": lat, "longitude": lon,
        "start_date": start, "end_date": end,
        "daily": "temperature_2m_max,temperature_2m_min,precipitation_sum,weather_code",
        "hourly": "surface_pressure",
        "timezone": "Europe/Prague",
    }, timeout=15)
    if resp.status_code != 200:
        return {}
    data = resp.json()

    daily = data.get("daily", {})
    dates = daily.get("time", [])
    hourly = data.get("hourly", {})
    hourly_times = hourly.get("time", [])
    hourly_pressure = hourly.get("surface_pressure", [])

    # Group hourly pressure by date
    pressure_by_date: dict[str, list[float]] = {}
    for t, p in zip(hourly_times, hourly_pressure):
        if p is not None:
            d = t[:10]
            pressure_by_date.setdefault(d, []).append(p)

    result = {}
    for i, d in enumerate(dates):
        w: dict = {}
        w["temp_min"] = daily.get("temperature_2m_min", [None])[i]
        w["temp_max"] = daily.get("temperature_2m_max", [None])[i]
        w["precipitation_mm"] = daily.get("precipitation_sum", [None])[i]
        w["weather_code"] = daily.get("weather_code", [None])[i]

        pressures = pressure_by_date.get(d, [])
        if pressures:
            w["pressure_avg"] = round(sum(pressures) / len(pressures), 1)
        result[d] = w

    return result


def get_location() -> tuple[float, float]:
    """Get lat/lon from fitbit config, default Prague."""
    try:
        config = json.loads(CONFIG_PATH.read_text())
        loc = config.get("weather_location", {})
        return loc.get("lat", 50.08), loc.get("lon", 14.42)
    except Exception:
        return 50.08, 14.42


# ── Risk Scoring ──────────────────────────────────────────────

def score_day(weather: dict, prev_weather: dict | None, cycle_info: dict | None) -> tuple[int, list[str]]:
    """Compute migraine risk score for a day. Returns (score, [reasons])."""
    score = 0
    reasons = []

    # Pressure drop from previous day
    if prev_weather and weather.get("pressure_avg") and prev_weather.get("pressure_avg"):
        drop = prev_weather["pressure_avg"] - weather["pressure_avg"]
        if drop > 10:
            score += 3
            reasons.append(f"pressure ↓{drop:.0f} hPa")
        elif drop > 5:
            score += 2
            reasons.append(f"pressure ↓{drop:.0f} hPa")

    # Cycle phase
    if cycle_info:
        phase = cycle_info.get("phase", "")
        day = cycle_info.get("cycle_day", 0)
        if phase == "Menstrual":
            score += 2
            reasons.append(f"menstrual day {day}")
        elif phase == "Ovulatory":
            score += 1
            reasons.append(f"ovulatory day {day}")

    # Temperature swing
    if weather.get("temp_min") is not None and weather.get("temp_max") is not None:
        swing = weather["temp_max"] - weather["temp_min"]
        if swing > 12:
            score += 1
            reasons.append(f"temp swing {swing:.0f}°C")

    return score, reasons


def risk_label(score: int) -> str:
    if score >= 4:
        return "HIGH"
    if score >= 2:
        return "MODERATE"
    return "LOW"


# ── Commands ──────────────────────────────────────────────────

def do_log(conn: sqlite3.Connection, date_str: str, severity: int | None,
           medication: str | None, notes: str | None):
    ensure_table(conn)
    conn.execute("""
        INSERT OR REPLACE INTO migraine_log (date, severity, medication, notes, created_at)
        VALUES (?, ?, ?, ?, ?)
    """, (date_str, severity, medication, notes,
          datetime.now(tz=timezone.utc).isoformat()))
    conn.commit()
    print(f"Logged migraine for {date_str}", end="")
    if severity:
        print(f" (severity {severity})", end="")
    if medication:
        print(f" (med: {medication})", end="")
    print()


def do_history(conn: sqlite3.Connection, n: int):
    ensure_table(conn)
    rows = conn.execute("""
        SELECT m.date, m.severity, m.medication, m.notes,
               h.weather_json, h.summary
        FROM migraine_log m
        LEFT JOIN health_data h ON m.date = h.date
        ORDER BY m.date DESC
        LIMIT ?
    """, (n,)).fetchall()

    if not rows:
        print("No migraines logged yet.")
        return

    print(f"Migraine history ({len(rows)} entries):\n")
    for date_str, severity, med, notes, weather_json, summary in rows:
        parts = [date_str]
        if severity:
            parts.append(f"severity {severity}")
        if med:
            parts.append(f"med: {med}")
        print("  " + "  ".join(parts))

        # Weather context
        if weather_json:
            w = json.loads(weather_json)
            ctx = []
            if w.get("pressure_avg"):
                p = f"pressure {w['pressure_avg']:.0f} hPa"
                morning = w.get("pressure_morning")
                evening = w.get("pressure_evening")
                if morning and evening:
                    ch = evening - morning
                    arrow = "↑" if ch > 0.5 else "↓" if ch < -0.5 else "→"
                    p += f" ({arrow}{abs(ch):.0f} over day)"
                ctx.append(p)
            if w.get("temp_min") is not None and w.get("temp_max") is not None:
                ctx.append(f"{w['temp_min']:.0f}→{w['temp_max']:.0f}°C")
            if ctx:
                print(f"            {', '.join(ctx)}")

        # Cycle context from summary
        if summary:
            for line in summary.split("\n"):
                if line.startswith("- Day ") and "phase" in line.lower():
                    print(f"            cycle: {line[2:]}")
                    break

        if notes:
            print(f"            notes: {notes}")
        print()


def do_predict(conn: sqlite3.Connection, days: int, quiet: bool):
    ensure_table(conn)
    lat, lon = get_location()
    today = date.today()

    # Fetch weather: yesterday (for day-over-day delta) through forecast period
    start = (today - timedelta(days=1)).isoformat()
    end = (today + timedelta(days=days - 1)).isoformat()

    weather_data = fetch_weather_range(start, end, lat, lon)
    if not weather_data:
        print("Failed to fetch weather forecast.")
        return

    if not quiet:
        print(f"Migraine forecast (next {days} days):\n")

    forecast_dates = [today + timedelta(days=i) for i in range(days)]

    for d in forecast_dates:
        d_str = d.isoformat()
        prev_str = (d - timedelta(days=1)).isoformat()

        w = weather_data.get(d_str, {})
        prev_w = weather_data.get(prev_str)

        # If previous day not in forecast, try DB
        if prev_w is None:
            row = conn.execute(
                "SELECT weather_json FROM health_data WHERE date = ?", (prev_str,)
            ).fetchone()
            if row and row[0]:
                prev_w = json.loads(row[0])

        cycle = get_cycle_info(conn, d)
        score, reasons = score_day(w, prev_w, cycle)
        label = risk_label(score)

        # Terminal output
        if not quiet:
            day_name = d.strftime("%a")
            d_short = d.strftime("%b %d")

            parts = []
            if w.get("pressure_avg"):
                p = f"{w['pressure_avg']:.0f} hPa"
                if prev_w and prev_w.get("pressure_avg"):
                    delta = w["pressure_avg"] - prev_w["pressure_avg"]
                    arrow = "↑" if delta > 0.5 else "↓" if delta < -0.5 else "→"
                    p += f" ({arrow}{abs(delta):.0f})"
                parts.append(p)
            if w.get("temp_min") is not None and w.get("temp_max") is not None:
                parts.append(f"{w['temp_min']:.0f}→{w['temp_max']:.0f}°C")
            if cycle:
                parts.append(f"{cycle['phase'].lower()} day {cycle['cycle_day']}")
            desc = WMO_CODES.get(w.get("weather_code"), "")
            if desc:
                parts.append(desc)

            detail = ", ".join(parts)
            print(f"  {d_short} {day_name}  {label:8s}  {detail}")

        # Write to calendar
        plan_text = f"Migraine risk: {label}"
        if reasons:
            plan_text += f" — {', '.join(reasons)}"
        elif label == "LOW":
            plan_text += " — no significant triggers forecast"

        now_iso = datetime.now(tz=timezone.utc).isoformat()
        conn.execute("""
            INSERT INTO calendar (date, scope, restricted, summary, plans, created_at, updated_at)
            VALUES (?, NULL, 0, NULL, ?, ?, ?)
            ON CONFLICT (date, COALESCE(scope, ''), restricted)
            DO UPDATE SET plans = ?, updated_at = ?
        """, (d_str, plan_text, now_iso, now_iso, plan_text, now_iso))

    conn.commit()
    if not quiet:
        print(f"\nWritten to calendar for {forecast_dates[0].isoformat()} – {forecast_dates[-1].isoformat()}.")


# ── Diary Sync ────────────────────────────────────────────────

def do_sync(conn: sqlite3.Connection):
    """Pull migraine entries from diary (tagged 'migraine') into migraine_log.

    Guardian can log migraines via diary_write with tags:"migraine".
    This syncs those into the structured migraine_log table.
    Uses INSERT OR IGNORE so CLI-logged entries are never overwritten.
    """
    ensure_table(conn)
    rows = conn.execute("""
        SELECT date(created_at) as d, content
        FROM entries
        WHERE ',' || tags || ',' LIKE '%,migraine,%'
          AND date(created_at) NOT IN (SELECT date FROM migraine_log)
        ORDER BY created_at
    """).fetchall()

    if not rows:
        return

    for date_str, content in rows:
        # Best-effort medication extraction from diary content
        med = None
        content_lower = content.lower()
        for drug in ("rimegepant", "ibuprofen", "paracetamol", "aspirin", "naproxen"):
            if drug in content_lower:
                med = drug
                break

        conn.execute("""
            INSERT OR IGNORE INTO migraine_log (date, severity, medication, notes, created_at)
            VALUES (?, NULL, ?, ?, ?)
        """, (date_str, med, content.strip(), datetime.now(tz=timezone.utc).isoformat()))

    conn.commit()
    print(f"Synced {len(rows)} migraine(s) from diary.")


# ── CLI ───────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Migraine Tracking & Prediction")
    sub = parser.add_subparsers(dest="command")

    log_p = sub.add_parser("log", help="Log a migraine event")
    log_p.add_argument("date", nargs="?", default=None, help="Date (YYYY-MM-DD, default: today)")
    log_p.add_argument("--severity", "-s", type=int, choices=[1, 2, 3], help="Severity 1-3")
    log_p.add_argument("--med", "-m", help="Medication taken")
    log_p.add_argument("--notes", "-n", help="Optional notes")

    hist_p = sub.add_parser("history", help="Show migraine history")
    hist_p.add_argument("-n", type=int, default=10, help="Number of entries (default: 10)")

    sub.add_parser("sync", help="Sync migraine-tagged diary entries into migraine_log")

    pred_p = sub.add_parser("predict", help="Predict migraine risk")
    pred_p.add_argument("--days", "-d", type=int, default=7, help="Forecast days (default: 7)")
    pred_p.add_argument("--quiet", "-q", action="store_true", help="Suppress terminal output")

    args = parser.parse_args()

    if not args.command:
        parser.print_help()
        sys.exit(1)

    conn = sqlite3.connect(str(DB_PATH))

    if args.command == "log":
        date_str = args.date or date.today().isoformat()
        do_log(conn, date_str, args.severity, args.med, args.notes)
    elif args.command == "sync":
        do_sync(conn)
    elif args.command == "history":
        do_history(conn, args.n)
    elif args.command == "predict":
        do_sync(conn)  # pick up any guardian-logged migraines first
        do_predict(conn, args.days, args.quiet)

    conn.close()


if __name__ == "__main__":
    main()
