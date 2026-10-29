from datetime import datetime, time, timedelta
import os
from pathlib import Path
from typing import Optional

from google.auth.transport.requests import Request
from google.oauth2.credentials import Credentials
from google_auth_oauthlib.flow import Flow
from googleapiclient.discovery import build

from backend.core.config import settings
from backend.models.schemas import (
    EventCategory,
    ExistingCalendarEvent,
    ParsedEvent,
    SyncedEventResult,
)

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


def _parse_iso_to_dt(dt_str: str) -> datetime:
    """Konvertiert einen ISO-8601-String zuverlässig in ein zeitzonenbewusstes datetime-Objekt."""
    norm = _normalize_iso_datetime(dt_str)
    return datetime.fromisoformat(norm)


def get_events_in_range(
    time_min: datetime, time_max: datetime, calendar_id: str = "primary"
) -> list[ExistingCalendarEvent]:
    """Liest bestehende Termine aus dem Google Kalender für den angegebenen Zeitraum (Zwei-Wege-Sync)."""
    creds = get_valid_credentials()
    if not creds:
        return []

    try:
        service = build("calendar", "v3", credentials=creds)
        events_result = (
            service.events()
            .list(
                calendarId=calendar_id,
                timeMin=time_min.isoformat(),
                timeMax=time_max.isoformat(),
                singleEvents=True,
                orderBy="startTime",
                maxResults=250,
            )
            .execute()
        )
    except Exception:
        return []

    items = events_result.get("items", [])
    existing: list[ExistingCalendarEvent] = []

    for item in items:
        if item.get("status") == "cancelled":
            continue
        # Transparente Termine ("Frei" statt "Beschäftigt") ignorieren
        if item.get("transparency") == "transparent":
            continue

        start_obj = item.get("start", {})
        end_obj = item.get("end", {})

        start_dt_str = start_obj.get("dateTime")
        end_dt_str = end_obj.get("dateTime")

        if not start_dt_str or not end_dt_str:
            # Ganztägige Termine (nur "date") blockieren keine spezifischen Uhrzeit-Slots
            continue

        existing.append(
            ExistingCalendarEvent(
                title=item.get("summary") or "Unbenannter Termin",
                start_time=_normalize_iso_datetime(start_dt_str),
                end_time=_normalize_iso_datetime(end_dt_str),
                is_all_day=False,
            )
        )

    return existing


def _round_up_to_quarter_hour(dt: datetime) -> datetime:
    """Rundet einen Zeitpunkt auf die nächste glatte Viertelstunde auf (falls nicht bereits glatt)."""
    remainder = dt.minute % 15
    if remainder == 0 and dt.second == 0 and dt.microsecond == 0:
        return dt
    add_mins = 15 - remainder if remainder != 0 else 0
    rounded = dt.replace(second=0, microsecond=0) + timedelta(minutes=add_mins)
    return rounded


def enrich_events_with_conflict_detection(
    events: list[ParsedEvent], calendar_id: str = "primary"
) -> list[ParsedEvent]:
    """
    Prüft alle geparsten Termine gegen bestehende Termine im Google Kalender (sowie untereinander).
    Bei einer Überschneidung wird ein Warnhinweis samt freiem Ausweich-Slot generiert.
    """
    if not events:
        return events

    parsed_intervals: list[tuple[datetime, datetime]] = []
    for ev in events:
        try:
            s_dt = _parse_iso_to_dt(ev.start_time)
            e_dt = _parse_iso_to_dt(ev.end_time)
            if e_dt <= s_dt:
                e_dt = s_dt + timedelta(minutes=30)
            parsed_intervals.append((s_dt, e_dt))
        except Exception:
            now = datetime.now().astimezone()
            parsed_intervals.append((now, now + timedelta(minutes=60)))

    min_dt = min(iv[0] for iv in parsed_intervals)
    max_dt = max(iv[1] for iv in parsed_intervals)

    # Gesamte betroffene Tage von 00:00 bis 23:59:59 abfragen
    query_start = datetime.combine(min_dt.date(), time(0, 0, 0), tzinfo=min_dt.tzinfo)
    query_end = datetime.combine(max_dt.date(), time(23, 59, 59), tzinfo=max_dt.tzinfo)

    existing_events = get_events_in_range(query_start, query_end, calendar_id=calendar_id)

    busy_slots: list[tuple[datetime, datetime, str]] = []
    for ex in existing_events:
        try:
            ex_s = _parse_iso_to_dt(ex.start_time)
            ex_e = _parse_iso_to_dt(ex.end_time)
            if ex_e > ex_s:
                busy_slots.append((ex_s, ex_e, ex.title))
        except Exception:
            continue

    for idx, ev in enumerate(events):
        s_dt, e_dt = parsed_intervals[idx]
        duration = e_dt - s_dt

        overlapping = [
            (b_s, b_e, b_title)
            for (b_s, b_e, b_title) in busy_slots
            if s_dt < b_e and e_dt > b_s
        ]

        if not overlapping:
            ev.has_conflict = False
            ev.conflict_with = None
            ev.conflict_warning = None
            ev.suggested_start_time = None
            ev.suggested_end_time = None
            ev.suggested_time_label = None
            # Diesen Termin ebenfalls als belegt vormerken für nachfolgende Termine im selben Batch
            busy_slots.append((s_dt, e_dt, ev.title))
            continue

        # Konflikt gefunden!
        ev.has_conflict = True
        first_b_s, first_b_e, first_b_title = overlapping[0]
        conflict_names = ", ".join(f"'{b[2]}'" for b in overlapping)
        conflict_details = ", ".join(
            f"'{b[2]}' ({b[0].strftime('%H:%M')}–{b[1].strftime('%H:%M')} Uhr)"
            for b in overlapping
        )
        ev.conflict_with = conflict_details

        # Nächsten freien Slot für dieselbe Dauer suchen (ab Ende des blockierenden Termins)
        latest_overlap_end = max(b[1] for b in overlapping)
        candidate_start = _round_up_to_quarter_hour(latest_overlap_end)

        # Maximal 48 Schritte (12 Stunden) vorwärts prüfen
        for _ in range(48):
            candidate_end = candidate_start + duration
            has_cand_overlap = False
            next_jump = candidate_start + timedelta(minutes=15)
            for b_s, b_e, _ in busy_slots:
                if candidate_start < b_e and candidate_end > b_s:
                    has_cand_overlap = True
                    next_jump = _round_up_to_quarter_hour(b_e)
                    break
            if not has_cand_overlap:
                break
            candidate_start = max(candidate_start + timedelta(minutes=15), next_jump)

        candidate_end = candidate_start + duration
        ev.suggested_start_time = candidate_start.isoformat()
        ev.suggested_end_time = candidate_end.isoformat()

        sug_start_str = candidate_start.strftime("%H:%M")
        sug_end_str = candidate_end.strftime("%H:%M")
        req_start_str = s_dt.strftime("%H:%M")

        if candidate_start.date() == s_dt.date():
            ev.suggested_time_label = f"{sug_start_str} – {sug_end_str} Uhr"
            sug_question = f"auf {sug_start_str} Uhr legen"
        else:
            date_fmt = candidate_start.strftime("%d.%m.")
            ev.suggested_time_label = f"{date_fmt}, {sug_start_str} – {sug_end_str} Uhr"
            sug_question = f"auf {date_fmt} um {sug_start_str} Uhr legen"

        ev.conflict_warning = (
            f"Achtung: Um {req_start_str} Uhr hast du bereits {conflict_names} "
            f"({first_b_s.strftime('%H:%M')}–{first_b_e.strftime('%H:%M')} Uhr) im Kalender. "
            f"Soll ich '{ev.title}' {sug_question}?"
        )

    return events


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

        if event.reminder_minutes is not None and event.reminder_minutes > 0:
            reminders_config = {
                "useDefault": False,
                "overrides": [
                    {"method": "popup", "minutes": event.reminder_minutes}
                ],
            }
        else:
            reminders_config = {
                "useDefault": False,
                "overrides": [],
            }

        event_body: dict = {
            "summary": event.title,
            "description": event.description or f"Erstellt mit Chronos ({event.category.value})",
            "start": {"dateTime": start_iso},
            "end": {"dateTime": end_iso},
            "colorId": CATEGORY_COLOR_MAP.get(event.category, "8"),
            "reminders": reminders_config,
        }
        if event.location and event.location.strip():
            event_body["location"] = event.location.strip()

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

