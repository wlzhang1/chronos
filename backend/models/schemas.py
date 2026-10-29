from datetime import datetime
from enum import Enum
from typing import Optional
from pydantic import BaseModel, ConfigDict, Field


class EventCategory(str, Enum):
    WORK = "work"
    PERSONAL = "personal"
    HEALTH = "health"
    MEETING = "meeting"
    FOCUS = "focus"
    OTHER = "other"


class ParsedEvent(BaseModel):
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
        description="Optionaler Ort, Treffpunkt oder Adresse des Termins (z. B. 'Paulaner am Nockherberg', 'Im Park', 'Raum 302')",
    )
    description: Optional[str] = Field(
        default=None,
        description="Optionale Zusatzinformationen, Notizen oder Kontext zum Termin",
    )
    reminder_minutes: Optional[int] = Field(
        default=None,
        description="Erinnerungsvorlauf in Minuten vor dem Termin, falls der Nutzer explizit eine Benachrichtigung/Erinnerung wünscht (z. B. 10, 15, 30, 60). Standardmäßig null (keine Benachrichtigung).",
    )
    has_conflict: bool = Field(
        default=False,
        description="Gibt an, ob der Termin sich zeitlich mit einem bestehenden Kalendereintrag überschneidet.",
    )
    conflict_with: Optional[str] = Field(
        default=None,
        description="Titel und Zeitraum des bestehenden Termins bei einer Überschneidung (z. B. \"'Zahnarzt' (14:00–15:00 Uhr)\").",
    )
    conflict_warning: Optional[str] = Field(
        default=None,
        description="Freundlicher Warnhinweis mit konkretem Ausweichvorschlag.",
    )
    suggested_start_time: Optional[str] = Field(
        default=None,
        description="Vorgeschlagener freier Startzeitpunkt im ISO-8601-Format zur Konfliktlösung.",
    )
    suggested_end_time: Optional[str] = Field(
        default=None,
        description="Vorgeschlagener freier Endzeitpunkt im ISO-8601-Format zur Konfliktlösung.",
    )
    suggested_time_label: Optional[str] = Field(
        default=None,
        description="Lesbares Label für den Ausweichvorschlag (z. B. '15:00 – 15:30 Uhr').",
    )


class ExistingCalendarEvent(BaseModel):
    title: str
    start_time: str
    end_time: str
    is_all_day: bool = False


class ParsedEventList(BaseModel):
    events: list[ParsedEvent] = Field(
        default_factory=list,
        description="Liste der aus dem Text extrahierten Kalenderereignisse",
    )


class ScheduleRequest(BaseModel):
    text: str = Field(
        ...,
        min_length=1,
        description="Unstrukturierte Texteingabe des Nutzers für die Tagesplanung",
        examples=["Morgen von 10 bis 11 Uhr Team-Sync und danach 1 Stunde Deep Work am Backend"],
    )
    current_time: Optional[datetime] = Field(
        default=None,
        description="Optionaler Referenzzeitpunkt (ISO-8601). Falls leer, wird die aktuelle Serverzeit genutzt.",
    )
    timezone: str = Field(
        default="Europe/Berlin",
        description="Zeitzone des Nutzers für relative Zeitangaben",
    )


class ScheduleResponse(BaseModel):
    events: list[ParsedEvent]
    reference_time: datetime


class CalendarSyncRequest(BaseModel):
    model_config = ConfigDict(
        json_schema_extra={
            "example": {
                "events": [
                    {
                        "title": "Chronos Test-Sync",
                        "start_time": "2026-10-28T10:00:00+01:00",
                        "end_time": "2026-10-28T11:00:00+01:00",
                        "category": "focus",
                        "description": "Erster automatischer Kalendereintrag über Chronos",
                    }
                ],
                "calendar_id": "primary",
            }
        }
    )

    events: list[ParsedEvent] = Field(
        ...,
        min_length=1,
        description="Liste der geparsten Ereignisse, die in Google Calendar eingetragen werden sollen",
    )
    calendar_id: str = Field(
        default="primary",
        description="Ziel-Kalender-ID (Standard: 'primary' für den Hauptkalender)",
    )


class SyncedEventResult(BaseModel):
    title: str
    google_event_id: str
    html_link: Optional[str] = None
    start_time: str
    end_time: str


class CalendarSyncResponse(BaseModel):
    synced_count: int
    synced_events: list[SyncedEventResult]


class QuickScheduleRequest(BaseModel):
    text: str = Field(..., min_length=1, description="Nutzereingabe für den Schnelltermin")
    timezone: str = Field(default="Europe/Berlin", description="Zeitzone des Nutzers")
    force_sync: bool = Field(
        default=False,
        description="Falls True, werden Termine trotz Konflikt eingetragen.",
    )
    use_suggested_slots: bool = Field(
        default=False,
        description="Falls True, werden bei Konflikten automatisch die vorgeschlagenen Ausweichzeiten genutzt.",
    )


class QuickScheduleResponse(BaseModel):
    success: bool
    has_conflict: bool = False
    message: str
    events: list[SyncedEventResult] = Field(default_factory=list)
    parsed_events: list[ParsedEvent] = Field(default_factory=list)


