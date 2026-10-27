from datetime import datetime
from google import genai
from google.genai import types

from backend.core.config import settings
from backend.models.schemas import ParsedEvent, ParsedEventList

SYSTEM_INSTRUCTION = """Du bist Chronos, ein präziser KI-Assistent für Zeitmanagement und Tagesplanung.
Deine Aufgabe ist es, unstrukturierte Texteingaben in strukturierte Kalenderereignisse zu übersetzen.

Regeln:
1. Nutze die übergebene Referenzzeit (`current_time`), um relative Zeitangaben wie "heute", "morgen", "in zwei Stunden" oder "nächsten Montag" exakt aufzulösen.
2. WICHTIG ZUR ZEITZONE: Wenn der Nutzer eine Uhrzeit nennt (z. B. "7:30 Uhr", "9 Uhr", "20:45 Uhr"), ist das IMMER die lokale Uhrzeit des Nutzers. Übernimm diese Uhrzeit 1:1 (z. B. `T07:30:00`, `T09:00:00`, `T20:45:00`) und hänge exakt denselben Zeitzonen-Offset an, den `current_time` besitzt (z. B. `+01:00` oder `+02:00`). Rechne die Zeiten NIEMALS in UTC (`Z` oder `+00:00`) um!
3. Wenn keine explizite Dauer oder Endzeit genannt wird, wähle eine sinnvolle Standarddauer (z. B. 15 Minuten für kurze Reminder/Nachrichten, 30 Minuten für kurze Calls/Syncs, 60 Minuten für allgemeine Termine, 120 Minuten für ein Fußballspiel).
4. Ordne jedem Termin die passendste Kategorie (`work`, `personal`, `health`, `meeting`, `focus`, `other`) zu.
5. Extrahiere zusätzliche Details (Teilnehmer, Ort, Aufgaben) in das Feld `description`.
"""


def _create_genai_client() -> genai.Client:
    """Erstellt den Google GenAI Client für Google AI Studio."""
    api_key = settings.GEMINI_API_KEY.strip()
    if not api_key:
        raise ValueError(
            "GEMINI_API_KEY ist nicht gesetzt. Bitte in der .env-Datei konfigurieren."
        )

    # Direkt Google AI Studio nutzen (100% Free Tier)
    return genai.Client(api_key=api_key)


def _enforce_local_tz(iso_str: str, target_tz) -> str:
    """Stellt sicher, dass die von der KI genannte Uhrzeit (z. B. 07:30) exakt mit dem lokalen Zeitzonen-Offset (+01:00/+02:00) versehen wird."""
    if not target_tz:
        return iso_str
    cleaned = iso_str.strip()
    if cleaned.endswith("Z"):
        cleaned = cleaned[:-1] + "+00:00"
    try:
        dt = datetime.fromisoformat(cleaned)
        # Uhrzeit (Stunde/Minute) beibehalten und lokalen Offset setzen
        dt_local = dt.replace(tzinfo=target_tz)
        return dt_local.isoformat()
    except ValueError:
        return iso_str


def parse_schedule_intent(text: str, current_time: datetime) -> list[ParsedEvent]:
    """Übersetzt unstrukturierte Texteingaben per Google GenAI SDK in eine Liste von ParsedEvent-Objekten."""
    client = _create_genai_client()

    tz_offset = current_time.strftime("%z")
    tz_offset_formatted = (
        f"{tz_offset[:3]}:{tz_offset[3:]}" if len(tz_offset) >= 5 else "+01:00"
    )

    prompt = (
        f"Lokale Referenzzeit (current_time): {current_time.isoformat()} "
        f"(Wochentag: {current_time.strftime('%A')}, Pflicht-Offset für alle Ausgaben: {tz_offset_formatted})\n\n"
        f"Nutzereingabe:\n{text}"
    )

    fallback_models = [
        settings.GEMINI_MODEL,
        "gemini-2.5-flash",
        "gemini-2.5-flash-lite",
        "gemini-3-flash-preview",
        "gemini-2.0-flash",
    ]
    # Duplikate entfernen unter Beibehaltung der Reihenfolge
    models_to_try = list(dict.fromkeys(fallback_models))

    last_error = None
    response = None
    for model_name in models_to_try:
        try:
            response = client.models.generate_content(
                model=model_name,
                contents=prompt,
                config=types.GenerateContentConfig(
                    system_instruction=SYSTEM_INSTRUCTION,
                    response_mime_type="application/json",
                    response_schema=ParsedEventList,
                    temperature=0.1,
                ),
            )
            break
        except Exception as exc:
            last_error = exc
            if "404" in str(exc) or "NOT_FOUND" in str(exc):
                continue
            raise

    if response is None and last_error is not None:
        raise last_error

    events: list[ParsedEvent] = []
    if response.parsed is not None and isinstance(response.parsed, ParsedEventList):
        events = response.parsed.events
    elif response.text:
        parsed_list = ParsedEventList.model_validate_json(response.text)
        events = parsed_list.events

    for ev in events:
        ev.start_time = _enforce_local_tz(ev.start_time, current_time.tzinfo)
        ev.end_time = _enforce_local_tz(ev.end_time, current_time.tzinfo)

    return events

