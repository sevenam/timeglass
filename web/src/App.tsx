import { useStatus } from './api/queries';
import { AppView } from './app/AppView.tsx';
import { DayView } from './day/DayView.tsx';
import { localToday } from './format.ts';
import { CategoryView } from './categories/CategoryView.tsx';
import { CategoryAppView } from './categories/CategoryAppView.tsx';
import type { CategoryView as CategoryViewMode } from './categories/ViewSelector.tsx';
import { Link } from './Link.tsx';
import { navigate, paths, useRoute, withCategoryView, type Route } from './router.ts';
import { SettingsView } from './settings/SettingsView.tsx';
import { TrackerControl } from './status/TrackerControl.tsx';
import { TrendsView } from './trends/TrendsView.tsx';
import { WeekView } from './week/WeekView.tsx';

type ApiStatus = 'checking' | 'online' | 'offline';

/**
 * The dashboard shell: the tracker's state and the screen the URL names. Today is the tracker's
 * own date (from `/api/status`), falling back to the browser's date when the tracker can't be
 * reached.
 */
export function App() {
  const route = useRoute();
  const { data, isPending, isError } = useStatus();
  const status: ApiStatus = isPending ? 'checking' : isError ? 'offline' : 'online';
  const today = data?.today ?? localToday();

  return (
    <main className="app">
      <header className="app__header">
        <h1>
          <Link href={paths.today()} className="app__home">
            Timeglass
          </Link>
        </h1>
        <div className="app__status">
          <p role="status" className="app__tracker">
            Tracker: {status}
          </p>
          {data && <TrackerControl status={data} />}
          <nav className="app__nav" aria-label="Main navigation">
            <Link
              href={
                'view' in route && route.view === 'categories'
                  ? withCategoryView(paths.today(), 'categories')
                  : paths.today()
              }
              className="app__nav-link"
              aria-current={route.name === 'today' || route.name === 'day' ? 'page' : undefined}
            >
              Usage
            </Link>
            <Link
              href={paths.trends()}
              className="app__nav-link"
              aria-current={route.name === 'trends' ? 'page' : undefined}
            >
              Trends
            </Link>
            <Link
              href={paths.settings()}
              className="app__nav-link"
              aria-current={route.name === 'settings' ? 'page' : undefined}
            >
              Settings
            </Link>
          </nav>
        </div>
      </header>
      {!isPending && <Screen route={route} today={today} />}
    </main>
  );
}

function Screen({ route, today }: { route: Route; today: string }) {
  const setView = (view: CategoryViewMode) =>
    navigate(withCategoryView(globalThis.location.pathname, view));
  switch (route.name) {
    case 'today':
      return (
        <DayView date={today} today={today} view={route.view ?? 'apps'} onViewChange={setView} />
      );
    case 'day':
      return (
        <DayView
          date={route.date}
          today={today}
          view={route.view ?? 'apps'}
          onViewChange={setView}
        />
      );
    case 'app':
      return <AppView date={route.date} appKey={route.appKey} isToday={route.date === today} />;
    case 'thisWeek':
      return (
        <WeekView date={today} today={today} view={route.view ?? 'apps'} onViewChange={setView} />
      );
    case 'week':
      return (
        <WeekView
          date={route.date}
          today={today}
          view={route.view ?? 'apps'}
          onViewChange={setView}
        />
      );
    case 'trends':
      return (
        <TrendsView
          today={today}
          initialView={route.view ?? 'apps'}
          initialPeriod={route.period ?? 30}
        />
      );
    case 'category':
      return (
        <CategoryView
          from={route.from}
          to={route.to}
          categoryName={route.categoryName}
          source={route.source}
        />
      );
    case 'categoryApp':
      return (
        <CategoryAppView
          from={route.from}
          to={route.to}
          categoryName={route.categoryName}
          source={route.source}
          appKey={route.appKey}
        />
      );
    case 'settings':
      return <SettingsView today={today} />;
    case 'notFound':
      return (
        <section className="day">
          <h2>Page not found</h2>
          <p>
            <Link href={paths.today()}>Go to today</Link>
          </p>
        </section>
      );
  }
}
