import type { SettingsDto } from '../api/client.ts';

/** One row of the app names table, as typed. */
export interface AliasRow {
  key: string;
  name: string;
  /** Executable file names, separated by commas. */
  exes: string;
  titlePattern: string;
}

export interface CategoryRow {
  name: string;
  keywords: string;
}

/** The settings form as the user edits it; {@link toSettings} turns it into what the API takes. */
export interface SettingsForm {
  idleEnabled: boolean;
  /** Minutes as typed, so a half-typed number isn't lost. */
  idleMinutes: string;
  idleExemptApps: string[];
  excludedApps: string[];
  recordWindowTitles: boolean;
  appsWithoutTitles: string[];
  aliases: AliasRow[];
  categories: CategoryRow[];
  /** Null where the app can't start with Windows (the dev host), so the switch is hidden. */
  startWithWindows: boolean | null;
  /** When work hours begin and end, `HH:mm` as the time inputs give them. */
  workStart: string;
  workEnd: string;
  discardAfterHoursReturns: boolean;
  afterHoursMinimumAwayMinutes: string;
  afterHoursMaximumReturnMinutes: string;
}

/** Errors by the API's field names (`idleThresholdSeconds`, `excludedApps`, …). */
export type FieldErrors = Record<string, string[]>;

/** The idle threshold the API accepts: 30 seconds to 4 hours (`TimeglassSettings.IdleThreshold`). */
export const IDLE_MIN_SECONDS = 30;
export const IDLE_MAX_SECONDS = 4 * 60 * 60;

/** The idle threshold offered when the user turns idle detection back on. */
const DEFAULT_IDLE_MINUTES = '5';

/** Work hours when the API leaves them out: 09:00 to 17:00, as in `WorkHours.Default`. */
const DEFAULT_WORK_START = 9 * 60;
const DEFAULT_WORK_END = 17 * 60;

/** `HH:mm` for minutes after midnight. */
export function toClock(minutes: number): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(Math.floor(minutes / 60))}:${pad(minutes % 60)}`;
}

/** Minutes after midnight from `HH:mm`, or NaN if it isn't a time of day. */
export function fromClock(clock: string): number {
  const match = /^(\d{1,2}):(\d{2})$/.exec(clock.trim());
  if (!match) {
    return Number.NaN;
  }
  const hours = Number(match[1]);
  const minutes = Number(match[2]);
  return hours < 24 && minutes < 60 ? hours * 60 + minutes : Number.NaN;
}

export function toForm(settings: SettingsDto): SettingsForm {
  return {
    idleEnabled: settings.idleThresholdSeconds !== null,
    idleMinutes:
      settings.idleThresholdSeconds === null
        ? DEFAULT_IDLE_MINUTES
        : String(Math.round((settings.idleThresholdSeconds / 60) * 100) / 100),
    idleExemptApps: [...settings.idleExemptApps],
    excludedApps: [...settings.excludedApps],
    recordWindowTitles: settings.recordWindowTitles,
    appsWithoutTitles: [...settings.appsWithoutTitles],
    aliases: settings.appAliases.map((a) => ({
      key: a.key,
      name: a.name,
      exes: a.exes.join(', '),
      titlePattern: a.titlePattern ?? '',
    })),
    categories: (settings.categories ?? []).map((category) => ({
      name: category.name,
      keywords: category.keywords.join(', '),
    })),
    startWithWindows: settings.startWithWindows,
    workStart: toClock(settings.workHoursStartMinutes ?? DEFAULT_WORK_START),
    workEnd: toClock(settings.workHoursEndMinutes ?? DEFAULT_WORK_END),
    discardAfterHoursReturns: settings.discardAfterHoursReturns ?? false,
    afterHoursMinimumAwayMinutes: String(settings.afterHoursMinimumAwayMinutes ?? 60),
    afterHoursMaximumReturnMinutes: String(settings.afterHoursMaximumReturnMinutes ?? 10),
  };
}

/** Seconds from the minutes field, or NaN if it isn't a number. */
function idleSeconds(form: SettingsForm): number {
  const text = form.idleMinutes.trim().replace(',', '.');
  return text === '' ? Number.NaN : Math.round(Number(text) * 60);
}

export function toSettings(form: SettingsForm): SettingsDto {
  return {
    idleThresholdSeconds: form.idleEnabled ? idleSeconds(form) : null,
    idleExemptApps: form.idleExemptApps,
    excludedApps: form.excludedApps,
    recordWindowTitles: form.recordWindowTitles,
    appsWithoutTitles: form.appsWithoutTitles,
    appAliases: form.aliases.map((row) => ({
      key: row.key.trim(),
      name: row.name.trim(),
      exes: row.exes
        .split(',')
        .map((e) => e.trim())
        .filter((e) => e !== ''),
      titlePattern: row.titlePattern.trim() === '' ? null : row.titlePattern,
    })),
    categories: form.categories.map((category) => ({
      name: category.name.trim(),
      keywords: category.keywords
        .split(',')
        .map((keyword) => keyword.trim())
        .filter(Boolean),
    })),
    startWithWindows: form.startWithWindows,
    workHoursStartMinutes: fromClock(form.workStart),
    workHoursEndMinutes: fromClock(form.workEnd),
    discardAfterHoursReturns: form.discardAfterHoursReturns,
    afterHoursMinimumAwayMinutes: Number(form.afterHoursMinimumAwayMinutes),
    afterHoursMaximumReturnMinutes: Number(form.afterHoursMaximumReturnMinutes),
  };
}

/**
 * The mistakes the form can catch before saving, worded for people. The API checks again with the
 * settings model's own rules and has the last word.
 */
export function validate(form: SettingsForm): FieldErrors {
  const errors: FieldErrors = {};
  if (form.idleEnabled) {
    const seconds = idleSeconds(form);
    if (!Number.isFinite(seconds) || seconds < IDLE_MIN_SECONDS || seconds > IDLE_MAX_SECONDS) {
      errors.idleThresholdSeconds = ['Enter a number of minutes from 0.5 to 240.'];
    }
  }

  const workStart = fromClock(form.workStart);
  const workEnd = fromClock(form.workEnd);
  if (Number.isNaN(workStart)) {
    errors.workHoursStartMinutes = ['Enter the time work hours begin, such as 09:00.'];
  }
  if (Number.isNaN(workEnd)) {
    errors.workHoursEndMinutes = ['Enter the time work hours end, such as 17:00.'];
  } else if (!Number.isNaN(workStart) && workEnd <= workStart) {
    errors.workHoursEndMinutes = ['Work hours must end after they begin.'];
  }

  for (const [field, text, max] of [
    ['afterHoursMinimumAwayMinutes', form.afterHoursMinimumAwayMinutes, 1440],
    ['afterHoursMaximumReturnMinutes', form.afterHoursMaximumReturnMinutes, 240],
  ] as const) {
    const value = Number(text);
    if (text.trim() === '' || !Number.isInteger(value) || value < 1 || value > max) {
      errors[field] = [`Enter a whole number of minutes from 1 to ${max}.`];
    }
  }

  const withoutTitles = new Set(form.appsWithoutTitles.map((k) => k.toLowerCase()));
  const both = form.excludedApps.filter((k) => withoutTitles.has(k.toLowerCase()));
  if (both.length > 0) {
    errors.appsWithoutTitles = [
      `${both.join(', ')} ${both.length === 1 ? 'is' : 'are'} also excluded; an excluded app has no titles to hide.`,
    ];
  }

  const aliasErrors = form.aliases.flatMap((row, i) =>
    row.key.trim() === '' ? [`Row ${i + 1}: give the app a key, such as "game".`] : [],
  );
  if (aliasErrors.length > 0) {
    errors.appAliases = aliasErrors;
  }
  const names = new Set<string>();
  const categoryErrors: string[] = [];
  form.categories.forEach((category, i) => {
    const name = category.name.trim().toLowerCase();
    if (!name || name === 'ungrouped' || names.has(name)) {
      categoryErrors.push(`Category ${i + 1}: enter a unique name other than Ungrouped.`);
    }
    names.add(name);
    if (!category.keywords.split(',').some((keyword) => keyword.trim())) {
      categoryErrors.push(`Category ${i + 1}: enter at least one keyword.`);
    }
  });
  if (categoryErrors.length > 0) {
    errors.categories = categoryErrors;
  }
  return errors;
}

/** Adds `key` (trimmed) to `keys` unless it is empty or already there, in any case. */
export function addKey(keys: string[], key: string): string[] {
  const trimmed = key.trim();
  if (trimmed === '' || keys.some((k) => k.toLowerCase() === trimmed.toLowerCase())) {
    return keys;
  }
  return [...keys, trimmed];
}
