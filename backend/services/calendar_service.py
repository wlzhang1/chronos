from datetime import datetime
import os
from pathlib import Path
from typing import Optional

from google.auth.transport.requests import Request
from google.oauth2.credentials import Credentials
from google_auth_oauthlib.flow import Flow
from googleapiclient.discovery import build

from backend.core.config import settings
from backend.models.schemas import EventCategory, ParsedEvent, SyncedEventResult

# Erlaubt OAuth2 über http://localhost für die lokale Entwicklung
os.environ["OAUTHLIB_INSECURE_TRANSPORT"] = "1"

SCOPES = ["https://www.googleapis.com/auth/calendar.events"]
TOKEN_PATH = Path(__file__).resolve().parent.parent.parent / "token.json"

# Google Calendar Event Color IDs (1-11) passend zu den Chronos-Kategorien
CATEGORY_COLOR_MAP: dict[EventCategory, str] = {
    EventCategory.WORK: "9",      # Blueberry (Blau)
    EventCategory.PERSONAL: "2",  # Sage (Grün)
    EventCategory.HEALTH: "10",   # Basil (Dunkelgrün)
    EventCategory.MEETING: "6",   # Tangerine (Orange)
    EventCategory.FOCUS: "3",     # Grape (Violett)
    EventCategory.OTHER: "8",     # Graphite (Grau)
}

# Temporärer In-Memory-Speicher für den PKCE code_verifier zwischen /login und /callback
_oauth_state_store: dict[str, Optional[str]] = {}


def _get_client_config() -> dict:
    if not settings.GOOGLE_CLIENT_ID or not settings.GOOGLE_CLIENT_SECRET:
        raise ValueError(
            "GOOGLE_CLIENT_ID oder GOOGLE_CLIENT_SECRET fehlt in der .env-Datei."
        )
    return {
        "web": {
            "client_id": settings.GOOGLE_CLIENT_ID,
            "client_secret": settings.GOOGLE_CLIENT_SECRET,
            "auth_uri": "https://accounts.google.com/o/oauth2/auth",
            "token_uri": "https://oauth2.googleapis.com/token",
            "redirect_uris": [settings.GOOGLE_REDIRECT_URI],
        }
    }


def get_authorization_url() -> tuple[str, str]:
    """Erzeugt die Google OAuth2 Login-URL."""
    flow = Flow.from_client_config(
        _get_client_config(),
        scopes=SCOPES,
        redirect_uri=settings.GOOGLE_REDIRECT_URI,
    )
    authorization_url, state = flow.authorization_url(
        access_type="offline",
        include_granted_scopes="true",
        prompt="consent",
    )
    _oauth_state_store[state] = getattr(flow, "code_verifier", None)
    return authorization_url, state


def exchange_code_for_token(code: str, state: Optional[str] = None) -> Credentials:
    """Tauscht den OAuth2 Authorization Code gegen Access/Refresh-Tokens und speichert sie lokal."""
    flow = Flow.from_client_config(
        _get_client_config(),
        scopes=SCOPES,
        state=state,
        redirect_uri=settings.GOOGLE_REDIRECT_URI,
    )
    if state and state in _oauth_state_store:
        flow.code_verifier = _oauth_state_store.pop(state)

    flow.fetch_token(code=code)
    creds = flow.credentials
    TOKEN_PATH.write_text(creds.to_json(), encoding="utf-8")
    return creds


def get_valid_credentials() -> Optional[Credentials]:
    """Lädt gespeicherte Credentials aus token.json und erneuert sie bei Bedarf automatisch."""
    if not TOKEN_PATH.exists():
        return None

    creds = Credentials.from_authorized_user_file(str(TOKEN_PATH), SCOPES)
    if creds and creds.expired and creds.refresh_token:
        creds.refresh(Request())
        TOKEN_PATH.write_text(creds.to_json(), encoding="utf-8")

    if not creds or not creds.valid:
        return None

    return creds


def _normalize_iso_datetime(dt_str: str) -> str:
    """Stellt sicher, dass der Zeitstempel ein gültiger RFC3339/ISO-8601 String mit Zeitzonen-Offset für Google Calendar ist."""
    cleaned = dt_str.strip()
    if cleaned.endswith("Z"):
        cleaned = cleaned[:-1] + "+00:00"
    try:
        dt = datetime.fromisoformat(cleaned)
    except ValueError as exc:
        raise ValueError(
            f"Ungültiges Datumsformat '{dt_str}'. Erwartet wird ISO-8601 (z. B. '2026-10-28T10:00:00+01:00')."
        ) from exc

    if dt.tzinfo is None:
        local_tz = datetime.now().astimezone().tzinfo
        dt = dt.replace(tzinfo=local_tz)

    return dt.isoformat()


def sync_events_to_calendar(
    events: list[ParsedEvent], calendar_id: str = "primary"
) -> list[SyncedEventResult]:
    """Schreibt eine Liste von ParsedEvents in den angegebenen Google Kalender."""
    creds = get_valid_credentials()
    if not creds:
        raise PermissionError(
            "Nicht mit Google Calendar verbunden. Bitte zuerst über /api/auth/login authentifizieren."
        )

    service = build("calendar", "v3", credentials=creds)
    results: list[SyncedEventResult] = []

    for event in events:
        start_iso = _normalize_iso_datetime(event.start_time)
        end_iso = _normalize_iso_datetime(event.end_time)

        event_body: dict = {
            "summary": event.title,
            "description": event.description or f"Erstellt mit Chronos ({event.category.value})",
            "start": {"dateTime": start_iso},
            "end": {"dateTime": end_iso},
            "colorId": CATEGORY_COLOR_MAP.get(event.category, "8"),
        }

        created = (
            service.events()
            .insert(calendarId=calendar_id, body=event_body)
            .execute()
        )

        results.append(
            SyncedEventResult(
                title=event.title,
                google_event_id=created.get("id", ""),
                html_link=created.get("htmlLink"),
                start_time=event.start_time,
                end_time=event.end_time,
            )
        )

    return results

