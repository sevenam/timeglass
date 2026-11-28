import type { DayTotalDto, RangeSummaryDto } from '../api/client.ts';
import { weekStart } from '../dates.ts';

/** How many of the period's apps the chart draws: the longest ones, or all of them. */
export type AppLimit = number | 'all';

/** One stretch of the chart's x axis: a day, or a week for long periods. */
export interface TrendBucket {
  /** The first date in the bucket, `yyyy-MM-dd`. */
  start: string;
  /** The last date in the bucket that the period includes. */
  end: string;
  /** How many of the period's days fall in the bucket (a week at either end may be cut short). */
  days: number;
}

/** One app's line: its average active time per day in each bucket. */
export interface TrendSeries {
  key: string;
  name: string;
  color: string;
  /** Active time over the whole period. */
  totalMs: number;
  /** Average active time per day, one value per bucket. */
  values: number[];
}

export interface Trend {
  unit: 'day' | 'week';
  buckets: TrendBucket[];
  series: TrendSeries[];
  /** The highest value of any line, for the y axis. */
  maxMs: number;
}

/** Periods up to this many days are drawn day by day; longer ones week by week. */
export const DAILY_LIMIT = 90;

/**
 * The lines of the trend chart (#99) from a range summary: one per app, longest first, cut to
 * `limit` apps, or only the app `appKey` when one is picked. Each value is an average per day, so
 * a week's point reads on the same scale as a day's.
 */
export function trendSeries(
  range: RangeSummaryDto,
  { limit = 'all', appKey = null }: { limit?: AppLimit; appKey?: string | null } = {},
): Trend {
  const unit = range.days.length > DAILY_LIMIT ? 'week' : 'day';
  const categories = groupDays(range.days, unit);
  const buckets = categories.map((g) => ({
    start: g[0]?.date ?? '',
    end: g.at(-1)?.date ?? '',
    days: g.length,
  }));

  const apps = [...range.apps].sort((a, b) => b.durationMs - a.durationMs);
  const picked =
    appKey === null
      ? limit === 'all'
        ? apps
        : apps.slice(0, Math.max(0, limit))
      : apps.filter((a) => a.app.key === appKey);

  const series = picked.map((a) => ({
    key: a.app.key,
    name: a.app.name,
    color: a.color,
    totalMs: a.durationMs,
    values: categories.map((g) => {
      const sum = g.reduce(
        (total, day) => total + (day.apps.find((d) => d.app.key === a.app.key)?.durationMs ?? 0),
        0,
      );
      return g.length === 0 ? 0 : sum / g.length;
    }),
  }));

  const maxMs = Math.max(0, ...series.flatMap((s) => s.values));
  return { unit, buckets, series, maxMs };
}

function groupDays(days: readonly DayTotalDto[], unit: 'day' | 'week'): DayTotalDto[][] {
  if (unit === 'day') {
    return days.map((d) => [d]);
  }
  const categories: DayTotalDto[][] = [];
  let current: DayTotalDto[] = [];
  let currentWeek = '';
  for (const day of days) {
    const week = weekStart(day.date);
    if (week !== currentWeek && current.length > 0) {
      categories.push(current);
      current = [];
    }
    currentWeek = week;
    current.push(day);
  }
  if (current.length > 0) {
    categories.push(current);
  }
  return categories;
}

/**
 * How an app's use changed over the period: the average of the second half against the first
 * half, as a whole percentage. `new` when it wasn't used in the first half at all, and `null` when
 * there is nothing to compare (fewer than two points, or no time in either half).
 */
export function trendChange(values: readonly number[]): number | 'new' | null {
  const half = Math.floor(values.length / 2);
  if (half === 0) {
    return null;
  }
  const average = (xs: readonly number[]) => xs.reduce((a, b) => a + b, 0) / xs.length;
  const before = average(values.slice(0, half));
  const after = average(values.slice(values.length - half));
  if (before === 0) {
    return after === 0 ? null : 'new';
  }
  return Math.round(((after - before) / before) * 100);
}

/** `+25%`, `−10%`, `±0%`, `New` or `–`, for {@link trendChange}. */
export function formatChange(change: number | 'new' | null): string {
  if (change === null) {
    return '–';
  }
  if (change === 'new') {
    return 'New';
  }
  if (change === 0) {
    return '±0%';
  }
  return change > 0 ? `+${change}%` : `−${-change}%`;
}

const MINUTE_MS = 60_000;
const HOUR_MS = 60 * MINUTE_MS;
const TICK_STEPS = [5, 10, 15, 30, 60, 120, 180, 240, 360, 480, 720].map((m) => m * MINUTE_MS);

/**
 * The y axis: a round step (15 minutes, 1 hour, 2 hours…) giving at most `maxTicks` steps up to
 * `maxMs`, and the ticks from 0 to the first one at or above it.
 */
export function axisTicks(maxMs: number, maxTicks = 4): number[] {
  if (maxMs <= 0) {
    return [0, HOUR_MS];
  }
  const step =
    TICK_STEPS.find((s) => Math.ceil(maxMs / s) <= maxTicks) ??
    Math.ceil(maxMs / maxTicks / (24 * HOUR_MS)) * 24 * HOUR_MS;
  const top = Math.ceil(maxMs / step) * step;
  return Array.from({ length: top / step + 1 }, (_, i) => i * step);
}
