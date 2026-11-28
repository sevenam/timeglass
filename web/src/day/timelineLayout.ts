import type { TimelineBlockDto, TimelineDto } from '../api/client.ts';
import { formatClockTime, formatDuration } from '../format.ts';

const HOUR_MS = 3_600_000;

/** The stretch of the day the timeline shows, as epoch milliseconds. */
export interface TimeRange {
  startMs: number;
  endMs: number;
}

/** Where something sits on the timeline, in percent of its width. */
export interface Position {
  left: number;
  width: number;
}

/** One block placed on the timeline. */
export interface TimelineSegment extends Position {
  block: TimelineBlockDto;
  color: string | null;
}

/** An hour mark under the timeline. */
export interface HourTick {
  label: string;
  left: number;
  /** Every other mark is minor, so a narrow screen can hide those and keep the labels apart. */
  major: boolean;
}

export interface TimelineLayout {
  range: TimeRange;
  segments: TimelineSegment[];
  ticks: HourTick[];
}

/**
 * The part of the day worth drawing: from the hour the first block starts in to the hour the last
 * one ends in, so a 08:12–16:05 day fills the width instead of a third of it. Hours are counted
 * from the start of the day, so they line up with the clock except on a daylight saving change.
 * `null` when nothing was tracked.
 */
export function visibleRange(timeline: TimelineDto): TimeRange | null {
  if (timeline.blocks.length === 0) {
    return null;
  }
  const dayStart = Date.parse(timeline.start);
  const dayEnd = Date.parse(timeline.end);
  const first = Math.max(dayStart, Math.min(...timeline.blocks.map((b) => Date.parse(b.start))));
  const last = Math.min(dayEnd, Math.max(...timeline.blocks.map((b) => Date.parse(b.end))));

  const startMs = dayStart + Math.floor((first - dayStart) / HOUR_MS) * HOUR_MS;
  const endMs = Math.min(dayEnd, dayStart + Math.ceil((last - dayStart) / HOUR_MS) * HOUR_MS);
  // A day whose only block is a few seconds on the hour still gets one hour of width.
  return { startMs, endMs: Math.max(endMs, Math.min(dayEnd, startMs + HOUR_MS)) };
}

/**
 * Where the interval from `startMs` to `endMs` sits in `range`, in percent. The part outside the
 * range is cut off, so a block that runs past midnight stops at the edge.
 */
export function position(range: TimeRange, startMs: number, endMs: number): Position {
  const span = range.endMs - range.startMs;
  if (span <= 0) {
    return { left: 0, width: 0 };
  }
  const from = Math.min(Math.max(startMs, range.startMs), range.endMs);
  const to = Math.min(Math.max(endMs, from), range.endMs);
  return {
    left: ((from - range.startMs) / span) * 100,
    width: ((to - from) / span) * 100,
  };
}

/** Hour marks across `range`: every hour when they fit, every 2, 3 or 6 hours on a long day. */
export function hourTicks(dayStartIso: string, range: TimeRange): HourTick[] {
  const dayStart = Date.parse(dayStartIso);
  const hours = (range.endMs - range.startMs) / HOUR_MS;
  const step = hours <= 10 ? 1 : hours <= 18 ? 2 : hours <= 24 ? 3 : 6;
  const firstHour = Math.round((range.startMs - dayStart) / HOUR_MS);
  const ticks: HourTick[] = [];
  for (
    let h = Math.ceil(firstHour / step) * step;
    dayStart + h * HOUR_MS <= range.endMs;
    h += step
  ) {
    ticks.push({
      label: `${String(h % 24).padStart(2, '0')}:00`,
      left: position(range, dayStart + h * HOUR_MS, dayStart + h * HOUR_MS).left,
      major: (h / step) % 2 === 0,
    });
  }
  return ticks;
}

/**
 * Places every block of the day on the timeline. Active blocks get their app's colour from
 * `colorFor` (so the timeline matches the app list); away blocks get `null` and are drawn as gaps.
 */
export function layoutTimeline(
  timeline: TimelineDto,
  colorFor: (appKey: string) => string,
  options: {
    colorByGroup?: boolean;
    includeBlock?: ((block: TimelineBlockDto) => boolean) | undefined;
  } = {},
): TimelineLayout | null {
  const range = visibleRange(timeline);
  if (range === null) {
    return null;
  }
  return {
    range,
    segments: timeline.blocks
      .filter((block) => options.includeBlock?.(block) ?? true)
      .map((block) => ({
        block,
        color: options.colorByGroup
          ? colorFor(block.kind === 'away' ? 'away' : (block.category?.key ?? 'ungrouped'))
          : block.kind === 'active' && block.app
            ? colorFor(block.app.key)
            : null,
        ...position(range, Date.parse(block.start), Date.parse(block.end)),
      })),
    ticks: hourTicks(timeline.start, range),
  };
}

/** What the tooltip says about a block. */
export interface BlockDetails {
  name: string;
  time: string;
  duration: string;
}

/** The tooltip content for a block: the app (or "Away"), its clock times and how long it was. */
export function blockDetails(block: TimelineBlockDto): BlockDetails {
  const name = block.kind === 'away' || !block.app ? 'Away' : block.app.name;
  const end = block.isOpen ? 'now' : formatClockTime(block.end);
  return {
    name,
    time: `${formatClockTime(block.start)}–${end}`,
    duration: formatDuration(block.durationMs),
  };
}

/** The block's details as one sentence, for screen readers. */
export function blockLabel(block: TimelineBlockDto): string {
  const d = blockDetails(block);
  return `${d.name}, ${d.time}, ${d.duration}`;
}
