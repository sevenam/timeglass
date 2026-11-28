import type { AppTotalDto } from '../api/client.ts';
import { appColor, OTHER_COLOR } from './appColor.ts';
import { appShares } from './appShares.ts';

const app = (key: string, minutes: number, percent: number): AppTotalDto => ({
  app: { key, name: key.toUpperCase() },
  durationMs: minutes * 60_000,
  share: percent / 100,
  percent,
  color: `color-${key}`,
});

describe('appColor', () => {
  it('is deterministic and returns a valid hsl colour', () => {
    expect(appColor('vscode')).toBe(appColor('vscode'));
    expect(appColor('vscode')).not.toBe(appColor('outlook'));
    expect(appColor('anything')).toMatch(/^hsl\(\d{1,3} [\d.]+% [\d.]+%\)$/);
  });
});

describe('appShares', () => {
  it('lists every app by name', () => {
    const rows = appShares([app('a', 30, 60), app('b', 20, 40)]);

    expect(rows.map((r) => r.name)).toEqual(['A', 'B']);
    expect(rows[0]?.color).toBe('color-a');
  });

  it('sorts longest first even if the API did not', () => {
    const rows = appShares([app('a', 10, 20), app('b', 40, 80)]);

    expect(rows.map((r) => r.key)).toEqual(['b', 'a']);
  });

  it('uses the persistent colours supplied by the API', () => {
    const first = appShares([app('app-19', 10, 50), app('app-24', 20, 50)]);
    const second = appShares([app('app-24', 10, 50), app('app-19', 20, 50), app('new-app', 5, 1)]);
    const colors = (rows: typeof first) =>
      Object.fromEntries(rows.map((row) => [row.key, row.color]));

    expect(colors(first)['app-19']).toBe('color-app-19');
    expect(colors(second)['app-19']).toBe(colors(first)['app-19']);
    expect(colors(second)['app-24']).toBe(colors(first)['app-24']);
  });

  it('keeps fallback colours distinct across many keys', () => {
    const colors = Array.from({ length: 400 }, (_, i) => appColor(`key-${i}`));
    expect(new Set(colors).size).toBe(400);
  });

  it('never folds apps into Other, however many there are (#96)', () => {
    const apps = 'abcdefghij'.split('').map((key, i) => app(key, 10 - i, 10));
    const rows = appShares(apps);

    expect(rows.map((r) => r.key)).toEqual('abcdefghij'.split(''));
    expect(rows.every((r) => r.color !== OTHER_COLOR)).toBe(true);
    expect(rows.reduce((sum, r) => sum + r.percent, 0)).toBe(100);
  });

  it('returns nothing for a day without apps', () => {
    expect(appShares([])).toEqual([]);
  });
});
