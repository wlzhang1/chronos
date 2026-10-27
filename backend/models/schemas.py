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
    description: Optional[str] = Field(
        default=None,
        description="Optionale Zusatzinformationen, Notizen oder Kontext zum Termin",
    )


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


