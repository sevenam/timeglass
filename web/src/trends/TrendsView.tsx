import { useId, useState } from 'react';
import { errorMessage } from '../api/errors.ts';
import { useCategoryRangeSummary, useRangeSummary } from '../api/queries.ts';
import { addDays, formatDateRange } from '../dates.ts';
import { formatDuration } from '../format.ts';
import { ViewSelector, type CategoryView } from '../categories/ViewSelector.tsx';
import { AWAY_CATEGORY_KEY } from '../categories/categoryKeys.ts';
import { Link } from '../Link.tsx';
import { paths } from '../router.ts';
import { TrendChart } from './TrendChart.tsx';
import {
  formatChange,
  trendChange,
  trendSeries,
  type AppLimit,
  type Trend,
} from './trendSeries.ts';

/** The periods to choose from, in days, ending today. */
const PERIODS = [7, 30, 90, 180, 365] as const;
/** How many apps the chart can show. */
const LIMITS: readonly AppLimit[] = [3, 5, 10, 'all'];

const DEFAULT_PERIOD = 30;
const DEFAULT_LIMIT: AppLimit = 5;
/** The value of the app picker that shows the top apps instead of one. */
const ALL_APPS = '';

interface TrendsViewProps {
  /** Today's date, `yyyy-MM-dd`; the period ends on it. */
  today: string;
  initialView?: CategoryView;
  initialPeriod?: number;
}

/**
 * How app use changes over time (#99): a line per app across the last days, weeks or months.
 * One picker chooses how many of the longest apps to draw, another picks a single app.
 */
export function TrendsView({
  today,
  initialView = 'apps',
  initialPeriod = DEFAULT_PERIOD,
}: TrendsViewProps) {
  const periodId = useId();
  const limitId = useId();
  const appId = useId();
  const [period, setPeriod] = useState<number>(initialPeriod);
  const [limit, setLimit] = useState<AppLimit>(DEFAULT_LIMIT);
  const [appKey, setAppKey] = useState<string>(ALL_APPS);
  const [view, setView] = useState<CategoryView>(initialView);

  const from = addDays(today, -(period - 1));
  const applications = useRangeSummary(from, today, { enabled: view === 'apps' });
  const categories = useCategoryRangeSummary(from, today, { enabled: view === 'categories' });
  const range = view === 'apps' ? applications : categories;
  const apps = range.data
    ? [...range.data.apps].sort((a, b) => a.app.name.localeCompare(b.app.name))
    : [];
  // An app picked for another period may not be in this one; then the top apps show again.
  const picked = apps.some((a) => a.app.key === appKey) ? appKey : ALL_APPS;

  return (
    <section className="trends" aria-labelledby="trends-heading" aria-busy={range.isPending}>
      <h2 id="trends-heading">Trends</h2>
      <p className="trends__dates">{formatDateRange(from, today)}</p>

      <div className="trends__controls">
        <ViewSelector
          value={view}
          onChange={(next) => {
            setView(next);
            setAppKey(ALL_APPS);
          }}
        />
        <div className="trends__control">
          <label htmlFor={periodId}>Period</label>
          <select
            id={periodId}
            value={period}
            onChange={(event) => setPeriod(Number(event.target.value))}
          >
            {PERIODS.map((p) => (
              <option key={p} value={p}>
                Last {p} days
              </option>
            ))}
          </select>
        </div>
        <div className="trends__control">
          <label htmlFor={limitId}>Show</label>
          <select
            id={limitId}
            value={String(limit)}
            disabled={picked !== ALL_APPS}
            onChange={(event) =>
              setLimit(event.target.value === 'all' ? 'all' : Number(event.target.value))
            }
          >
            {LIMITS.map((l) => (
              <option key={l} value={String(l)}>
                {l === 'all'
                  ? `All ${view === 'categories' ? 'categories' : 'apps'}`
                  : `Top ${l} ${view === 'categories' ? 'categories' : 'apps'}`}
              </option>
            ))}
          </select>
        </div>
        <div className="trends__control">
          <label htmlFor={appId}>{view === 'categories' ? 'Category' : 'App'}</label>
          <select id={appId} value={picked} onChange={(event) => setAppKey(event.target.value)}>
            <option value={ALL_APPS}>Top {view === 'categories' ? 'categories' : 'apps'}</option>
            {apps.map((a) => (
              <option key={a.app.key} value={a.app.key}>
                {a.app.name}
              </option>
            ))}
          </select>
        </div>
      </div>

      {range.isPending ? (
        <p role="status" className="day__loading">
          Loading the trend…
        </p>
      ) : range.isError ? (
        <p role="alert" className="day__error">
          {errorMessage(range.error)}
        </p>
      ) : range.data.apps.length === 0 ? (
        <p className="day__empty">Nothing was tracked in this period.</p>
      ) : (
        <TrendSections
          byCategory={view === 'categories'}
          from={from}
          to={today}
          trend={trendSeries(range.data, {
            limit,
            appKey: picked === ALL_APPS ? null : picked,
          })}
        />
      )}
    </section>
  );
}

function TrendSections({
  trend,
  byCategory,
  from,
  to,
}: {
  trend: Trend;
  byCategory: boolean;
  from: string;
  to: string;
}) {
  const days = trend.buckets.reduce((total, b) => total + b.days, 0);
  return (
    <>
      <TrendChart trend={trend} byCategory={byCategory} />
      <table className="app-shares trends__table">
        <caption className="visually-hidden">
          The {byCategory ? 'categories' : 'apps'} in the chart, with their time in the period and
          how it changed
        </caption>
        <thead>
          <tr>
            <th scope="col">{byCategory ? 'Category' : 'App'}</th>
            <th scope="col">Total</th>
            <th scope="col">Per day</th>
            <th scope="col" title="The second half of the period against the first">
              Change
            </th>
          </tr>
        </thead>
        <tbody>
          {trend.series.map((s) => (
            <tr key={s.key} data-app={s.key}>
              <th scope="row" className="app-shares__name">
                <span
                  className="app-shares__swatch"
                  style={{ backgroundColor: s.color }}
                  aria-hidden="true"
                />
                {byCategory && s.key !== AWAY_CATEGORY_KEY ? (
                  <Link
                    href={paths.category(from, to, s.name, 'trends')}
                    className="app-shares__link"
                  >
                    {s.name}
                  </Link>
                ) : (
                  s.name
                )}
              </th>
              <td className="app-shares__duration">{formatDuration(s.totalMs)}</td>
              <td className="app-shares__duration">{formatDuration(s.totalMs / days)}</td>
              <td className="app-shares__percent">{formatChange(trendChange(s.values))}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}
