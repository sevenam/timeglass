import { ApiError, errorMessage } from '../api/errors.ts';
import { useCategoryApplications, useCategoryAppTitles } from '../api/queries.ts';
import { TitleList } from '../app/AppView.tsx';
import { countTitles, hasNoTitles } from '../app/titleRows.ts';
import { formatDateRange } from '../dates.ts';
import { appColor } from '../day/appColor.ts';
import { appShares } from '../day/appShares.ts';
import { formatDuration, formatLongDate } from '../format.ts';
import { Link } from '../Link.tsx';
import { paths, type CategorySource } from '../router.ts';
import { CategoryTimelines } from './CategoryTimelines.tsx';

export function CategoryAppView({
  from,
  to,
  categoryName,
  source,
  appKey,
}: {
  from: string;
  to: string;
  categoryName: string;
  source: CategorySource;
  appKey: string;
}) {
  const titles = useCategoryAppTitles(from, to, categoryName, appKey);
  const applications = useCategoryApplications(from, to, categoryName);
  const color = applications.data
    ? (appShares(applications.data.apps).find((row) => row.key === appKey)?.color ??
      appColor(appKey))
    : appColor(appKey);
  const name = titles.data?.app.name ?? appKey;
  const date = from === to ? formatLongDate(from) : formatDateRange(from, to);
  return (
    <section
      className="app-day"
      aria-labelledby="category-app-heading"
      aria-busy={titles.isPending}
    >
      <p className="app-day__back">
        <Link href={paths.category(from, to, categoryName, source)}>← Back to {categoryName}</Link>
      </p>
      <h2 id="category-app-heading">
        <span className="app-day__swatch" style={{ backgroundColor: color }} aria-hidden="true" />
        {name}
      </h2>
      <p className="app-day__date">
        {categoryName} · {date}
      </p>
      {titles.isPending ? (
        <p role="status" className="app-day__loading">
          Loading the titles…
        </p>
      ) : titles.isError ? (
        <p role="alert" className="app-day__error">
          {titles.error instanceof ApiError
            ? (titles.error.problem?.errors?.name?.[0] ?? errorMessage(titles.error))
            : errorMessage(titles.error)}
        </p>
      ) : titles.data.totalMs === 0 ? (
        <p className="app-day__empty">
          No time in this application matched the category in this period.
        </p>
      ) : (
        <>
          <dl className="day__totals">
            <div>
              <dt>Active in category</dt>
              <dd>{formatDuration(titles.data.totalMs)}</dd>
            </div>
            {!hasNoTitles(titles.data) && (
              <div>
                <dt>Titles</dt>
                <dd>{countTitles(titles.data.titles)}</dd>
              </div>
            )}
          </dl>
          {applications.isPending ? (
            <p role="status">Loading the timeline…</p>
          ) : applications.isError ? (
            <p role="alert">{errorMessage(applications.error)}</p>
          ) : (
            <CategoryTimelines
              summary={applications.data}
              categoryName={categoryName}
              appKey={appKey}
              colorFor={() => color}
            />
          )}
          {hasNoTitles(titles.data) ? (
            <p className="app-day__empty">
              No window titles are available for this time. Titles may not have been recorded or may
              have been removed by retention.
            </p>
          ) : (
            <TitleList
              day={titles.data}
              color={color}
              missingTitleLabel="No title recorded or retained"
            />
          )}
        </>
      )}
    </section>
  );
}
