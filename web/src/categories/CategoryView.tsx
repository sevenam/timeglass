import { ApiError, errorMessage } from '../api/errors.ts';
import { useCategoryApplications } from '../api/queries.ts';
import { formatDateRange } from '../dates.ts';
import { AppShareList } from '../day/AppShareList.tsx';
import { appShares } from '../day/appShares.ts';
import { formatDuration, formatLongDate } from '../format.ts';
import { Link } from '../Link.tsx';
import { paths, type CategorySource } from '../router.ts';
import { OTHER_COLOR } from '../day/appColor.ts';
import { CategoryTimelines } from './CategoryTimelines.tsx';

export function CategoryView({
  from,
  to,
  categoryName,
  source,
}: {
  from: string;
  to: string;
  categoryName: string;
  source: CategorySource;
}) {
  const applications = useCategoryApplications(from, to, categoryName);
  const date = from === to ? formatLongDate(from) : formatDateRange(from, to);
  const rows = applications.data ? appShares(applications.data.apps) : [];
  const colors = new Map(rows.map((row) => [row.key, row.color]));
  return (
    <section
      className="app-day"
      aria-labelledby="category-heading"
      aria-busy={applications.isPending}
    >
      <p className="app-day__back">
        <Link href={paths.categoryBack(from, to, source)}>
          ← Back to {source === 'day' ? 'the day' : source === 'week' ? 'the week' : 'trends'}
        </Link>
      </p>
      <h2 id="category-heading">{categoryName}</h2>
      <p className="app-day__date">{date}</p>
      {applications.isPending ? (
        <p role="status" className="app-day__loading">
          Loading applications…
        </p>
      ) : applications.isError ? (
        <p role="alert" className="app-day__error">
          {applications.error instanceof ApiError
            ? (applications.error.problem?.errors?.name?.[0] ?? errorMessage(applications.error))
            : errorMessage(applications.error)}
        </p>
      ) : applications.data.apps.length === 0 ? (
        <p className="app-day__empty">No time matched this category in the selected period.</p>
      ) : (
        <>
          <dl className="day__totals">
            <div>
              <dt>Active in category</dt>
              <dd>{formatDuration(applications.data.activeMs)}</dd>
            </div>
          </dl>
          <CategoryTimelines
            summary={applications.data}
            categoryName={categoryName}
            colorFor={(key) => colors.get(key) ?? OTHER_COLOR}
          />
          <h3>Applications</h3>
          <AppShareList
            rows={rows}
            appHref={(appKey) => paths.categoryApp(from, to, categoryName, source, appKey)}
          />
        </>
      )}
    </section>
  );
}
