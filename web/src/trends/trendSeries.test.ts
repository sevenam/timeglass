import type { RangeSummaryDto } from '../api/client.ts';
import { addDays } from '../dates.ts';
import { rangeSummary } from '../test/fixtures.ts';
import { axisTicks, formatChange, trendChange, trendSeries } from './trendSeries.ts';

const HOUR = 3_600_000;
const MINUTE = 60_000;

describe('trendSeries', () => {
  // Monday 2 to Sunday 8 November: five workdays with the demo apps, then an empty weekend.
  const week = rangeSummary('2026-11-02', '2026-11-08');

  it('draws one line per app, longest first, with a value per day', () => {
    const trend = trendSeries(week);

    expect(trend.unit).toBe('day');
    expect(trend.buckets.map((b) => b.start)).toEqual([
      '2026-11-02',
      '2026-11-03',
      '2026-11-04',
      '2026-11-05',
      '2026-11-06',
      '2026-11-07',
      '2026-11-08',
    ]);
    expect(trend.series.map((s) => s.key)).toEqual([
      'vscode',
      'outlook',
      'edge',
      'teams',
      'terminal',
    ]);
    const code = trend.series[0];
    expect(code?.values).toEqual([3, 3, 3, 3, 3, 0, 0].map((h) => h * HOUR));
    expect(code?.totalMs).toBe(15 * HOUR);
    expect(code?.color).toBe(week.apps.find((a) => a.app.key === 'vscode')?.color);
    expect(trend.maxMs).toBe(3 * HOUR);
  });

  it('cuts the lines to the longest apps', () => {
    expect(trendSeries(week, { limit: 3 }).series.map((s) => s.key)).toEqual([
      'vscode',
      'outlook',
      'edge',
    ]);
    expect(trendSeries(week, { limit: 'all' }).series).toHaveLength(5);
  });

  it('shows only the app picked, whatever the limit', () => {
    const trend = trendSeries(week, { limit: 1, appKey: 'teams' });

    expect(trend.series.map((s) => s.key)).toEqual(['teams']);
    expect(trend.maxMs).toBe(45 * MINUTE);
  });

  it('keeps the same colours when limiting or picking an app', () => {
    const all = trendSeries(week).series;
    const limited = trendSeries(week, { limit: 3 }).series;
    const picked = trendSeries(week, { appKey: 'vscode' }).series;
    expect(limited.map((s) => s.color)).toEqual(all.slice(0, 3).map((s) => s.color));
    expect(picked[0]?.color).toBe(all.find((s) => s.key === 'vscode')?.color);
  });

  it('has no lines for an app not in the range', () => {
    const trend = trendSeries(week, { appKey: 'nope' });

    expect(trend.series).toEqual([]);
    expect(trend.maxMs).toBe(0);
  });

  it('goes week by week over long periods, averaging per day', () => {
    // 2 November 2026 is a Monday; 91 days run to Sunday 31 January, thirteen whole weeks.
    const from = '2026-11-02';
    const long: RangeSummaryDto = rangeSummary(from, addDays(from, 90));
    const trend = trendSeries(long, { appKey: 'vscode' });

    expect(trend.unit).toBe('week');
    expect(trend.buckets).toHaveLength(13);
    expect(trend.buckets[0]).toEqual({ start: '2026-11-02', end: '2026-11-08', days: 7 });
    // Five days of 3h in seven.
    expect(trend.series[0]?.values[0]).toBeCloseTo((15 * HOUR) / 7);
  });

  it('keeps short weeks at the ends of a long period', () => {
    // Thursday 5 November for 100 days: Thursday to Sunday first, then whole weeks.
    const long = rangeSummary('2026-11-05', addDays('2026-11-05', 99));
    const { buckets } = trendSeries(long);

    expect(buckets[0]).toEqual({ start: '2026-11-05', end: '2026-11-08', days: 4 });
    expect(buckets[1]?.start).toBe('2026-11-09');
    expect(buckets.reduce((n, b) => n + b.days, 0)).toBe(100);
  });
});

describe('trendChange', () => {
  it.each([
    [[1, 1, 2, 2], 100],
    [[2, 2, 1, 1], -50],
    [[1, 5, 1], 0],
    [[0, 0, 3, 3], 'new'],
    [[0, 0, 0, 0], null],
    [[4], null],
    [[], null],
  ])('of %j is %j', (values, change) => {
    expect(trendChange(values)).toBe(change);
  });

  it.each([
    [25, '+25%'],
    [-10, '−10%'],
    [0, '±0%'],
    ['new', 'New'],
    [null, '–'],
  ] as const)('formats %j as %s', (change, text) => {
    expect(formatChange(change)).toBe(text);
  });
});

describe('axisTicks', () => {
  it.each([
    [0, [0, HOUR]],
    [40 * MINUTE, [0, 10, 20, 30, 40].map((m) => m * MINUTE)],
    [50 * MINUTE, [0, 15, 30, 45, 60].map((m) => m * MINUTE)],
    [3 * HOUR, [0, 1, 2, 3].map((h) => h * HOUR)],
    [7.5 * HOUR, [0, 2, 4, 6, 8].map((h) => h * HOUR)],
  ])('for %d ms are %j', (max, ticks) => {
    expect(axisTicks(max)).toEqual(ticks);
  });

  it('always reaches the highest value in at most four steps', () => {
    for (const max of [1, 59_999, 5 * HOUR, 13 * HOUR, 30 * HOUR]) {
      const ticks = axisTicks(max);
      expect(ticks.at(-1)).toBeGreaterThanOrEqual(max);
      expect(ticks.length).toBeLessThanOrEqual(5);
    }
  });
});
