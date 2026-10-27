"use client";

import React, { useEffect, useState } from "react";
import {
  Calendar,
  CheckCircle2,
  Clock,
  ExternalLink,
  Loader2,
  Plus,
  Sparkles,
  Trash2,
  AlertCircle,
  Link2,
  Briefcase,
  User,
  HeartPulse,
  Users,
  Brain,
  Tag,
} from "lucide-react";
import {
  API_BASE_URL,
  checkAuthStatus,
  EventCategory,
  ParsedEvent,
  parseScheduleIntent,
  SyncedEventResult,
  syncEventsToCalendar,
} from "@/lib/api";

const CATEGORY_CONFIG: Record<
  EventCategory,
  { label: string; badgeClass: string; icon: React.ReactNode }
> = {
  work: {
    label: "Work",
    badgeClass: "bg-blue-500/15 text-blue-300 border-blue-500/30",
    icon: <Briefcase className="w-3.5 h-3.5" />,
  },
  meeting: {
    label: "Meeting",
    badgeClass: "bg-amber-500/15 text-amber-300 border-amber-500/30",
    icon: <Users className="w-3.5 h-3.5" />,
  },
  focus: {
    label: "Deep Work",
    badgeClass: "bg-purple-500/15 text-purple-300 border-purple-500/30",
    icon: <Brain className="w-3.5 h-3.5" />,
  },
  health: {
    label: "Health",
    badgeClass: "bg-emerald-500/15 text-emerald-300 border-emerald-500/30",
    icon: <HeartPulse className="w-3.5 h-3.5" />,
  },
  personal: {
    label: "Personal",
    badgeClass: "bg-teal-500/15 text-teal-300 border-teal-500/30",
    icon: <User className="w-3.5 h-3.5" />,
  },
  other: {
    label: "Sonstiges",
    badgeClass: "bg-zinc-500/15 text-zinc-300 border-zinc-500/30",
    icon: <Tag className="w-3.5 h-3.5" />,
  },
};

const EXAMPLE_PROMPTS = [
  "Morgen von 9 bis 10 Uhr Daily Standup, danach 2 Stunden Deep Work an Chronos und um 18 Uhr 45 Minuten Joggen im Park",
  "Heute um 14:30 Uhr kurzes Alignment mit Sarah (30 Min) und um 16 Uhr Zahnarzttermin für eine Stunde",
  "Übermorgen 10-12 Uhr Architektur-Workshop, 12:30 Uhr Mittagessen im Team, 15 Uhr Code-Reviews",
];

function formatDateTimeRange(startIso: string, endIso: string): {
  dateStr: string;
  timeRangeStr: string;
} {
  try {
    const start = new Date(startIso);
    const end = new Date(endIso);
    const dateStr = start.toLocaleDateString("de-DE", {
      weekday: "short",
      day: "2-digit",
      month: "2-digit",
      year: "numeric",
    });
    const startTime = start.toLocaleTimeString("de-DE", {
      hour: "2-digit",
      minute: "2-digit",
    });
    const endTime = end.toLocaleTimeString("de-DE", {
      hour: "2-digit",
      minute: "2-digit",
    });
    return { dateStr, timeRangeStr: `${startTime} – ${endTime} Uhr` };
  } catch {
    return { dateStr: startIso, timeRangeStr: endIso };
  }
}

export default function ChronosPage() {
  const [inputText, setInputText] = useState("");
  const [events, setEvents] = useState<ParsedEvent[]>([]);
  const [syncedResults, setSyncedResults] = useState<SyncedEventResult[]>([]);
  const [isParsing, setIsParsing] = useState(false);
  const [isSyncing, setIsSyncing] = useState(false);
  const [isAuthenticated, setIsAuthenticated] = useState<boolean | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);

  useEffect(() => {
    checkAuthStatus().then(setIsAuthenticated);
  }, []);

  async function handleParse(e?: React.FormEvent) {
    if (e) e.preventDefault();
    if (!inputText.trim() || isParsing) return;

    setIsParsing(true);
    setErrorMessage(null);
    setSyncedResults([]);

    try {
      const tz =
        Intl.DateTimeFormat().resolvedOptions().timeZone || "Europe/Berlin";
      const response = await parseScheduleIntent(inputText, tz);
      setEvents(response.events);
    } catch (err) {
      setErrorMessage(
        err instanceof Error
          ? err.message
          : "Unbekannter Fehler bei der KI-Analyse."
      );
    } finally {
      setIsParsing(false);
    }
  }

  async function handleSync() {
    if (events.length === 0 || isSyncing) return;

    setIsSyncing(true);
    setErrorMessage(null);

    try {
      const response = await syncEventsToCalendar(events);
      setSyncedResults(response.synced_events);
      setIsAuthenticated(true);
    } catch (err) {
      setErrorMessage(
        err instanceof Error
          ? err.message
          : "Fehler beim Synchronisieren mit Google Calendar."
      );
    } finally {
      setIsSyncing(false);
    }
  }

  function handleUpdateEvent(
    index: number,
    field: keyof ParsedEvent,
    value: string
  ) {
    setEvents((prev) =>
      prev.map((ev, i) => (i === index ? { ...ev, [field]: value } : ev))
    );
  }

  function handleRemoveEvent(index: number) {
    setEvents((prev) => prev.filter((_, i) => i !== index));
  }

  return (
    <div className="min-h-screen bg-zinc-950 text-zinc-100">
      {/* Top Glow */}
      <div className="pointer-events-none fixed inset-x-0 top-0 h-64 bg-gradient-to-b from-indigo-500/10 via-purple-500/5 to-transparent" />

      <div className="relative mx-auto max-w-4xl px-6 py-12">
        {/* Header */}
        <header className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between border-b border-zinc-800/80 pb-6">
          <div className="flex items-center gap-3">
            <img
              src="/logo.png"
              alt="Chronos Logo"
              className="h-11 w-11 rounded-xl object-contain shadow-inner"
            />
            <div>
              <h1 className="text-2xl font-bold tracking-tight text-white">
                Chronos
              </h1>
              <p className="text-sm text-zinc-400">
                KI-gestützter Day-Planner & Google Calendar Sync
              </p>
            </div>
          </div>

          {/* Google Calendar Connection Status */}
          <div className="flex items-center gap-3">
            {isAuthenticated ? (
              <span className="inline-flex items-center gap-2 rounded-full bg-emerald-500/10 border border-emerald-500/30 px-3.5 py-1.5 text-xs font-medium text-emerald-300">
                <span className="h-2 w-2 rounded-full bg-emerald-400 animate-pulse" />
                Google Calendar verbunden
              </span>
            ) : (
              <a
                href={`${API_BASE_URL}/api/auth/login`}
                className="inline-flex items-center gap-2 rounded-xl bg-zinc-900 hover:bg-zinc-800 border border-zinc-700/80 px-4 py-2 text-xs font-medium text-zinc-200 transition"
              >
                <Link2 className="h-3.5 w-3.5 text-indigo-400" />
                Mit Google Calendar verbinden
              </a>
            )}
          </div>
        </header>

        {/* Natural Language Input Section */}
        <section className="mt-8">
          <form onSubmit={handleParse} className="space-y-4">
            <div className="rounded-2xl bg-zinc-900/80 border border-zinc-800 p-4 shadow-xl focus-within:border-indigo-500/60 transition">
              <label
                htmlFor="schedule-input"
                className="block text-xs font-medium uppercase tracking-wider text-zinc-400 mb-2"
              >
                Was steht an? (Freitext eingeben)
              </label>
              <textarea
                id="schedule-input"
                rows={4}
                value={inputText}
                onChange={(e) => setInputText(e.target.value)}
                placeholder="z. B. Morgen von 9 bis 10 Uhr Daily Standup, danach 2 Stunden Deep Work an Chronos und um 18 Uhr 45 Minuten Joggen..."
                className="w-full resize-none bg-transparent text-zinc-100 placeholder-zinc-500 focus:outline-none text-base leading-relaxed"
              />

              <div className="mt-3 flex flex-wrap items-center justify-between gap-3 border-t border-zinc-800/80 pt-3">
                <div className="flex flex-wrap items-center gap-1.5">
                  <span className="text-xs text-zinc-500 mr-1">Beispiele:</span>
                  {EXAMPLE_PROMPTS.map((sample, idx) => (
                    <button
                      key={idx}
                      type="button"
                      onClick={() => setInputText(sample)}
                      className="rounded-lg bg-zinc-800/70 hover:bg-zinc-800 px-2.5 py-1 text-xs text-zinc-300 transition"
                    >
                      Beispiel {idx + 1}
                    </button>
                  ))}
                </div>

                <button
                  type="submit"
                  disabled={isParsing || !inputText.trim()}
                  className="inline-flex items-center gap-2 rounded-xl bg-indigo-600 hover:bg-indigo-500 disabled:opacity-50 disabled:pointer-events-none px-5 py-2.5 text-sm font-medium text-white shadow-lg shadow-indigo-600/20 transition"
                >
                  {isParsing ? (
                    <>
                      <Loader2 className="h-4 w-4 animate-spin" />
                      KI analysiert...
                    </>
                  ) : (
                    <>
                      <Sparkles className="h-4 w-4" />
                      Termine extrahieren
                    </>
                  )}
                </button>
              </div>
            </div>
          </form>
        </section>

        {/* Error Banner */}
        {errorMessage && (
          <div className="mt-6 flex items-start gap-3 rounded-xl bg-red-500/10 border border-red-500/30 p-4 text-sm text-red-300">
            <AlertCircle className="h-5 w-5 shrink-0 text-red-400 mt-0.5" />
            <div>
              <p className="font-medium">Fehler</p>
              <p className="text-red-300/90 mt-0.5">{errorMessage}</p>
            </div>
          </div>
        )}

        {/* Synced Confirmation Banner */}
        {syncedResults.length > 0 && (
          <div className="mt-6 rounded-2xl bg-emerald-500/10 border border-emerald-500/30 p-5">
            <div className="flex items-center gap-2.5 text-emerald-300 font-medium">
              <CheckCircle2 className="h-5 w-5 text-emerald-400" />
              <span>
                {syncedResults.length}{" "}
                {syncedResults.length === 1 ? "Termin wurde" : "Termine wurden"}{" "}
                erfolgreich in deinen Google Kalender eingetragen!
              </span>
            </div>
            <div className="mt-3 grid gap-2 sm:grid-cols-2">
              {syncedResults.map((item, i) => (
                <div
                  key={i}
                  className="flex items-center justify-between rounded-xl bg-zinc-900/80 border border-zinc-800 px-3.5 py-2.5 text-xs"
                >
                  <span className="font-medium text-zinc-200 truncate pr-2">
                    {item.title}
                  </span>
                  {item.html_link && (
                    <a
                      href={item.html_link}
                      target="_blank"
                      rel="noreferrer"
                      className="inline-flex items-center gap-1 text-indigo-400 hover:text-indigo-300 shrink-0 font-medium"
                    >
                      Im Kalender öffnen
                      <ExternalLink className="h-3 w-3" />
                    </a>
                  )}
                </div>
              ))}
            </div>
          </div>
        )}

        {/* Parsed Events Preview & Editor */}
        {events.length > 0 && (
          <section className="mt-8 space-y-4">
            <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
              <div>
                <h2 className="text-lg font-semibold text-white flex items-center gap-2">
                  <Calendar className="h-5 w-5 text-indigo-400" />
                  Erkannte Termine ({events.length})
                </h2>
                <p className="text-xs text-zinc-400">
                  Du kannst Titel, Kategorie oder Zeiten vor der Synchronisation
                  noch anpassen.
                </p>
              </div>

              <button
                type="button"
                onClick={handleSync}
                disabled={isSyncing}
                className="inline-flex items-center justify-center gap-2 rounded-xl bg-emerald-600 hover:bg-emerald-500 disabled:opacity-50 px-5 py-2.5 text-sm font-medium text-white shadow-lg shadow-emerald-600/20 transition"
              >
                {isSyncing ? (
                  <>
                    <Loader2 className="h-4 w-4 animate-spin" />
                    Synchronisiere...
                  </>
                ) : (
                  <>
                    <Plus className="h-4 w-4" />
                    In Google Calendar eintragen
                  </>
                )}
              </button>
            </div>

            <div className="space-y-3">
              {events.map((ev, index) => {
                const cat =
                  CATEGORY_CONFIG[ev.category] || CATEGORY_CONFIG.other;
                const { dateStr, timeRangeStr } = formatDateTimeRange(
                  ev.start_time,
                  ev.end_time
                );

                return (
                  <div
                    key={index}
                    className="group rounded-2xl bg-zinc-900/70 border border-zinc-800/90 hover:border-zinc-700 p-4 transition"
                  >
                    <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
                      <div className="flex-1 space-y-2">
                        <div className="flex flex-wrap items-center gap-2">
                          <span
                            className={`inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-xs font-medium ${cat.badgeClass}`}
                          >
                            {cat.icon}
                            <select
                              value={ev.category}
                              onChange={(e) =>
                                handleUpdateEvent(
                                  index,
                                  "category",
                                  e.target.value as EventCategory
                                )
                              }
                              className="bg-transparent focus:outline-none cursor-pointer"
                            >
                              <option value="work" className="bg-zinc-900 text-zinc-100">Work</option>
                              <option value="meeting" className="bg-zinc-900 text-zinc-100">Meeting</option>
                              <option value="focus" className="bg-zinc-900 text-zinc-100">Deep Work</option>
                              <option value="health" className="bg-zinc-900 text-zinc-100">Health</option>
                              <option value="personal" className="bg-zinc-900 text-zinc-100">Personal</option>
                              <option value="other" className="bg-zinc-900 text-zinc-100">Sonstiges</option>
                            </select>
                          </span>

                          <span className="text-xs text-zinc-400 font-medium">
                            {dateStr} · {timeRangeStr}
                          </span>
                        </div>

                        <input
                          type="text"
                          value={ev.title}
                          onChange={(e) =>
                            handleUpdateEvent(index, "title", e.target.value)
                          }
                          className="w-full bg-transparent text-base font-semibold text-white focus:outline-none focus:ring-1 focus:ring-indigo-500/50 rounded px-1 -mx-1"
                        />

                        <input
                          type="text"
                          value={ev.description || ""}
                          placeholder="Notizen / Beschreibung hinzufügen..."
                          onChange={(e) =>
                            handleUpdateEvent(
                              index,
                              "description",
                              e.target.value
                            )
                          }
                          className="w-full bg-transparent text-sm text-zinc-400 placeholder-zinc-600 focus:outline-none focus:ring-1 focus:ring-indigo-500/40 rounded px-1 -mx-1"
                        />
                      </div>

                      <button
                        type="button"
                        onClick={() => handleRemoveEvent(index)}
                        title="Termin entfernen"
                        className="self-end sm:self-start rounded-lg p-2 text-zinc-500 hover:bg-red-500/10 hover:text-red-400 transition"
                      >
                        <Trash2 className="h-4 w-4" />
                      </button>
                    </div>
                  </div>
                );
              })}
            </div>
          </section>
        )}
      </div>
    </div>
  );
}

