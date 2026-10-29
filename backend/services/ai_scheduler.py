from datetime import datetime
from google import genai
from google.genai import types

from typing import Optional
from pydantic import BaseModel, Field

from backend.core.config import settings
from backend.models.schemas import EventCategory, ExistingCalendarEvent, ParsedEvent, ParsedEventList


class AIExtractedEvent(BaseModel):
    title: str = Field(..., description="Kurzer, prägnanter Titel des Termins")
    start_time: str = Field(
        ...,
        description="Startzeitpunkt im ISO-8601-Format (z. B. 2026-10-28T09:00:00+01:00)",
    )
    end_time: str = Field(
        ...,
        description="Endzeitpunkt im ISO-8601-Format (z. B. 2026-10-28T10:00:00+01:00)",
    )
    category: EventCategory = Field(
        default=EventCategory.OTHER,
        description="Kategorie des Ereignisses (work, personal, health, meeting, focus, other)",
    )
    location: Optional[str] = Field(
        default=None,
        description="Optionaler Ort, Treffpunkt oder Adresse des Termins",
    )
    description: Optional[str] = Field(
        default=None,
        description="Optionale Zusatzinformationen, Notizen oder Kontext zum Termin",
    )
    reminder_minutes: Optional[int] = Field(
        default=None,
        description="Erinnerungsvorlauf in Minuten vor dem Termin, falls explizit gewünscht. Standardmäßig null.",
    )


class AIExtractedEventList(BaseModel):
    events: list[AIExtractedEvent] = Field(default_factory=list)

SYSTEM_INSTRUCTION = """Du bist Chronos, ein präziser KI-Assistent für Zeitmanagement und Tagesplanung.
Deine Aufgabe ist es, unstrukturierte Texteingaben in strukturierte Kalenderereignisse zu übersetzen.

Regeln:
1. Nutze die übergebene Referenzzeit (`current_time`), um relative Zeitangaben wie "heute", "morgen", "in zwei Stunden" oder "nächsten Montag" exakt aufzulösen.
2. WICHTIG ZUR ZEITZONE: Wenn der Nutzer eine Uhrzeit nennt (z. B. "7:30 Uhr", "9 Uhr", "20:45 Uhr"), ist das IMMER die lokale Uhrzeit des Nutzers. Übernimm diese Uhrzeit 1:1 (z. B. `T07:30:00`, `T09:00:00`, `T20:45:00`) und hänge exakt denselben Zeitzonen-Offset an, den `current_time` besitzt (z. B. `+01:00` oder `+02:00`). Rechne die Zeiten NIEMALS in UTC (`Z` oder `+00:00`) um!
3. Wenn keine explizite Dauer oder Endzeit genannt wird, wähle eine sinnvolle Standarddauer (z. B. 15 Minuten für kurze Reminder/Nachrichten, 30 Minuten für kurze Calls/Syncs, 60 Minuten für allgemeine Termine, 120 Minuten für ein Fußballspiel).
4. Ordne jedem Termin die passendste Kategorie (`work`, `personal`, `health`, `meeting`, `focus`, `other`) zu.
5. ORT (`location`): Wenn im Text ein Ort, Restaurant, Gebäude, Raum, eine Stadt oder Adresse genannt wird (z. B. "Weihnachtsfeier im Paulaner am Nockherberg" -> `location`: "Paulaner am Nockherberg", "Joggen im Park" -> `location`: "Park"), trage diesen zwingend in das Feld `location` ein. Falls kein Ort genannt wird, lasse `location` auf `null`.
6. BESCHREIBUNG (`description`): Extrahiere sonstige Zusatzdetails (Teilnehmer, Themen, Aufgaben, Notizen) in das Feld `description`.
7. ERINNERUNGEN / BENACHRICHTIGUNGEN (`reminder_minutes`):
   - STANDARDEINSTELLUNG: Setze `reminder_minutes` standardmäßig IMMER auf `null` (keine Benachrichtigung), auch wenn das Wort "Reminder" nur für einen kurzen Kalendereintrag genutzt wird.
   - NUR wenn der Nutzer explizit sagt, dass er eine Benachrichtigung oder Erinnerung für den Termin haben möchte (z. B. "erinnere mich 15 Minuten vorher", "mit Erinnerung", "benachrichtige mich vorher", "erinnere mich daran"), setze `reminder_minutes` auf die gewünschte Minutenanzahl vor dem Termin (z. B. `15`, `30`, `60`).
   - Falls der Nutzer explizit eine Erinnerung wünscht, aber keine genaue Zeitspanne nennt (z. B. "mit Erinnerung" oder "erinnere mich daran"), nutze `10` (10 Minuten vorher) als Standardwert.
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
                    response_schema=AIExtractedEventList,
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

    extracted_events: list[AIExtractedEvent] = []
    if response.parsed is not None and isinstance(response.parsed, AIExtractedEventList):
        extracted_events = response.parsed.events
    elif response.text:
        parsed_list = AIExtractedEventList.model_validate_json(response.text)
        extracted_events = parsed_list.events

    events: list[ParsedEvent] = []
    for ev in extracted_events:
        events.append(
            ParsedEvent(
                title=ev.title,
                start_time=_enforce_local_tz(ev.start_time, current_time.tzinfo),
                end_time=_enforce_local_tz(ev.end_time, current_time.tzinfo),
                category=ev.category,
                location=ev.location,
                description=ev.description,
                reminder_minutes=ev.reminder_minutes,
            )
        )

    return events

