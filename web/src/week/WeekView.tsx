import type { DayTotalDto } from '../api/client.ts';
import { errorMessage } from '../api/errors.ts';
import { useState } from 'react';
import { useCategoryRangeSummary, useRangeSummary } from '../api/queries.ts';
import { addDays, formatDateRange, formatShortDay, weekStart } from '../dates.ts';
import { AppShareList } from '../day/AppShareList.tsx';
import { OTHER_COLOR } from '../day/appColor.ts';
import { appShares } from '../day/appShares.ts';
import { formatDuration } from '../format.ts';
import { Link } from '../Link.tsx';
import { DateNav } from '../nav/DateNav.tsx';
import { ViewSelector, type CategoryView } from '../categories/ViewSelector.tsx';
import { navigate, paths, withCategoryView } from '../router.ts';
import { barSegments, describeSegments } from './weekBars.ts';

interface WeekViewProps {
  /** Any date in the week to show, `yyyy-MM-dd`; the week runs Monday to Sunday. */
  date: string;
  /** Today's date, `yyyy-MM-dd`. */
  today: string;
  view?: CategoryView;
  onViewChange?: (view: CategoryView) => void;
}

/**
 * The week at a glance (#35): each day's active time as a bar stacked by its apps, and the
 * week's apps below. A day opens in the day view.
 */
export function WeekView({ date, today, view: selectedView, onViewChange }: WeekViewProps) {
  const from = weekStart(date);
  const to = addDays(from, 6);
  const [localView, setLocalView] = useState<CategoryView>('apps');
  const view = selectedView ?? localView;
  const setView = onViewChange ?? setLocalView;
  const apps = useRangeSummary(from, to, { enabled: view === 'apps' });
  const categories = useCategoryRangeSummary(from, to, { enabled: view === 'categories' });
  const range = view === 'apps' ? apps : categories;
  const dates = formatDateRange(from, to);
  const heading = from === weekStart(today) ? `This week, ${dates}` : `Week of ${dates}`;

  return (
    <section className="week" aria-labelledby="week-heading" aria-busy={range.isPending}>
      <h2 id="week-heading">{heading}</h2>
      <DateNav view="week" date={date} today={today} categoryView={view} />
      <ViewSelector value={view} onChange={setView} />

      {range.isPending ? (
        <p role="status" className="day__loading">
          Loading the week…
        </p>
      ) : range.isError ? (
        <p role="alert" className="day__error">
          {errorMessage(range.error)}
        </p>
      ) : range.data.activeMs === 0 && range.data.apps.length === 0 ? (
        <p className="day__empty">Nothing was tracked this week.</p>
      ) : (
        <WeekSections
          days={range.data.days}
          rows={appShares(range.data.apps)}
          activeMs={range.data.activeMs}
          awayMs={range.data.awayMs}
          today={today}
          byCategory={view === 'categories'}
          from={from}
          to={to}
        />
      )}
    </section>
  );
}

interface WeekSectionsProps {
  days: readonly DayTotalDto[];
  rows: ReturnType<typeof appShares>;
  activeMs: number;
  awayMs: number;
  today: string;
  byCategory: boolean;
  from: string;
  to: string;
}

function WeekSections({
  days,
  rows,
  activeMs,
  awayMs,
  today,
  byCategory,
  from,
  to,
}: WeekSectionsProps) {
  const colors = new Map(rows.map((r) => [r.key, r.color] as const));
  const colorFor = (appKey: string) => colors.get(appKey) ?? OTHER_COLOR;
  const activeDays = days.filter((d) => d.activeMs > 0).length;
  const longest = Math.max(...days.map((d) => d.activeMs + (byCategory ? d.awayMs : 0)), 0);

  return (
    <>
      <dl className="day__totals">
        <div>
          <dt>Active</dt>
          <dd>{formatDuration(activeMs)}</dd>
        </div>
        <div>
          <dt>Away</dt>
          <dd>{formatDuration(awayMs)}</dd>
        </div>
        <div>
          <dt>Per active day</dt>
          <dd>{formatDuration(activeDays === 0 ? 0 : activeMs / activeDays)}</dd>
        </div>
      </dl>

      <h3>Day by day</h3>
      <table className="week-days">
        <caption className="visually-hidden">
          {byCategory ? 'Tracked time' : 'Active time'} per day, with each day&apos;s{' '}
          {byCategory ? 'categories' : 'apps'}
        </caption>
        <thead className="visually-hidden">
          <tr>
            <th scope="col">Day</th>
            <th scope="col">{byCategory ? 'Categories' : 'Apps'}</th>
            <th scope="col">Time</th>
          </tr>
        </thead>
        <tbody>
          {days.map((day) => (
            <WeekDayRow
              key={day.date}
              day={day}
              today={today}
              longestMs={longest}
              colorFor={colorFor}
              byCategory={byCategory}
            />
          ))}
        </tbody>
      </table>

      <h3>Where the time went</h3>
      {byCategory ? (
        <AppShareList
          rows={rows}
          byCategory
          categoryHref={(name) => paths.category(from, to, name, 'week')}
        />
      ) : (
        <AppShareList rows={rows} />
      )}
    </>
  );
}

interface WeekDayRowProps {
  day: DayTotalDto;
  today: string;
  longestMs: number;
  colorFor: (appKey: string) => string;
  byCategory: boolean;
}

function WeekDayRow({ day, today, longestMs, colorFor, byCategory }: WeekDayRowProps) {
  const isFuture = day.date > today;
  const isToday = day.date === today;
  const href = withCategoryView(
    isToday ? paths.today() : paths.day(day.date),
    byCategory ? 'categories' : 'apps',
  );
  const label = formatShortDay(day.date);
  const segments = isFuture ? [] : barSegments(day, longestMs, colorFor);

  return (
    <tr
      className={isFuture ? 'week-days__row week-days__row--future' : 'week-days__row'}
      data-date={day.date}
      // The whole row opens the day for a mouse; the day's link is the way in for the keyboard.
      onClick={
        isFuture
          ? undefined
          : (event) => {
              if (!(event.target instanceof Element && event.target.closest('a'))) {
                navigate(href);
              }
            }
      }
    >
      <th scope="row" className="week-days__day">
        {isFuture ? (
          label
        ) : (
          <Link href={href} className="week-days__link" aria-current={isToday ? 'date' : undefined}>
            {label}
          </Link>
        )}
        {isToday && <span className="week-days__today"> (today)</span>}
      </th>
      <td className="week-days__bar">
        {segments.length === 0 ? (
          <span className="week-days__track" aria-hidden="true" />
        ) : (
          <span className="week-days__track" role="img" aria-label={describeSegments(segments)}>
            {segments.map((s) => (
              <span
                key={s.key}
                className="week-days__segment"
                style={{ width: `${s.width * 100}%`, backgroundColor: s.color }}
                title={`${s.name}: ${formatDuration(s.durationMs)}`}
              />
            ))}
          </span>
        )}
      </td>
      <td className="week-days__duration">
        {isFuture ? '' : formatDuration(day.activeMs + (byCategory ? day.awayMs : 0))}
      </td>
    </tr>
  );
}
