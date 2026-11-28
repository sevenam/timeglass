import { useId, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import type { AppDayDto, DaySummaryDto } from '../api/client.ts';
import { errorMessage } from '../api/errors.ts';
import { queryKeys, useAppDay } from '../api/queries.ts';
import { appColor } from '../day/appColor.ts';
import { appShares } from '../day/appShares.ts';
import { DayTimeline } from '../day/DayTimeline.tsx';
import { formatDuration, formatLongDate } from '../format.ts';
import { Link } from '../Link.tsx';
import { paths } from '../router.ts';
import { countTitles, filterTitles, hasNoTitles } from './titleRows.ts';

interface AppViewProps {
  /** The local date, `yyyy-MM-dd`. */
  date: string;
  /** The app's key, as in the API, e.g. `outlook`. */
  appKey: string;
  /** True when `date` is today, which only changes the wording and the way back. */
  isToday?: boolean;
}

/**
 * The app drill-down (#34): which window titles (mails, documents, projects) an app's time on a
 * day went to, with a search box to find one. Every title is listed, in a list that scrolls on
 * its own when there are many (#100), and how many different titles there are (#109).
 */
export function AppView({ date, appKey, isToday = false }: AppViewProps) {
  const appDay = useAppDay(date, appKey);
  const summary = useQueryClient().getQueryData<DaySummaryDto>(queryKeys.daySummary(date));
  const color = summary
    ? (appShares(summary.apps).find((row) => row.key === appKey)?.color ?? appColor(appKey))
    : (appDay.data?.color ?? appColor(appKey));
  const name = appDay.data?.app.name ?? appKey;
  const longDate = formatLongDate(date);

  return (
    <section className="app-day" aria-labelledby="app-day-heading" aria-busy={appDay.isPending}>
      <p className="app-day__back">
        <Link href={isToday ? paths.today() : paths.day(date)}>
          ← Back to {isToday ? 'today' : 'the day'}
        </Link>
      </p>
      <h2 id="app-day-heading">
        <span className="app-day__swatch" style={{ backgroundColor: color }} aria-hidden="true" />
        {name}
      </h2>
      <p className="app-day__date">{isToday ? `Today, ${longDate}` : longDate}</p>

      {appDay.isPending ? (
        <p role="status" className="app-day__loading">
          Loading the titles…
        </p>
      ) : appDay.isError ? (
        <p role="alert" className="app-day__error">
          {errorMessage(appDay.error)}
        </p>
      ) : appDay.data.totalMs === 0 ? (
        <p className="app-day__empty">
          No time in {name} {isToday ? 'yet today' : 'on this day'}.
        </p>
      ) : (
        <>
          <dl className="day__totals">
            <div>
              <dt>Active</dt>
              <dd>{formatDuration(appDay.data.totalMs)}</dd>
            </div>
            {!hasNoTitles(appDay.data) && (
              <div>
                <dt>Titles</dt>
                <dd>{countTitles(appDay.data.titles)}</dd>
              </div>
            )}
          </dl>
          <h3>When</h3>
          <DayTimeline
            date={date}
            colorFor={() => color}
            includeBlock={(block) => block.kind === 'active' && block.app?.key === appKey}
          />
          {hasNoTitles(appDay.data) ? (
            <p className="app-day__empty">
              No window titles were recorded for {name}, so its time can&apos;t be split by mail,
              document or project. Titles aren&apos;t recorded while title recording is off, or for
              apps excluded from it.
            </p>
          ) : (
            <TitleList day={appDay.data} color={color} />
          )}
        </>
      )}
    </section>
  );
}

export function TitleList({
  day,
  missingTitleLabel = 'No title recorded',
  color = day.color ?? appColor(day.app.key),
}: {
  day: Pick<AppDayDto, 'app' | 'titles'> & { color?: string | null };
  missingTitleLabel?: string;
  color?: string;
}) {
  const [query, setQuery] = useState('');
  const searchId = useId();
  const rows = filterTitles(day.titles, query);
  const total = countTitles(day.titles);
  const filtering = query.trim() !== '';
  const longest = Math.max(...day.titles.map((t) => t.durationMs), 1);

  return (
    <>
      <div className="app-day__search">
        <label htmlFor={searchId}>Filter titles</label>
        <input
          id={searchId}
          type="search"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          autoComplete="off"
          spellCheck={false}
        />
        {filtering && rows.length > 0 && (
          <span className="app-day__count" aria-live="polite">
            {rows.length} of {total} {total === 1 ? 'title' : 'titles'}
          </span>
        )}
      </div>

      {rows.length === 0 ? (
        <p role="status" className="app-day__empty">
          No titles match “{query.trim()}”.
        </p>
      ) : (
        <div
          className="app-titles__scroll"
          role="region"
          aria-label={`Window titles in ${day.app.name}`}
          tabIndex={0}
        >
          <table className="app-shares app-titles">
            <caption className="visually-hidden">
              Time in {day.app.name} per window title, longest first
            </caption>
            <thead className="visually-hidden">
              <tr>
                <th scope="col">Title</th>
                <th scope="col">Time</th>
                <th scope="col">Share</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <TitleRowView
                  key={row.title ?? '\0none'}
                  label={row.title ?? missingTitleLabel}
                  muted={row.title === null}
                  title={row.title ?? undefined}
                  durationMs={row.durationMs}
                  percent={row.percent}
                  width={row.durationMs / longest}
                  color={color}
                />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

interface TitleRowViewProps {
  label: string;
  /** The full title as a tooltip, since a long one is cut off. */
  title?: string | undefined;
  muted: boolean;
  durationMs: number;
  percent: number;
  width: number;
  color: string;
}

function TitleRowView({
  label,
  title,
  muted,
  durationMs,
  percent,
  width,
  color,
}: TitleRowViewProps) {
  return (
    <tr>
      <th scope="row" className="app-shares__name">
        <span
          className={muted ? 'app-titles__title app-titles__title--muted' : 'app-titles__title'}
          title={title}
        >
          {label}
        </span>
        <span className="app-shares__track" aria-hidden="true">
          <span
            className="app-shares__bar"
            style={{ width: `${width * 100}%`, backgroundColor: color }}
          />
        </span>
      </th>
      <td className="app-shares__duration">{formatDuration(durationMs)}</td>
      <td className="app-shares__percent">{percent}%</td>
    </tr>
  );
}
