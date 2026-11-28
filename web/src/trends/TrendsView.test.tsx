import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { demoDate, rangeSummary } from '../test/fixtures.ts';
import { badRequest } from '../test/handlers.ts';
import { renderWithApi } from '../test/render.tsx';
import { server } from '../test/server.ts';
import { TrendsView } from './TrendsView.tsx';

async function appTable() {
  return screen.findByRole('table', { name: /The apps in the chart/ });
}

function appRows(table: HTMLElement) {
  return within(table)
    .getAllByRole('row')
    .slice(1)
    .map((row) => [
      within(row).getByRole('rowheader').textContent,
      ...within(row)
        .getAllByRole('cell')
        .map((c) => c.textContent),
    ]);
}

const chartLines = () =>
  [...document.querySelectorAll('.trend-chart__series')].map((g) => g.getAttribute('data-app'));

describe('TrendsView', () => {
  it('draws category trends and offers category choices', async () => {
    const range = rangeSummary('2026-10-07', demoDate);
    const renamed = (app: (typeof range.apps)[number]) => ({
      ...app,
      app: { key: 'category-0', name: 'Investigator' },
    });
    const away = (durationMs: number) => ({
      app: { key: 'away', name: 'Away' },
      durationMs,
      share: 0,
      percent: 0,
    });
    server.use(
      http.get('/api/range/categories', () =>
        HttpResponse.json({
          ...range,
          apps: [...range.apps.slice(0, 1).map(renamed), away(range.awayMs)],
          days: range.days.map((day) => ({
            ...day,
            apps: [
              ...day.apps.slice(0, 1).map(renamed),
              ...(day.awayMs > 0 ? [away(day.awayMs)] : []),
            ],
          })),
        }),
      ),
    );
    const user = userEvent.setup();
    renderWithApi(<TrendsView today={demoDate} />);
    await appTable();
    await user.click(screen.getByRole('button', { name: 'Category' }));
    const table = await screen.findByRole('table', { name: /The categories in the chart/ });
    expect(table).toHaveTextContent('Investigator');
    expect(screen.getByLabelText('Category')).toHaveTextContent('Investigator');
    expect(chartLines()).toEqual(['category-0', 'away']);
    expect(within(table).queryByRole('link', { name: 'Away' })).not.toBeInTheDocument();
    expect(screen.getByRole('img')).toHaveAccessibleName(/Average tracked time per day/);
    await user.selectOptions(screen.getByLabelText('Category'), 'Away');
    expect(chartLines()).toEqual(['away']);
  });
  it('asks for the last 30 days, ending today', async () => {
    let asked = '';
    server.use(
      http.get('/api/range/summary', ({ request }) => {
        asked = new URL(request.url).search;
        return HttpResponse.json(rangeSummary('2026-10-07', demoDate));
      }),
    );

    renderWithApi(<TrendsView today={demoDate} />);

    await appTable();
    expect(asked).toBe('?from=2026-10-07&to=2026-11-05');
  });

  it('shows a loading state until the range arrives', async () => {
    renderWithApi(<TrendsView today={demoDate} />);

    expect(screen.getByRole('status')).toHaveTextContent('Loading the trend…');
    await appTable();
  });

  it('draws the top five apps with their totals and change', async () => {
    renderWithApi(<TrendsView today={demoDate} />);

    const rows = appRows(await appTable());
    // 30 days from Wednesday 7 October to Thursday 5 November hold 22 workdays.
    expect(rows).toEqual([
      ['Visual Studio Code', '66h', '2h 12m', '±0%'],
      ['Outlook', '38h 30m', '1h 17m', '±0%'],
      ['Microsoft Edge', '27h 30m', '55m', '±0%'],
      ['Microsoft Teams', '16h 30m', '33m', '±0%'],
      ['Windows Terminal', '5h 30m', '11m', '±0%'],
    ]);
    expect(chartLines()).toEqual(['vscode', 'outlook', 'edge', 'teams', 'terminal']);
    expect(screen.getByRole('img')).toHaveAccessibleName(/Average active time per day, day by day/);
  });

  it('shows fewer apps when asked', async () => {
    const user = userEvent.setup();
    renderWithApi(<TrendsView today={demoDate} />);
    await appTable();

    await user.selectOptions(screen.getByLabelText('Show'), 'Top 3 apps');

    expect(chartLines()).toEqual(['vscode', 'outlook', 'edge']);
    expect(appRows(await appTable()).map((r) => r[0])).toEqual([
      'Visual Studio Code',
      'Outlook',
      'Microsoft Edge',
    ]);
  });

  it('lists every app of the period by name and shows one when picked', async () => {
    const user = userEvent.setup();
    renderWithApi(<TrendsView today={demoDate} />);
    await appTable();

    const picker = screen.getByLabelText('App');
    expect(
      within(picker)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual([
      'Top apps',
      'Microsoft Edge',
      'Microsoft Teams',
      'Outlook',
      'Visual Studio Code',
      'Windows Terminal',
    ]);

    await user.selectOptions(picker, 'Outlook');

    expect(chartLines()).toEqual(['outlook']);
    expect(screen.getByLabelText('Show')).toBeDisabled();

    await user.selectOptions(picker, 'Top apps');
    expect(chartLines()).toHaveLength(5);
    expect(screen.getByLabelText('Show')).toBeEnabled();
  });

  it('changes the period and goes week by week over long ones', async () => {
    const user = userEvent.setup();
    let asked = '';
    server.use(
      http.get('/api/range/summary', ({ request }) => {
        const url = new URL(request.url);
        asked = url.search;
        return HttpResponse.json(
          rangeSummary(url.searchParams.get('from') ?? '', url.searchParams.get('to') ?? ''),
        );
      }),
    );
    renderWithApi(<TrendsView today={demoDate} />);
    await appTable();

    await user.selectOptions(screen.getByLabelText('Period'), 'Last 365 days');

    expect(await screen.findByRole('img', { name: /week by week/ })).toBeInTheDocument();
    expect(asked).toBe('?from=2025-11-06&to=2026-11-05');
  });

  it('says when nothing was tracked', async () => {
    server.use(
      http.get('/api/range/summary', () =>
        HttpResponse.json({ ...rangeSummary('2026-10-07', demoDate), apps: [] }),
      ),
    );

    renderWithApi(<TrendsView today={demoDate} />);

    expect(await screen.findByText('Nothing was tracked in this period.')).toBeInTheDocument();
  });

  it('shows the API error', async () => {
    server.use(http.get('/api/range/summary', () => badRequest('to', 'Too long.')));

    renderWithApi(<TrendsView today={demoDate} />);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'One or more validation errors occurred.',
    );
  });
});
