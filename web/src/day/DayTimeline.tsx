import { useState, type KeyboardEvent } from 'react';
import type { TimelineBlockDto } from '../api/client.ts';
import { errorMessage } from '../api/errors.ts';
import { useTimeline } from '../api/queries.ts';
import { blockDetails, blockLabel, layoutTimeline } from './timelineLayout.ts';

interface DayTimelineProps {
  /** The local date to show, `yyyy-MM-dd`. */
  date: string;
  /** The colour of an app, the same one the app list uses. */
  colorFor: (appKey: string) => string;
  byCategory?: boolean;
  colorByGroup?: boolean;
  includeBlock?: (block: TimelineBlockDto) => boolean;
}

/**
 * When during the day the time went where (#33): one lane, coloured by app, with away time as
 * faint gaps and untracked time left blank. Hovering or focusing a block shows its app, clock
 * times and duration. The lane is one tab stop; the arrow keys, Home and End move between blocks.
 */
export function DayTimeline({
  date,
  colorFor,
  byCategory = false,
  colorByGroup = false,
  includeBlock,
}: DayTimelineProps) {
  const timeline = useTimeline(date, byCategory);
  const [hovered, setHovered] = useState<number | null>(null);
  const [focused, setFocused] = useState<number | null>(null);
  const [current, setCurrent] = useState(0);

  if (timeline.isPending) {
    return (
      <p role="status" className="timeline__loading">
        Loading the timeline…
      </p>
    );
  }
  if (timeline.isError) {
    return (
      <p role="alert" className="timeline__error">
        {errorMessage(timeline.error)}
      </p>
    );
  }

  const layout = layoutTimeline(timeline.data, colorFor, { colorByGroup, includeBlock });
  if (layout === null) {
    return includeBlock ? <p>No matching timeline data is available for this day.</p> : null;
  }

  const segments = layout.segments;
  const roving = Math.min(current, segments.length - 1);
  const shown = hovered ?? focused;
  const tip = shown === null ? null : segments[shown];

  function onKeyDown(event: KeyboardEvent<HTMLButtonElement>, index: number) {
    const next =
      event.key === 'ArrowRight'
        ? Math.min(index + 1, segments.length - 1)
        : event.key === 'ArrowLeft'
          ? Math.max(index - 1, 0)
          : event.key === 'Home'
            ? 0
            : event.key === 'End'
              ? segments.length - 1
              : null;
    if (next === null) {
      return;
    }
    event.preventDefault();
    setCurrent(next);
    const lane = event.currentTarget.parentElement;
    lane?.querySelectorAll<HTMLButtonElement>('button')[next]?.focus();
  }

  return (
    <>
      {includeBlock && segments.length === 0 && (
        <p>No matching timeline data is available for this day.</p>
      )}
      <figure className="timeline">
        <div
          className="timeline__lane"
          role="group"
          aria-label="Timeline of the day. Use the arrow keys to move between blocks."
        >
          {segments.map((s, i) => (
            <button
              key={`${s.block.start}-${i}`}
              type="button"
              className={`timeline__block timeline__block--${colorByGroup ? 'active' : s.block.kind}`}
              style={{
                left: `${s.left}%`,
                width: `${s.width}%`,
                backgroundColor: s.color ?? undefined,
              }}
              tabIndex={i === roving ? 0 : -1}
              aria-label={blockLabel(s.block)}
              data-app={s.block.app?.key ?? 'away'}
              onMouseEnter={() => setHovered(i)}
              onMouseLeave={() => setHovered(null)}
              onFocus={() => {
                setFocused(i);
                setCurrent(i);
              }}
              onBlur={() => setFocused(null)}
              onKeyDown={(e) => onKeyDown(e, i)}
            />
          ))}
          {tip && <TimelineTooltip left={tip.left + tip.width / 2} {...blockDetails(tip.block)} />}
        </div>
        <div className="timeline__axis" aria-hidden="true">
          {layout.ticks.map((t) => (
            <span
              key={t.label + t.left}
              className={`timeline__tick${t.left <= 0 ? ' timeline__tick--start' : t.left >= 100 ? ' timeline__tick--end' : ''}${t.major ? '' : ' timeline__tick--minor'}`}
              style={{ left: `${t.left}%` }}
            >
              {t.label}
            </span>
          ))}
        </div>
        <figcaption className="visually-hidden">
          Colours match the {colorByGroup ? 'category' : 'app'} list.
          {!colorByGroup && !includeBlock && ' Faint stretches are away time.'}
        </figcaption>
      </figure>
    </>
  );
}

interface TimelineTooltipProps {
  left: number;
  name: string;
  time: string;
  duration: string;
}

function TimelineTooltip({ left, name, time, duration }: TimelineTooltipProps) {
  // Kept away from the edges so it does not spill out of the lane.
  const anchor = Math.min(Math.max(left, 12), 88);
  return (
    <div className="timeline__tooltip" role="tooltip" style={{ left: `${anchor}%` }}>
      <strong>{name}</strong>
      <span>
        {time} · {duration}
      </span>
    </div>
  );
}
