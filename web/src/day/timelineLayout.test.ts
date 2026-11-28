import type { TimelineBlockDto, TimelineDto } from '../api/client.ts';
import { apps, demoDate, timeline } from '../test/fixtures.ts';
import {
  blockDetails,
  blockLabel,
  hourTicks,
  layoutTimeline,
  position,
  visibleRange,
} from './timelineLayout.ts';

const at = (time: string, date = demoDate) => Date.parse(`${date}T${time}+01:00`);

const block = (overrides: Partial<TimelineBlockDto> = {}): TimelineBlockDto => ({
  kind: 'active',
  app: apps.code,
  start: `${demoDate}T08:00:00+01:00`,
  end: `${demoDate}T09:00:00+01:00`,
  durationMs: 3_600_000,
  isOpen: false,
  category: null,
  ...overrides,
});

describe('position', () => {
  const range = { startMs: at('08:00:00'), endMs: at('18:00:00') };

  it('turns an interval into left and width in percent', () => {
    expect(position(range, at('08:00:00'), at('09:00:00'))).toEqual({ left: 0, width: 10 });
    expect(position(range, at('13:00:00'), at('15:30:00'))).toEqual({ left: 50, width: 25 });
  });

  it('cuts off the part outside the range', () => {
    expect(position(range, at('07:00:00'), at('09:00:00'))).toEqual({ left: 0, width: 10 });
    expect(position(range, at('17:00:00'), at('19:00:00'))).toEqual({ left: 90, width: 10 });
  });

  it('gives an interval wholly outside the range no width', () => {
    expect(position(range, at('06:00:00'), at('07:00:00'))).toEqual({ left: 0, width: 0 });
    expect(position(range, at('19:00:00'), at('20:00:00'))).toEqual({ left: 100, width: 0 });
  });

  it('never gives a negative width for an end before the start', () => {
    expect(position(range, at('10:00:00'), at('09:00:00')).width).toBe(0);
  });

  it('copes with an empty range', () => {
    expect(position({ startMs: 5, endMs: 5 }, 0, 10)).toEqual({ left: 0, width: 0 });
  });
});

describe('visibleRange', () => {
  it('runs from the hour of the first block to the hour after the last', () => {
    expect(visibleRange(timeline())).toEqual({ startMs: at('08:00:00'), endMs: at('17:00:00') });
  });

  it('keeps an end that is already on the hour', () => {
    const day = { ...timeline(), blocks: [block({ end: `${demoDate}T10:00:00+01:00` })] };
    expect(visibleRange(day)).toEqual({ startMs: at('08:00:00'), endMs: at('10:00:00') });
  });

  it('is at least an hour wide', () => {
    const day = {
      ...timeline(),
      blocks: [block({ start: `${demoDate}T08:00:00+01:00`, end: `${demoDate}T08:00:00+01:00` })],
    };
    expect(visibleRange(day)).toEqual({ startMs: at('08:00:00'), endMs: at('09:00:00') });
  });

  it('stays inside the day for a block that runs past midnight', () => {
    const day: TimelineDto = {
      ...timeline(),
      blocks: [block({ start: `${demoDate}T22:30:00+01:00`, end: '2026-11-06T00:40:00+01:00' })],
    };
    expect(visibleRange(day)).toEqual({
      startMs: at('22:00:00'),
      endMs: at('00:00:00', '2026-11-06'),
    });
  });

  it('is null for a day without blocks', () => {
    expect(visibleRange({ ...timeline(), blocks: [] })).toBeNull();
  });
});

describe('hourTicks', () => {
  const dayStart = `${demoDate}T00:00:00+01:00`;

  it('marks every hour on a short day', () => {
    const ticks = hourTicks(dayStart, { startMs: at('08:00:00'), endMs: at('12:00:00') });
    expect(ticks).toEqual([
      { label: '08:00', left: 0, major: true },
      { label: '09:00', left: 25, major: false },
      { label: '10:00', left: 50, major: true },
      { label: '11:00', left: 75, major: false },
      { label: '12:00', left: 100, major: true },
    ]);
  });

  it('thins the marks out on a long day', () => {
    const ticks = hourTicks(dayStart, { startMs: at('07:00:00'), endMs: at('21:00:00') });
    expect(ticks.map((t) => t.label)).toEqual([
      '08:00',
      '10:00',
      '12:00',
      '14:00',
      '16:00',
      '18:00',
      '20:00',
    ]);
  });

  it('labels midnight at the end of the day as 00:00', () => {
    const ticks = hourTicks(dayStart, {
      startMs: at('22:00:00'),
      endMs: at('00:00:00', '2026-11-06'),
    });
    expect(ticks.map((t) => t.label)).toEqual(['22:00', '23:00', '00:00']);
  });
});

describe('layoutTimeline', () => {
  it('places every block and colours active ones by app', () => {
    const layout = layoutTimeline(timeline(), (key) => `colour-${key}`);

    expect(layout).not.toBeNull();
    const segments = layout?.segments ?? [];
    expect(segments).toHaveLength(7);
    expect(segments.map((s) => s.color)).toEqual([
      'colour-outlook',
      'colour-vscode',
      'colour-teams',
      'colour-edge',
      null,
      'colour-terminal',
      'colour-vscode',
    ]);
    // 08:00–17:00 is nine hours: lunch runs 11:40–12:20.
    const away = segments[4];
    expect(away?.left).toBeCloseTo((220 / 540) * 100);
    expect(away?.width).toBeCloseTo((40 / 540) * 100);
  });

  it('leaves untracked time between blocks blank', () => {
    const segments = layoutTimeline(timeline(), () => 'c')?.segments ?? [];
    for (let i = 1; i < segments.length; i++) {
      const prev = segments[i - 1];
      const curr = segments[i];
      if (prev && curr) {
        expect(curr.left).toBeGreaterThanOrEqual(prev.left + prev.width - 1e-9);
      }
    }
  });

  it('is null for an empty day', () => {
    expect(layoutTimeline({ ...timeline(), blocks: [] }, () => 'c')).toBeNull();
  });
});

describe('blockDetails', () => {
  it('names the app, the clock times and the duration', () => {
    expect(
      blockDetails(
        block({
          start: `${demoDate}T09:47:00+01:00`,
          end: `${demoDate}T11:40:00+01:00`,
          durationMs: 113 * 60_000 - 3_000,
          app: apps.edge,
        }),
      ),
    ).toEqual({ name: 'Microsoft Edge', time: '09:47–11:40', duration: '1h 52m' });
  });

  it('calls away time "Away"', () => {
    expect(blockDetails(block({ kind: 'away', app: null })).name).toBe('Away');
  });

  it('ends a running block "now"', () => {
    expect(blockDetails(block({ isOpen: true })).time).toBe('08:00–now');
  });

  it('reads as one sentence for screen readers', () => {
    expect(blockLabel(block())).toBe('Visual Studio Code, 08:00–09:00, 1h');
  });
});
