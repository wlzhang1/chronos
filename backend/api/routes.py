from datetime import datetime, timezone
from typing import Optional
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError
from fastapi import APIRouter, HTTPException, Query, status
from fastapi.responses import HTMLResponse, RedirectResponse

from backend.core.config import settings
from backend.models.schemas import (
    CalendarSyncRequest,
    CalendarSyncResponse,
    ScheduleRequest,
    ScheduleResponse,
)
from backend.services.ai_scheduler import parse_schedule_intent
from backend.services.calendar_service import (
    exchange_code_for_token,
    get_authorization_url,
    get_valid_credentials,
    sync_events_to_calendar,
)

router = APIRouter(prefix="/api", tags=["scheduler"])


def _resolve_timezone(tz_name: str):
    """Löst eine IANA-Zeitzone auf und fällt unter Windows ohne tzdata sicher auf die lokale Systemzeitzone zurück."""
    try:
        return ZoneInfo(tz_name)
    except ZoneInfoNotFoundError:
        return datetime.now().astimezone().tzinfo or timezone.utc


@router.post("/parse", response_model=ScheduleResponse)
async def parse_schedule(request: ScheduleRequest) -> ScheduleResponse:
    """Nimmt unstrukturierten Text entgegen und gibt strukturierte Kalenderereignisse zurück."""
    tz = _resolve_timezone(request.timezone)

    if request.current_time is not None:
        if request.current_time.tzinfo is None:
            reference_time = request.current_time.replace(tzinfo=tz)
        else:
            # Wichtig: Browser-ISO-Strings sind meist UTC ('Z'). In lokale Nutzer-Zeitzone umrechnen!
            reference_time = request.current_time.astimezone(tz)
    else:
        reference_time = datetime.now(tz)

    try:
        events = parse_schedule_intent(text=request.text, current_time=reference_time)
    except ValueError as exc:
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail=str(exc),
        ) from exc
    except Exception as exc:
        raise HTTPException(
            status_code=status.HTTP_502_BAD_GATEWAY,
            detail=f"Fehler bei der KI-Verarbeitung: {exc}",
        ) from exc

    return ScheduleResponse(events=events, reference_time=reference_time)


@router.get("/auth/status", tags=["auth"])
async def auth_status() -> dict[str, bool]:
    """Prüft, ob gültige Google Calendar OAuth-Credentials vorliegen."""
    creds = get_valid_credentials()
    return {"authenticated": creds is not None}


@router.get("/auth/login", tags=["auth"])
async def auth_login(redirect: bool = Query(default=True, description="Direkt zu Google weiterleiten")):
    """Startet den Google OAuth2-Flow. Leitet direkt weiter oder gibt die Auth-URL als JSON zurück."""
    try:
        auth_url, state = get_authorization_url()
    except ValueError as exc:
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail=str(exc),
        ) from exc

    if redirect:
        return RedirectResponse(url=auth_url)
    return {"authorization_url": auth_url, "state": state}


@router.get("/auth/callback", tags=["auth"], response_class=HTMLResponse)
async def auth_callback(code: str, state: Optional[str] = None) -> HTMLResponse:
    """OAuth2 Callback-Endpunkt, den Google nach erfolgreichem Login aufruft."""
    try:
        exchange_code_for_token(code=code, state=state)
    except Exception as exc:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"OAuth2 Token-Austausch fehlgeschlagen: {exc}",
        ) from exc

    return HTMLResponse(
        content=f"""
        <html>
            <head><title>Chronos - Google Calendar verbunden</title></head>
            <body style="font-family: system-ui, sans-serif; max-width: 520px; margin: 60px auto; text-align: center; background: #09090b; color: #f4f4f5; padding: 24px;">
                <div style="background: #18181b; border: 1px solid #27272a; border-radius: 16px; padding: 32px;">
                    <h2 style="color: #4ade80; margin-top: 0;">✅ Google Calendar verbunden!</h2>
                    <p style="color: #a1a1aa; line-height: 1.6;">Dein OAuth-Token wurde erfolgreich gespeichert.</p>
                    <div style="margin-top: 24px; display: flex; gap: 12px; justify-content: center;">
                        <a href="{settings.FRONTEND_URL}" style="background: #6366f1; color: white; text-decoration: none; padding: 10px 18px; border-radius: 8px; font-weight: 500;">Zurück zur Chronos App</a>
                        <a href="/docs" style="background: #27272a; color: #e4e4e7; text-decoration: none; padding: 10px 18px; border-radius: 8px; font-weight: 500;">Swagger UI</a>
                    </div>
                </div>
            </body>
        </html>
        """
    )


@router.post("/calendar/sync", response_model=CalendarSyncResponse, tags=["calendar"])
async def sync_calendar(request: CalendarSyncRequest) -> CalendarSyncResponse:
    """Synchronisiert eine Liste von ParsedEvents mit Google Calendar."""
    try:
        synced = sync_events_to_calendar(
            events=request.events, calendar_id=request.calendar_id
        )
    except PermissionError as exc:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail=str(exc),
        ) from exc
    except ValueError as exc:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=str(exc),
        ) from exc
    except Exception as exc:
        raise HTTPException(
            status_code=status.HTTP_502_BAD_GATEWAY,
            detail=f"Fehler bei der Google Calendar Synchronisation: {exc}",
        ) from exc

    return CalendarSyncResponse(synced_count=len(synced), synced_events=synced)


