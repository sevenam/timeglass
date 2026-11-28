import type {
  AppDayDto,
  AppDto,
  AppTotalDto,
  DaySummaryDto,
  DayTotalDto,
  RangeSummaryDto,
  SettingsDto,
  StatusDto,
  TimelineDto,
} from '../api/client.ts';

/**
 * A demo workday shaped like the dev host's demo data (`tools/Timeglass.DevHost/DemoData.cs`):
 * the same apps and titles, a standup, a lunch break and an open block at the end. Typed from the
 * generated schema, so a change to the API that breaks them fails the typecheck.
 */
export const demoDate = '2026-11-05';

const offset = '+01:00';
const at = (date: string, time: string) => `${date}T${time}${offset}`;
const nextDay = (date: string) => {
  const d = new Date(`${date}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + 1);
  return d.toISOString().slice(0, 10);
};

export const apps = {
  code: { key: 'vscode', name: 'Visual Studio Code' },
  outlook: { key: 'outlook', name: 'Outlook' },
  edge: { key: 'edge', name: 'Microsoft Edge' },
  teams: { key: 'teams', name: 'Microsoft Teams' },
  terminal: { key: 'terminal', name: 'Windows Terminal' },
} satisfies Record<string, AppDto>;

const minutes = (n: number) => n * 60_000;

const appTotals: AppTotalDto[] = [
  {
    app: apps.code,
    durationMs: minutes(180),
    share: 180 / 420,
    percent: 43,
    color: 'hsl(0 75% 50%)',
  },
  {
    app: apps.outlook,
    durationMs: minutes(105),
    share: 105 / 420,
    percent: 25,
    color: 'hsl(120 75% 50%)',
  },
  {
    app: apps.edge,
    durationMs: minutes(75),
    share: 75 / 420,
    percent: 18,
    color: 'hsl(240 75% 50%)',
  },
  {
    app: apps.teams,
    durationMs: minutes(45),
    share: 45 / 420,
    percent: 11,
    color: 'hsl(60 75% 50%)',
  },
  {
    app: apps.terminal,
    durationMs: minutes(15),
    share: 15 / 420,
    percent: 3,
    color: 'hsl(300 75% 50%)',
  },
];

export function status(overrides: Partial<StatusDto> = {}): StatusDto {
  return {
    state: 'active',
    pausedUntil: null,
    currentApp: apps.code,
    since: at(demoDate, '15:42:10'),
    today: demoDate,
    now: at(demoDate, '16:05:00'),
    ...overrides,
  };
}

export function daySummary(date: string = demoDate): DaySummaryDto {
  return {
    date,
    start: at(date, '00:00:00'),
    end: at(nextDay(date), '00:00:00'),
    activeMs: minutes(420),
    awayMs: minutes(65),
    firstActivity: at(date, '08:12:31'),
    lastActivity: at(date, '16:05:00'),
    // From 09:00, when work hours begin, to the last activity at 16:05.
    workdayMs: minutes(425),
    apps: appTotals,
  };
}

export function timeline(date: string = demoDate): TimelineDto {
  const block = (
    app: AppDto | null,
    start: string,
    end: string,
    durationMs: number,
    isOpen = false,
  ) => ({
    kind: app ? ('active' as const) : ('away' as const),
    app,
    start: at(date, start),
    end: at(date, end),
    durationMs,
    isOpen,
    category: null,
  });
  return {
    date,
    start: at(date, '00:00:00'),
    end: at(nextDay(date), '00:00:00'),
    blocks: [
      block(apps.outlook, '08:12:31', '08:40:00', minutes(27) + 29_000),
      block(apps.code, '08:40:00', '09:30:00', minutes(50)),
      block(apps.teams, '09:30:00', '09:47:00', minutes(17)),
      block(apps.edge, '09:47:00', '11:40:00', minutes(113) - 3_000),
      block(null, '11:40:00', '12:20:00', minutes(40)),
      block(apps.terminal, '12:20:00', '12:35:00', minutes(15)),
      block(apps.code, '12:35:00', '16:05:00', minutes(210), true),
    ],
  };
}

export function appDay(date: string = demoDate, appKey: string = apps.code.key): AppDayDto {
  const app = Object.values(apps).find((a) => a.key === appKey) ?? { key: appKey, name: appKey };
  const total = app.key === apps.code.key ? minutes(180) : 0;
  return {
    date,
    app,
    start: at(date, '00:00:00'),
    end: at(nextDay(date), '00:00:00'),
    totalMs: total,
    color: appTotals.find((a) => a.app.key === appKey)?.color ?? null,
    titles:
      total === 0
        ? []
        : [
            {
              title: 'TrackingEngine.cs - timeglass - Visual Studio Code',
              durationMs: minutes(90),
              share: 0.5,
              percent: 50,
            },
            {
              title: 'DaySummaries.cs - timeglass - Visual Studio Code',
              durationMs: minutes(60),
              share: 1 / 3,
              percent: 33,
            },
            {
              title: 'Program.cs - timeglass - Visual Studio Code',
              durationMs: minutes(30),
              share: 1 / 6,
              percent: 17,
            },
          ],
  };
}

/** Every weekday from `from` to `to` looks like the demo day; weekends are empty. */
export function rangeSummary(from: string, to: string): RangeSummaryDto {
  const days: DayTotalDto[] = [];
  for (let date = from; date <= to; date = nextDay(date)) {
    const weekday = new Date(`${date}T00:00:00Z`).getUTCDay();
    const workday = weekday !== 0 && weekday !== 6;
    days.push({
      date,
      start: at(date, '00:00:00'),
      end: at(nextDay(date), '00:00:00'),
      activeMs: workday ? minutes(420) : 0,
      awayMs: workday ? minutes(65) : 0,
      apps: workday ? appTotals : [],
    });
  }
  const workdays = days.filter((d) => d.activeMs > 0).length;
  return {
    from,
    to,
    start: at(from, '00:00:00'),
    end: at(nextDay(to), '00:00:00'),
    activeMs: workdays * minutes(420),
    awayMs: workdays * minutes(65),
    days,
    apps:
      workdays === 0 ? [] : appTotals.map((a) => ({ ...a, durationMs: a.durationMs * workdays })),
  };
}

/** A category range shaped like the API response, including the built-in away category. */
export function categoryRangeSummary(from: string, to: string): RangeSummaryDto {
  const summary = rangeSummary(from, to);
  const totals = (activeMs: number, awayMs: number): AppTotalDto[] => {
    const totalMs = activeMs + awayMs;
    if (totalMs === 0) {
      return [];
    }
    return [
      ...(activeMs > 0
        ? [
            {
              app: { key: 'category-0', name: 'Work' },
              durationMs: activeMs,
              share: activeMs / totalMs,
              percent: Math.round((activeMs / totalMs) * 100),
              color: 'hsl(0 75% 50%)',
            },
          ]
        : []),
      ...(awayMs > 0
        ? [
            {
              app: { key: 'away', name: 'Away' },
              durationMs: awayMs,
              share: awayMs / totalMs,
              percent: Math.round((awayMs / totalMs) * 100),
              color: 'hsl(220 9% 60%)',
            },
          ]
        : []),
    ];
  };
  return {
    ...summary,
    apps: totals(summary.activeMs, summary.awayMs),
    days: summary.days.map((day) => ({
      ...day,
      apps: totals(day.activeMs, day.awayMs),
    })),
  };
}

/** The default settings, with Windows autostart available and off. */
export function settings(overrides: Partial<SettingsDto> = {}): SettingsDto {
  return {
    idleThresholdSeconds: 300,
    idleExemptApps: [],
    excludedApps: [],
    recordWindowTitles: true,
    appsWithoutTitles: [],
    appAliases: [],
    categories: [],
    startWithWindows: false,
    workHoursStartMinutes: 9 * 60,
    workHoursEndMinutes: 17 * 60,
    discardAfterHoursReturns: false,
    afterHoursMinimumAwayMinutes: 60,
    afterHoursMaximumReturnMinutes: 10,
    ...overrides,
  };
}
