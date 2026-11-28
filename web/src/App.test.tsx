import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { App } from './App.tsx';
import { apps, demoDate, rangeSummary, timeline } from './test/fixtures.ts';
import { badRequest, unauthorized } from './test/handlers.ts';
import { renderWithApi } from './test/render.tsx';
import { server } from './test/server.ts';

describe('App', () => {
  it('shows the tracker as online when the API responds', async () => {
    renderWithApi(<App />);

    expect(await screen.findByText('Tracker: online')).toBeInTheDocument();
  });

  it('shows the tracker as offline when the API fails', async () => {
    server.use(http.get('/api/status', () => new HttpResponse(null, { status: 500 })));

    renderWithApi(<App />);

    expect(await screen.findByText('Tracker: offline')).toBeInTheDocument();
  });

  it('shows the tracker as offline on network errors', async () => {
    server.use(http.get('/api/status', () => HttpResponse.error()));

    renderWithApi(<App />);

    expect(await screen.findByText('Tracker: offline')).toBeInTheDocument();
  });

  it('asks to open the dashboard from the tray icon when not signed in', async () => {
    server.use(http.get('/api/status', unauthorized));

    renderWithApi(<App />);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Open the dashboard from the Timeglass tray icon to sign in.',
    );
    expect(screen.queryByText(/Tracker:/)).not.toBeInTheDocument();
  });
});

describe('App routes', () => {
  it('keeps the selected category view across day and week navigation and browser history', async () => {
    server.use(
      http.get('/api/range/categories', ({ request }) => {
        const query = new URL(request.url).searchParams;
        return HttpResponse.json(
          rangeSummary(query.get('from') ?? demoDate, query.get('to') ?? demoDate),
        );
      }),
    );
    const user = userEvent.setup();
    renderWithApi(<App />);
    await screen.findByRole('table', { name: 'Active time per app, longest first' });
    const daySelector = screen.getByRole('group', { name: 'View by' });
    expect(
      daySelector.compareDocumentPosition(screen.getByRole('heading', { name: 'When' })) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();

    await user.click(screen.getByRole('button', { name: 'Category' }));
    expect(globalThis.location.search).toBe('?view=categories');
    await user.click(screen.getByRole('link', { name: 'Week' }));
    expect(globalThis.location.pathname + globalThis.location.search).toBe(
      '/week/2026-11-02?view=categories',
    );
    expect(screen.getByRole('button', { name: 'Category' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    const table = await screen.findByRole('table', { name: /each day.s categories/ });
    expect(
      screen.getByRole('group', { name: 'View by' }).compareDocumentPosition(table) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    await user.click(within(table).getByRole('link', { name: /Wed/ }));
    expect(globalThis.location.pathname + globalThis.location.search).toBe(
      '/day/2026-11-04?view=categories',
    );
    expect(screen.getByRole('button', { name: 'Category' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await user.click(screen.getByRole('button', { name: 'Application' }));
    expect(globalThis.location.search).toBe('');
    await user.click(screen.getByRole('link', { name: 'Week' }));
    expect(screen.getByRole('button', { name: 'Application' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );

    act(() => {
      globalThis.history.replaceState(null, '', '/week/2026-11-02?view=categories');
      globalThis.dispatchEvent(new PopStateEvent('popstate'));
    });
    expect(screen.getByRole('button', { name: 'Category' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
  });

  it('filters category and application timelines by category on every matching day', async () => {
    server.use(
      http.get('/api/days/:date/timeline', ({ params }) => {
        const data = timeline(String(params.date));
        return HttpResponse.json({
          ...data,
          blocks: data.blocks.map((block, i) => ({
            ...block,
            category:
              block.kind === 'active'
                ? { key: i === 6 ? 'category-1' : 'category-0', name: i === 6 ? 'Other' : 'Work' }
                : null,
          })),
        });
      }),
      http.get('/api/range/category-application-titles', () =>
        HttpResponse.json({
          app: apps.code,
          totalMs: 60000,
          titles: [{ title: 'Work', durationMs: 60000, share: 1, percent: 100 }],
        }),
      ),
    );
    globalThis.history.replaceState(
      null,
      '',
      '/category/2026-11-04/2026-11-05?category=Work&source=week',
    );
    const user = userEvent.setup();
    renderWithApi(<App />);
    const lanes = await screen.findAllByRole('group', { name: /Timeline of the day/ });
    await waitFor(() =>
      expect(screen.getAllByRole('group', { name: /Timeline of the day/ })).toHaveLength(2),
    );
    const first = lanes[0];
    if (!first) throw new Error('Expected a timeline');
    expect(first.querySelectorAll('[data-app="vscode"]')).toHaveLength(1);
    expect(first.querySelector('[data-app="away"]')).toBeNull();
    const appList = screen.getByRole('table', { name: 'Active time per app, longest first' });
    expect(first.querySelector<HTMLElement>('[data-app="vscode"]')?.style.backgroundColor).toBe(
      appList.querySelector<HTMLElement>('tr[data-app="vscode"] .app-shares__swatch')?.style
        .backgroundColor,
    );
    await user.click(within(appList).getByRole('link', { name: 'Visual Studio Code' }));
    await screen.findByRole('table', { name: /Time in Visual Studio Code/ });
    await waitFor(() =>
      expect(screen.getAllByRole('group', { name: /Timeline of the day/ })).toHaveLength(2),
    );
    for (const lane of screen.getAllByRole('group', { name: /Timeline of the day/ })) {
      expect(within(lane).getAllByRole('button')).toHaveLength(1);
      expect(within(lane).getByRole('button')).toHaveAttribute('data-app', 'vscode');
    }
  });

  it.each([
    ['/day/2026-11-05?view=categories', '2026-11-05', '2026-11-05', 'day'],
    ['/week/2026-11-02?view=categories', '2026-11-02', '2026-11-08', 'week'],
    ['/trends?view=categories&period=7', '2026-10-30', '2026-11-05', 'trends'],
  ])(
    'opens a filtered category from %s and returns to the same view',
    async (url, from, to, source) => {
      let detailQuery = '';
      let titleQuery = '';
      server.use(
        http.get('/api/range/categories', ({ request }) => {
          const query = new URL(request.url).searchParams;
          const range = rangeSummary(query.get('from') ?? from, query.get('to') ?? to);
          const rename = (app: (typeof range.apps)[number]) => ({
            ...app,
            app: { key: 'category-0', name: 'R&D / Prizm' },
          });
          return HttpResponse.json({
            ...range,
            apps: range.apps.slice(0, 1).map(rename),
            days: range.days.map((day) => ({ ...day, apps: day.apps.slice(0, 1).map(rename) })),
          });
        }),
        http.get('/api/range/category-applications', ({ request }) => {
          detailQuery = new URL(request.url).search;
          const range = rangeSummary(from, to);
          return HttpResponse.json(range);
        }),
        http.get('/api/range/category-application-titles', ({ request }) => {
          titleQuery = new URL(request.url).search;
          return HttpResponse.json({
            app: apps.code,
            totalMs: 120000,
            titles: [
              { title: 'Matching Prizm project', durationMs: 60000, share: 0.5, percent: 50 },
              { title: null, durationMs: 60000, share: 0.5, percent: 50 },
            ],
          });
        }),
      );
      globalThis.history.replaceState(null, '', url);
      const user = userEvent.setup();
      renderWithApi(<App />);
      await user.click(await screen.findByRole('link', { name: 'R&D / Prizm' }));
      expect(await screen.findByRole('heading', { name: 'R&D / Prizm' })).toBeInTheDocument();
      const applications = await screen.findByRole('table', {
        name: 'Active time per app, longest first',
      });
      expect(applications).toHaveTextContent('Visual Studio Code');
      const listColor = applications.querySelector<HTMLElement>(
        'tr[data-app="vscode"] .app-shares__swatch',
      )?.style.backgroundColor;
      expect(listColor).toBeTruthy();
      const query = new URLSearchParams(detailQuery);
      expect(Object.fromEntries(query)).toEqual({ from, to, category: 'R&D / Prizm' });
      await user.click(screen.getByRole('link', { name: 'Visual Studio Code' }));
      const titles = await screen.findByRole('table', {
        name: /Time in Visual Studio Code per window title/,
      });
      expect(titles).toHaveTextContent('Matching Prizm project');
      expect(titles).toHaveTextContent('No title recorded or retained');
      expect(
        screen
          .getByRole('heading', { name: 'Visual Studio Code' })
          .querySelector<HTMLElement>('.app-day__swatch')?.style.backgroundColor,
      ).toBe(listColor);
      expect(titles.querySelector<HTMLElement>('.app-shares__bar')?.style.backgroundColor).toBe(
        listColor,
      );
      expect(Object.fromEntries(new URLSearchParams(titleQuery))).toEqual({
        from,
        to,
        category: 'R&D / Prizm',
        appKey: 'vscode',
      });
      await user.click(screen.getByRole('link', { name: /Back to R&D \/ Prizm/ }));
      expect(await screen.findByRole('heading', { name: 'R&D / Prizm' })).toBeInTheDocument();
      await user.click(screen.getByRole('link', { name: /Back to/ }));
      expect(await screen.findByRole('button', { name: 'Category' })).toHaveAttribute(
        'aria-pressed',
        'true',
      );
      if (source === 'trends') expect(screen.getByLabelText('Period')).toHaveValue('7');
    },
  );

  it('shows an error when the selected category no longer exists', async () => {
    server.use(
      http.get('/api/range/category-applications', () =>
        badRequest('name', 'The category no longer exists.'),
      ),
    );
    globalThis.history.replaceState(
      null,
      '',
      '/category/2026-11-05/2026-11-05?category=Missing&source=day',
    );
    renderWithApi(<App />);
    expect(await screen.findByRole('alert')).toHaveTextContent('The category no longer exists.');
  });

  it('explains unavailable titles in a category application', async () => {
    server.use(
      http.get('/api/range/category-application-titles', () =>
        HttpResponse.json({
          app: apps.code,
          totalMs: 60000,
          titles: [{ title: null, durationMs: 60000, share: 1, percent: 100 }],
        }),
      ),
    );
    globalThis.history.replaceState(
      null,
      '',
      '/category/2026-11-05/2026-11-05/app?category=Work&source=day&app=vscode',
    );
    renderWithApi(<App />);
    expect(await screen.findByText(/may have been removed by retention/)).toBeInTheDocument();
  });
  it('opens an app from the day view and goes back', async () => {
    const user = userEvent.setup();
    renderWithApi(<App />);

    const table = await screen.findByRole('table');
    const listColor = table.querySelector<HTMLElement>('tr[data-app="vscode"] .app-shares__swatch')
      ?.style.backgroundColor;
    expect(listColor).toBeTruthy();
    await user.click(within(table).getByRole('link', { name: 'Visual Studio Code' }));

    expect(globalThis.location.pathname).toBe(`/day/${demoDate}/app/vscode`);
    expect(await screen.findByRole('heading', { level: 2 })).toHaveTextContent(
      'Visual Studio Code',
    );
    expect(await screen.findByText(/TrackingEngine\.cs/)).toBeInTheDocument();
    expect(
      screen
        .getByRole('heading', { name: 'Visual Studio Code' })
        .querySelector<HTMLElement>('.app-day__swatch')?.style.backgroundColor,
    ).toBe(listColor);
    expect(
      screen.getByRole('table').querySelector<HTMLElement>('.app-shares__bar')?.style
        .backgroundColor,
    ).toBe(listColor);

    await user.click(screen.getByRole('link', { name: /Back to today/ }));
    expect(globalThis.location.pathname).toBe('/');
    expect(await screen.findByRole('heading', { level: 2 })).toHaveTextContent(/^Today, /);
  });

  it('opens a deep link to an app on another day', async () => {
    globalThis.history.replaceState(null, '', '/day/2026-11-04/app/vscode');

    renderWithApi(<App />);

    expect(await screen.findByRole('link', { name: /Back to the day/ })).toHaveAttribute(
      'href',
      '/day/2026-11-04',
    );
    expect(await screen.findByRole('table')).toBeInTheDocument();
  });

  it('opens a deep link to another day', async () => {
    globalThis.history.replaceState(null, '', '/day/2026-11-04');

    renderWithApi(<App />);

    expect(await screen.findByRole('heading', { level: 2 })).toHaveTextContent(
      /^Wednesday, .*2026/,
    );
  });

  it('shows a way home for an unknown page', async () => {
    globalThis.history.replaceState(null, '', '/nowhere');

    renderWithApi(<App />);

    expect(await screen.findByRole('heading', { name: 'Page not found' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Go to today' })).toHaveAttribute('href', '/');
  });

  it('steps back a day with the arrow key and returns with the back button', async () => {
    const user = userEvent.setup();
    renderWithApi(<App />);

    await screen.findByRole('table');
    await user.keyboard('{ArrowLeft}');
    expect(globalThis.location.pathname).toBe('/day/2026-11-04');
    expect(await screen.findByRole('heading', { level: 2 })).toHaveTextContent(/^Wednesday, /);

    act(() => {
      globalThis.history.back();
    });
    await waitFor(() => expect(globalThis.location.pathname).toBe('/'));
    expect(await screen.findByRole('heading', { level: 2 })).toHaveTextContent(/^Today, /);
  });

  it('opens the week and a day from it', async () => {
    const user = userEvent.setup();
    renderWithApi(<App />);

    await screen.findByRole('table');
    await user.click(screen.getByRole('link', { name: 'Week' }));
    expect(globalThis.location.pathname).toBe('/week/2026-11-02');
    expect(await screen.findByRole('heading', { level: 2 })).toHaveTextContent(/^This week, /);

    const days = await screen.findByRole('table', { name: /Active time per day/ });
    await user.click(within(days).getByRole('link', { name: /Tue/ }));
    expect(globalThis.location.pathname).toBe('/day/2026-11-03');
    expect(await screen.findByRole('heading', { level: 2 })).toHaveTextContent(/^Tuesday, /);
  });

  it('opens this week at /week', async () => {
    globalThis.history.replaceState(null, '', '/week');

    renderWithApi(<App />);

    expect(await screen.findByRole('heading', { level: 2 })).toHaveTextContent(/^This week, /);
  });

  it('opens a deep link to another week', async () => {
    globalThis.history.replaceState(null, '', '/week/2026-10-28');

    renderWithApi(<App />);

    expect(await screen.findByRole('heading', { level: 2 })).toHaveTextContent(/^Week of /);
    expect(screen.getByRole('link', { name: 'Next week' })).toHaveAttribute(
      'href',
      '/week/2026-11-02',
    );
  });
});

describe('App settings', () => {
  it('opens Usage from the header and preserves category view', async () => {
    const user = userEvent.setup();
    globalThis.history.replaceState(null, '', '/trends?view=categories');
    renderWithApi(<App />);

    await screen.findByRole('heading', { level: 2, name: 'Trends' });
    await user.click(screen.getByRole('link', { name: 'Usage' }));

    expect(globalThis.location.pathname + globalThis.location.search).toBe('/?view=categories');
    expect(await screen.findByRole('heading', { level: 2, name: /^Today,/ })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Usage' })).toHaveAttribute('aria-current', 'page');
  });

  it('opens the settings from the header', async () => {
    const user = userEvent.setup();
    renderWithApi(<App />);

    await user.click(await screen.findByRole('link', { name: 'Settings' }));

    expect(globalThis.location.pathname).toBe('/settings');
    expect(await screen.findByRole('heading', { level: 2, name: 'Settings' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Settings' })).toHaveAttribute('aria-current', 'page');
    expect(await screen.findByRole('button', { name: 'Save settings' })).toBeInTheDocument();
  });
});
