import type { AppDayDto } from '../api/client.ts';

/** One row of the title list. */
export interface TitleRow {
  /** The window title, or `null` for time where no title was recorded. */
  title: string | null;
  durationMs: number;
  /** Whole percentage of the app's time. */
  percent: number;
  /** Exact fraction of the app's time, for the bar width. */
  share: number;
}

/**
 * True when the app had time but none of it has a title: title recording was off, or the app is
 * one whose titles aren't recorded. Then a list with one "no title" row says nothing useful.
 */
export function hasNoTitles(day: Pick<AppDayDto, 'totalMs' | 'titles'>): boolean {
  return day.totalMs > 0 && day.titles.every((t) => t.title === null);
}

/**
 * The titles that match `query`, longest first as the API sends them. The search ignores case
 * and looks anywhere in the title; time without a title only shows while the search is empty.
 */
export function filterTitles(titles: readonly TitleRow[], query: string): TitleRow[] {
  const needle = query.trim().toLocaleLowerCase();
  if (needle === '') {
    return [...titles];
  }
  return titles.filter((t) => t.title?.toLocaleLowerCase().includes(needle) ?? false);
}

/** How many different titles the list has, not counting time where no title was recorded. */
export function countTitles(titles: readonly TitleRow[]): number {
  return titles.filter((t) => t.title !== null).length;
}
