export type EventCategory =
  | "work"
  | "personal"
  | "health"
  | "meeting"
  | "focus"
  | "other";

export interface ParsedEvent {
  title: string;
  start_time: string;
  end_time: string;
  category: EventCategory;
  location?: string | null;
  description?: string | null;
  reminder_minutes?: number | null;
  has_conflict?: boolean;
  conflict_with?: string | null;
  conflict_warning?: string | null;
  suggested_start_time?: string | null;
  suggested_end_time?: string | null;
  suggested_time_label?: string | null;
}

export interface ScheduleResponse {
  events: ParsedEvent[];
  reference_time: string;
}

export interface SyncedEventResult {
  title: string;
  google_event_id: string;
  html_link?: string | null;
  start_time: string;
  end_time: string;
}

export interface CalendarSyncResponse {
  synced_count: number;
  synced_events: SyncedEventResult[];
}

export const API_BASE_URL =
  process.env.NEXT_PUBLIC_API_URL || "http://localhost:8000";

export async function checkAuthStatus(): Promise<boolean> {
  try {
    const res = await fetch(`${API_BASE_URL}/api/auth/status`, {
      cache: "no-store",
    });
    if (!res.ok) return false;
    const data = await res.json();
    return Boolean(data.authenticated);
  } catch {
    return false;
  }
}

export async function parseScheduleIntent(
  text: string,
  timezone = "Europe/Berlin"
): Promise<ScheduleResponse> {
  const res = await fetch(`${API_BASE_URL}/api/parse`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      text,
      timezone,
      current_time: new Date().toISOString(),
    }),
  });

  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    throw new Error(
      err.detail || `Fehler beim Parsen der Eingabe (Status ${res.status})`
    );
  }

  return res.json();
}

export async function syncEventsToCalendar(
  events: ParsedEvent[]
): Promise<CalendarSyncResponse> {
  const res = await fetch(`${API_BASE_URL}/api/calendar/sync`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      events,
      calendar_id: "primary",
    }),
  });

  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    throw new Error(
      err.detail ||
        `Fehler bei der Google Calendar Synchronisation (Status ${res.status})`
    );
  }

  return res.json();
}

