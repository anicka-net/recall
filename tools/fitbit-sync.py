#!/usr/bin/env python3
"""
Fitbit Health Data Sync for Recall MCP Server

Fetches sleep, heart rate, activity, and SpO2 data from Fitbit API,
generates structured summaries with assessments, computes embeddings,
and writes to the shared recall.db SQLite database.

Usage:
    python3 fitbit-sync.py auth           # One-time OAuth2 setup
    python3 fitbit-sync.py sync           # Sync today + yesterday
    python3 fitbit-sync.py sync --days 30 # Backfill last 30 days
"""

import argparse
import base64
import hashlib
import json
import os
import secrets
import sqlite3
import struct
import sys
import webbrowser
from datetime import datetime, timedelta
from http.server import HTTPServer, BaseHTTPRequestHandler
from pathlib import Path
from urllib.parse import urlencode, urlparse, parse_qs

import requests
import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer, models, normalizers, pre_tokenizers, processors

from cycle import build_cycle_summary, ensure_table as ensure_cycle_table


# ── Paths ──────────────────────────────────────────────────────

RECALL_DIR = Path.home() / ".recall"
CONFIG_PATH = RECALL_DIR / "fitbit.json"
DB_PATH = RECALL_DIR / "recall.db"
MODEL_DIR = RECALL_DIR / "models" / "all-MiniLM-L6-v2"

FITBIT_AUTH_URL = "https://www.fitbit.com/oauth2/authorize"
FITBIT_TOKEN_URL = "https://api.fitbit.com/oauth2/token"
FITBIT_API_BASE = "https://api.fitbit.com"
SCOPES = "sleep heartrate activity oxygen_saturation"
REDIRECT_URI = "http://127.0.0.1:8189/callback"


# ── Config ─────────────────────────────────────────────────────

def load_config() -> dict:
    if not CONFIG_PATH.exists():
        default = {
            "client_id": "YOUR_FITBIT_APP_CLIENT_ID",
            "redirect_uri": REDIRECT_URI,
            "resting_hr_baseline": 67,
            "token": None,
        }
        RECALL_DIR.mkdir(parents=True, exist_ok=True)
        CONFIG_PATH.write_text(json.dumps(default, indent=4))
        print(f"Created default config at {CONFIG_PATH}")
        print("Edit it with your Fitbit app client_id, then run: fitbit-sync.py auth")
        sys.exit(1)

    return json.loads(CONFIG_PATH.read_text())


def save_config(config: dict):
    CONFIG_PATH.write_text(json.dumps(config, indent=4))


# ── OAuth2 PKCE ────────────────────────────────────────────────

def generate_pkce():
    """Generate PKCE code_verifier and code_challenge."""
    verifier = secrets.token_urlsafe(64)[:128]
    digest = hashlib.sha256(verifier.encode("ascii")).digest()
    challenge = base64.urlsafe_b64encode(digest).rstrip(b"=").decode("ascii")
    return verifier, challenge


class CallbackHandler(BaseHTTPRequestHandler):
    """Minimal HTTP handler to capture OAuth2 callback."""
    auth_code = None

    def do_GET(self):
        parsed = urlparse(self.path)
        params = parse_qs(parsed.query)

        if "code" in params:
            CallbackHandler.auth_code = params["code"][0]
            self.send_response(200)
            self.send_header("Content-Type", "text/html")
            self.end_headers()
            self.wfile.write(b"<html><body><h2>Authorization successful!</h2>"
                             b"<p>You can close this tab.</p></body></html>")
        else:
            error = params.get("error", ["unknown"])[0]
            self.send_response(400)
            self.send_header("Content-Type", "text/html")
            self.end_headers()
            self.wfile.write(f"<html><body><h2>Error: {error}</h2></body></html>".encode())

    def log_message(self, format, *args):
        pass  # Suppress request logging


def do_auth(config: dict):
    """Run OAuth2 PKCE authorization flow."""
    client_id = config["client_id"]
    if client_id == "YOUR_FITBIT_APP_CLIENT_ID":
        print("Error: Set your client_id in", CONFIG_PATH)
        print("Register a 'Personal' app at https://dev.fitbit.com/apps")
        print(f"  Redirect URI: {REDIRECT_URI}")
        sys.exit(1)

    verifier, challenge = generate_pkce()

    params = {
        "response_type": "code",
        "client_id": client_id,
        "redirect_uri": config.get("redirect_uri", REDIRECT_URI),
        "scope": SCOPES,
        "code_challenge": challenge,
        "code_challenge_method": "S256",
    }

    auth_url = f"{FITBIT_AUTH_URL}?{urlencode(params)}"
    print("Opening browser for Fitbit authorization...")
    print(f"If browser doesn't open, visit:\n{auth_url}\n")
    webbrowser.open(auth_url)

    # Wait for callback
    server = HTTPServer(("127.0.0.1", 8189), CallbackHandler)
    print("Waiting for authorization callback on port 8189...")
    while CallbackHandler.auth_code is None:
        server.handle_request()
    server.server_close()

    code = CallbackHandler.auth_code
    print("Got authorization code, exchanging for token...")

    # Exchange code for token
    resp = requests.post(FITBIT_TOKEN_URL, data={
        "grant_type": "authorization_code",
        "client_id": client_id,
        "code": code,
        "redirect_uri": config.get("redirect_uri", REDIRECT_URI),
        "code_verifier": verifier,
    }, headers={"Content-Type": "application/x-www-form-urlencoded"})

    if resp.status_code != 200:
        print(f"Token exchange failed ({resp.status_code}): {resp.text}")
        sys.exit(1)

    token_data = resp.json()
    token_data["expires_at"] = datetime.now().timestamp() + token_data.get("expires_in", 28800)

    config["token"] = token_data
    save_config(config)
    print("Authorization successful! Token saved.")


# ── Token Management ───────────────────────────────────────────

def ensure_token(config: dict) -> str:
    """Return a valid access token, refreshing if needed."""
    token = config.get("token")
    if not token:
        print("No token found. Run: fitbit-sync.py auth")
        sys.exit(1)

    # Refresh if expired (with 60s buffer)
    if datetime.now().timestamp() >= token.get("expires_at", 0) - 60:
        print("Token expired, refreshing...")
        resp = requests.post(FITBIT_TOKEN_URL, data={
            "grant_type": "refresh_token",
            "client_id": config["client_id"],
            "refresh_token": token["refresh_token"],
        }, headers={"Content-Type": "application/x-www-form-urlencoded"})

        if resp.status_code != 200:
            print(f"Token refresh failed ({resp.status_code}): {resp.text}")
            print("Try re-authorizing: fitbit-sync.py auth")
            sys.exit(1)

        token_data = resp.json()
        token_data["expires_at"] = datetime.now().timestamp() + token_data.get("expires_in", 28800)
        config["token"] = token_data
        save_config(config)
        print("Token refreshed.")

    return config["token"]["access_token"]


def api_get(config: dict, path: str) -> dict | None:
    """Make an authenticated GET request to Fitbit API."""
    token = ensure_token(config)
    resp = requests.get(
        f"{FITBIT_API_BASE}{path}",
        headers={"Authorization": f"Bearer {token}"},
    )
    if resp.status_code == 200:
        return resp.json()
    if resp.status_code == 429:
        print(f"  Rate limited on {path}, skipping")
        return None
    if resp.status_code in (401, 403):
        print(f"  Auth error on {path} ({resp.status_code}), try re-authorizing")
        return None
    # Some endpoints return empty for days without data
    if resp.status_code == 404:
        return None
    print(f"  API error {resp.status_code} on {path}: {resp.text[:200]}")
    return None


# ── Data Fetching ──────────────────────────────────────────────

def fetch_sleep(config: dict, date: str) -> dict | None:
    """Fetch sleep data for a date (YYYY-MM-DD)."""
    data = api_get(config, f"/1.2/user/-/sleep/date/{date}.json")
    if not data or not data.get("sleep"):
        return None

    # Find main sleep entry
    main = None
    for s in data["sleep"]:
        if s.get("isMainSleep"):
            main = s
            break
    if not main:
        main = data["sleep"][0]

    result = {
        "duration_ms": main.get("duration", 0),
        "efficiency": main.get("efficiency"),
        "start_time": main.get("startTime"),
        "end_time": main.get("endTime"),
    }

    # Sleep stages (if available)
    stages = main.get("levels", {}).get("summary", {})
    if stages:
        result["stages"] = {
            "deep": stages.get("deep", {}).get("minutes", 0),
            "light": stages.get("light", {}).get("minutes", 0),
            "rem": stages.get("rem", {}).get("minutes", 0),
            "wake": stages.get("wake", {}).get("minutes", 0),
        }

    # Sleep score from summary if available
    summary = data.get("summary", {})
    if "totalSleepScore" in summary:
        result["score"] = summary["totalSleepScore"]
    # Also check individual entry
    if "sleepScore" in main:
        result["score"] = main["sleepScore"]

    return result


def fetch_heart_rate(config: dict, date: str) -> dict | None:
    """Fetch heart rate data for a date."""
    data = api_get(config, f"/1/user/-/activities/heart/date/{date}/1d.json")
    if not data:
        return None

    hr_data = data.get("activities-heart", [{}])
    if not hr_data:
        return None

    value = hr_data[0].get("value", {})
    result = {}

    if "restingHeartRate" in value:
        result["resting_hr"] = value["restingHeartRate"]

    zones = value.get("heartRateZones", [])
    if zones:
        result["zones"] = [
            {"name": z["name"], "minutes": z.get("minutes", 0)}
            for z in zones
        ]

    return result if result else None


def fetch_activity(config: dict, date: str) -> dict | None:
    """Fetch activity summary for a date."""
    data = api_get(config, f"/1/user/-/activities/date/{date}.json")
    if not data or "summary" not in data:
        return None

    summary = data["summary"]
    return {
        "steps": summary.get("steps", 0),
        "active_minutes": (
            summary.get("fairlyActiveMinutes", 0)
            + summary.get("veryActiveMinutes", 0)
        ),
        "calories": summary.get("caloriesOut", 0),
        "floors": summary.get("floors", 0),
        "distance_km": round(sum(
            d.get("distance", 0) for d in summary.get("distances", [])
            if d.get("activity") == "total"
        ), 2),
    }


def fetch_spo2(config: dict, date: str) -> dict | None:
    """Fetch SpO2 data for a date."""
    data = api_get(config, f"/1/user/-/spo2/date/{date}.json")
    if not data:
        return None

    # Handle both single-day and range formats
    value = data.get("value")
    if not value:
        # Try minutes array format
        minutes = data.get("minutes", [])
        if minutes:
            values = [m["value"] for m in minutes if "value" in m]
            if values:
                return {
                    "avg": round(sum(values) / len(values), 1),
                    "min": min(values),
                    "max": max(values),
                }
        return None

    return {
        "avg": value.get("avg"),
        "min": value.get("min"),
        "max": value.get("max"),
    }


# ── Summary Generation ─────────────────────────────────────────

def format_duration(minutes: int) -> str:
    """Format minutes as 'Xh Ym'."""
    h, m = divmod(minutes, 60)
    if h > 0:
        return f"{h}h {m:02d}m"
    return f"{m}m"


def assess_sleep(score: int | None) -> str:
    if score is None:
        return "Unknown"
    if score >= 80:
        return "Good"
    if score >= 60:
        return "Concerning"
    return "Critical"


def assess_hr(resting: int, baseline: int) -> str:
    diff = resting - baseline
    if diff <= 10:
        return "Normal"
    if diff <= 20:
        return "Elevated"
    return "Concerning"


def assess_activity(steps: int) -> str:
    if steps >= 5000:
        return "Adequate"
    if steps >= 2000:
        return "Low"
    return "Sedentary"


def build_summary(
    date: str,
    sleep: dict | None,
    heart: dict | None,
    activity: dict | None,
    spo2: dict | None,
    baseline_hr: int,
    cycle_summary: str | None = None,
) -> str:
    """Build structured health summary for a given day."""
    dt = datetime.strptime(date, "%Y-%m-%d")
    day_name = dt.strftime("%A")
    header = f"Health data for {day_name}, {dt.strftime('%B %d, %Y').replace(' 0', ' ')}:"
    lines = [header]

    if sleep:
        lines.append("")
        lines.append("Sleep:")
        total_min = sleep["duration_ms"] // 60000
        stages = sleep.get("stages")
        if stages:
            asleep_min = stages["deep"] + stages["light"] + stages["rem"]
            lines.append(f"- Asleep: {format_duration(asleep_min)} (vs 8h goal)")
            lines.append(f"- Time in bed: {format_duration(total_min)}")
        else:
            lines.append(f"- Duration: {format_duration(total_min)} (vs 8h goal)")
        if "score" in sleep:
            lines.append(f"- Sleep score: {sleep['score']}/100")
        if sleep.get("efficiency") is not None:
            lines.append(f"- Efficiency: {sleep['efficiency']}%")
        if stages:
            lines.append(f"- Deep sleep: {format_duration(stages['deep'])}")
            lines.append(f"- Light sleep: {format_duration(stages['light'])}")
            lines.append(f"- REM: {format_duration(stages['rem'])}")
            lines.append(f"- Awake: {format_duration(stages['wake'])}")
        score = sleep.get("score") or sleep.get("efficiency")
        lines.append(f"- Assessment: {assess_sleep(score)}")

    if heart:
        lines.append("")
        lines.append("Heart Rate:")
        if "resting_hr" in heart:
            resting = heart["resting_hr"]
            lines.append(f"- Resting HR: {resting} bpm (baseline: ~{baseline_hr})")
            lines.append(f"- Assessment: {assess_hr(resting, baseline_hr)}")
        if "zones" in heart:
            for z in heart["zones"]:
                if z["minutes"] > 0:
                    lines.append(f"- {z['name']}: {z['minutes']} min")

    if activity:
        lines.append("")
        lines.append("Activity:")
        steps = activity["steps"]
        lines.append(f"- Steps: {steps:,}")
        lines.append(f"- Active minutes: {activity['active_minutes']}")
        if activity.get("floors"):
            lines.append(f"- Floors: {activity['floors']}")
        if activity.get("distance_km"):
            lines.append(f"- Distance: {activity['distance_km']} km")
        lines.append(f"- Assessment: {assess_activity(steps)}")

    if spo2 and spo2.get("avg") is not None:
        lines.append("")
        parts = []
        if spo2.get("avg") is not None:
            parts.append(f"avg {spo2['avg']}%")
        if spo2.get("min") is not None:
            parts.append(f"min {spo2['min']}%")
        if spo2.get("max") is not None:
            parts.append(f"max {spo2['max']}%")
        lines.append(f"SpO2: {', '.join(parts)}")

    if cycle_summary:
        lines.append("")
        lines.append(cycle_summary)

    # If we got no data at all, note it
    if not any([sleep, heart, activity, spo2]):
        lines.append("")
        lines.append("No data available for this day.")

    return "\n".join(lines)


# ── Embeddings ─────────────────────────────────────────────────

class EmbeddingService:
    """ONNX embedding service compatible with Recall C# server.

    Uses all-MiniLM-L6-v2 (384 dimensions). Serialization format is
    binary float32 array, compatible with C# Buffer.BlockCopy.
    """

    def __init__(self, model_dir: Path = MODEL_DIR):
        model_path = model_dir / "model.onnx"
        vocab_path = model_dir / "vocab.txt"

        if not model_path.exists() or not vocab_path.exists():
            raise FileNotFoundError(f"Model files not found in {model_dir}")

        self.session = ort.InferenceSession(
            str(model_path),
            providers=["CPUExecutionProvider"],
        )
        self.tokenizer = self._build_tokenizer(vocab_path)

    @staticmethod
    def _build_tokenizer(vocab_path: Path) -> Tokenizer:
        tokenizer = Tokenizer(models.WordPiece.from_file(str(vocab_path), unk_token="[UNK]"))
        tokenizer.normalizer = normalizers.BertNormalizer(lowercase=True)
        tokenizer.pre_tokenizer = pre_tokenizers.BertPreTokenizer()
        tokenizer.post_processor = processors.TemplateProcessing(
            single="[CLS] $A [SEP]",
            pair="[CLS] $A [SEP] $B:1 [SEP]:1",
            special_tokens=[("[CLS]", 101), ("[SEP]", 102)],
        )
        tokenizer.enable_truncation(max_length=256)
        tokenizer.enable_padding(length=256)
        return tokenizer

    def embed(self, text: str) -> np.ndarray:
        encoded = self.tokenizer.encode(text)
        input_ids = np.array([encoded.ids], dtype=np.int64)
        attention_mask = np.array([encoded.attention_mask], dtype=np.int64)
        token_type_ids = np.zeros_like(input_ids)

        outputs = self.session.run(None, {
            "input_ids": input_ids,
            "attention_mask": attention_mask,
            "token_type_ids": token_type_ids,
        })

        token_embeddings = outputs[0]  # (1, seq_len, 384)
        mask = attention_mask[:, :, np.newaxis].astype(np.float32)
        summed = (token_embeddings * mask).sum(axis=1)
        counts = mask.sum(axis=1).clip(min=1e-9)
        pooled = summed / counts

        norm = np.linalg.norm(pooled, axis=1, keepdims=True).clip(min=1e-9)
        normalized = pooled / norm

        return normalized[0]  # (384,)

    @staticmethod
    def serialize(embedding: np.ndarray) -> bytes:
        return struct.pack(f"{len(embedding)}f", *embedding)


# ── Database ───────────────────────────────────────────────────

def ensure_table(conn: sqlite3.Connection):
    """Create health_data table if it doesn't exist."""
    conn.execute("""
        CREATE TABLE IF NOT EXISTS health_data (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            date TEXT NOT NULL UNIQUE,
            summary TEXT NOT NULL,
            sleep_json TEXT,
            heart_json TEXT,
            activity_json TEXT,
            spo2_json TEXT,
            embedding BLOB,
            synced_at TEXT NOT NULL
        )
    """)
    conn.execute("""
        CREATE INDEX IF NOT EXISTS idx_health_date ON health_data(date DESC)
    """)
    conn.commit()


def write_health_entry(
    conn: sqlite3.Connection,
    date: str,
    summary: str,
    sleep: dict | None,
    heart: dict | None,
    activity: dict | None,
    spo2: dict | None,
    embedding: bytes | None,
):
    """Insert or replace health data for a date."""
    conn.execute("""
        INSERT OR REPLACE INTO health_data
            (date, summary, sleep_json, heart_json, activity_json, spo2_json, embedding, synced_at)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?)
    """, (
        date,
        summary,
        json.dumps(sleep) if sleep else None,
        json.dumps(heart) if heart else None,
        json.dumps(activity) if activity else None,
        json.dumps(spo2) if spo2 else None,
        embedding,
        datetime.now(tz=__import__('datetime').timezone.utc).isoformat(),
    ))
    conn.commit()


# ── Sync ───────────────────────────────────────────────────────

def sync_day(config: dict, conn: sqlite3.Connection, embedder: EmbeddingService | None, date: str):
    """Fetch and store health data for a single day."""
    print(f"  Syncing {date}...", end=" ", flush=True)

    sleep = fetch_sleep(config, date)
    heart = fetch_heart_rate(config, date)
    activity = fetch_activity(config, date)
    spo2 = fetch_spo2(config, date)

    if not any([sleep, heart, activity, spo2]):
        print("no data")
        return

    baseline_hr = config.get("resting_hr_baseline", 67)

    # Add cycle context if available
    cycle_summary = None
    try:
        from datetime import date as date_type
        cycle_summary = build_cycle_summary(conn, date_type.fromisoformat(date))
    except Exception:
        pass  # cycle data not imported yet, or table missing — silently skip

    summary = build_summary(date, sleep, heart, activity, spo2, baseline_hr, cycle_summary)

    embedding = None
    if embedder:
        try:
            embedding = EmbeddingService.serialize(embedder.embed(summary))
        except Exception as e:
            print(f"(embedding failed: {e})", end=" ")

    write_health_entry(conn, date, summary, sleep, heart, activity, spo2, embedding)
    print("ok")


def do_sync(config: dict, days: int):
    """Sync health data for the specified number of days."""
    # Initialize embedding service
    embedder = None
    try:
        embedder = EmbeddingService()
        print(f"Embedding model loaded from {MODEL_DIR}")
    except FileNotFoundError:
        print("Warning: Embedding model not found, syncing without embeddings")
    except Exception as e:
        print(f"Warning: Could not load embedding model: {e}")

    conn = sqlite3.connect(str(DB_PATH))
    ensure_table(conn)
    ensure_cycle_table(conn)

    today = datetime.now().date()
    print(f"Syncing {days} day(s) of Fitbit data...")

    for i in range(days):
        date = (today - timedelta(days=i)).isoformat()
        try:
            sync_day(config, conn, embedder, date)
        except Exception as e:
            print(f"  Error syncing {date}: {e}")

    conn.close()
    print("Done.")


# ── CLI ────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Fitbit Health Data Sync for Recall")
    sub = parser.add_subparsers(dest="command")

    sub.add_parser("auth", help="One-time OAuth2 authorization")

    sync_parser = sub.add_parser("sync", help="Sync health data")
    sync_parser.add_argument(
        "--days", type=int, default=2,
        help="Number of days to sync (default: 2 = today + yesterday)",
    )

    args = parser.parse_args()

    if not args.command:
        parser.print_help()
        sys.exit(1)

    config = load_config()

    if args.command == "auth":
        do_auth(config)
    elif args.command == "sync":
        do_sync(config, args.days)


if __name__ == "__main__":
    main()
