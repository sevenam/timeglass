import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { demoDate, rangeSummary } from '../test/fixtures.ts';
import { badRequest } from '../test/handlers.ts';
import { renderWithApi } from '../test/render.tsx';
import { server } from '../test/server.ts';
import { WeekView } from './WeekView.tsx';

// demoDate is Thursday 5 November 2026; its week runs from Monday 2 to Sunday 8 November.
const monday = '2026-11-02';

async function dayTable() {
  return screen.findByRole('table', { name: /Active time per day/ });
}

function dayRows(table: HTMLElement) {
  return within(table)
    .getAllByRole('row')
    .slice(1)
    .map((row) => ({
      date: row.getAttribute('data-date'),
      day: within(row).getByRole('rowheader').textContent,
      bar: within(row).queryByRole('img')?.getAttribute('aria-label') ?? null,
      time: within(row).getAllByRole('cell').at(-1)?.textContent,
    }));
}

describe('WeekView', () => {
  it('shows category totals and bars when the category view is selected', async () => {
    const range = rangeSummary(monday, '2026-11-08');
    const renamed = (app: (typeof range.apps)[number], index: number) => ({
      ...app,
      app:
        index === 0
          ? { key: 'category-0', name: 'Investigator' }
          : { key: 'category-20', name: 'Prizm' },
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
          apps: [...range.apps.slice(0, 2).map(renamed), away(range.awayMs)],
          days: range.days.map((day) => ({
            ...day,
            apps: [
              ...day.apps.slice(0, 2).map(renamed),
              ...(day.awayMs > 0 ? [away(day.awayMs)] : []),
            ],
          })),
        }),
      ),
    );
    const user = userEvent.setup();
    renderWithApi(<WeekView date={demoDate} today={demoDate} />);
    await dayTable();
    await user.click(screen.getByRole('button', { name: 'Category' }));
    const table = await screen.findByRole('table', { name: /each day.s categories/ });
    expect(dayRows(table)[0]?.bar).toContain('Investigator');
    expect(dayRows(table)[0]?.bar).toContain('Away');
    expect(dayRows(table)[0]?.time).toBe('8h 5m');
    const categories = screen.getByRole('table', {
      name: 'Tracked time per category, longest first',
    });
    expect(categories).toHaveTextContent('Investigator');
    expect(within(categories).queryByRole('link', { name: 'Away' })).not.toBeInTheDocument();
    const colors = ['category-0', 'category-20'].map(
      (key) =>
        categories.querySelector<HTMLElement>(`tr[data-app="${key}"] .app-shares__swatch`)?.style
          .backgroundColor,
    );
    expect(colors[0]).toBeTruthy();
    expect(colors[1]).toBeTruthy();
    expect(colors[0]).not.toBe(colors[1]);
    const segments = table.querySelectorAll<HTMLElement>(
      'tr[data-date="2026-11-02"] .week-days__segment',
    );
    expect([...segments].slice(0, 2).map((segment) => segment.style.backgroundColor)).toEqual(
      colors,
    );
    expect(Number.parseFloat(segments[2]?.style.width ?? '')).toBeCloseTo((65 / 485) * 100);
  });
  it('asks for the week from Monday to Sunday, whatever day it is given', async () => {
    let asked = '';
    server.use(
      http.get('/api/range/summary', ({ request }) => {
        asked = new URL(request.url).search;
        return HttpResponse.json(rangeSummary(monday, '2026-11-08'));
      }),
    );

    renderWithApi(<WeekView date="2026-11-07" today={demoDate} />);

    await dayTable();
    expect(asked).toBe('?from=2026-11-02&to=2026-11-08');
  });

  it('shows a loading state until the week arrives', async () => {
    renderWithApi(<WeekView date={demoDate} today={demoDate} />);

    expect(screen.getByRole('status')).toHaveTextContent('Loading the week…');
    await dayTable();
  });

  it('heads this week and other weeks', async () => {
    const { unmount } = renderWithApi(<WeekView date={demoDate} today={demoDate} />);
    expect(screen.getByRole('heading', { level: 2 })).toHaveTextContent(/^This week, /);
    await dayTable();
    unmount();

    renderWithApi(<WeekView date="2026-10-26" today={demoDate} />);
    expect(screen.getByRole('heading', { level: 2 })).toHaveTextContent(/^Week of .*2026/);
    await dayTable();
  });

  it('shows the totals of the week', async () => {
    renderWithApi(<WeekView date={demoDate} today={demoDate} />);

    await dayTable();
    const totals = Object.fromEntries(
      screen
        .getAllByRole('term')
        .map((dt) => [dt.textContent, dt.nextElementSibling?.textContent ?? '']),
    );
    // Five workdays of 7h each, in the fixture.
    expect(totals).toEqual({ Active: '35h', Away: '5h 25m', 'Per active day': '7h' });
  });

  it('draws a bar per day stacked by all its apps', async () => {
    renderWithApi(<WeekView date={demoDate} today={demoDate} />);

    const rows = dayRows(await dayTable());
    expect(rows.map((r) => r.date)).toEqual([
      '2026-11-02',
      '2026-11-03',
      '2026-11-04',
      '2026-11-05',
      '2026-11-06',
      '2026-11-07',
      '2026-11-08',
    ]);
    expect(rows[0]).toMatchObject({
      bar: 'Visual Studio Code 3h, Outlook 1h 45m, Microsoft Edge 1h 15m, Microsoft Teams 45m, Windows Terminal 15m',
      time: '7h',
    });
    expect(rows[3]?.day).toMatch(/\(today\)$/);
    // Friday is still to come: no link, no time.
    expect(rows[4]).toMatchObject({ bar: null, time: '' });
    // Saturday and Sunday are empty in the fixture.
    expect(rows[5]).toMatchObject({ bar: null, time: '' });
  });

  it('shows an empty past day as 0m', async () => {
    renderWithApi(<WeekView date="2026-10-26" today={demoDate} />);

    const rows = dayRows(await dayTable());
    expect(rows[6]).toMatchObject({ date: '2026-11-01', bar: null, time: '0m' });
  });

  it('links each past day to the day view, and today to the home page', async () => {
    renderWithApi(<WeekView date={demoDate} today={demoDate} />);

    const table = await dayTable();
    const links = within(table).getAllByRole('link');
    expect(links.map((l) => l.getAttribute('href'))).toEqual([
      '/day/2026-11-02',
      '/day/2026-11-03',
      '/day/2026-11-04',
      '/',
    ]);
    expect(links[3]).toHaveAttribute('aria-current', 'date');
  });

  it('opens a day when its row is clicked', async () => {
    const user = userEvent.setup();
    renderWithApi(<WeekView date={demoDate} today={demoDate} />);

    const table = await dayTable();
    await user.click(within(table).getAllByRole('img')[1] as HTMLElement);
    expect(globalThis.location.pathname).toBe('/day/2026-11-03');

    await user.click(within(table).getByRole('link', { name: /Wed/ }));
    expect(globalThis.location.pathname).toBe('/day/2026-11-04');
  });

  it('does not open a day that is still to come', async () => {
    const user = userEvent.setup();
    renderWithApi(<WeekView date={demoDate} today={demoDate} />);

    const table = await dayTable();
    const friday = within(table).getAllByRole('row')[5] as HTMLElement;
    await user.click(friday);
    expect(globalThis.location.pathname).toBe('/');
  });

  it("lists the week's apps without links to a single day", async () => {
    renderWithApi(<WeekView date={demoDate} today={demoDate} />);

    await dayTable();
    const apps = screen.getByRole('table', { name: 'Active time per app, longest first' });
    expect(
      within(apps)
        .getAllByRole('rowheader')
        .map((th) => th.textContent),
    ).toEqual([
      'Visual Studio Code',
      'Outlook',
      'Microsoft Edge',
      'Microsoft Teams',
      'Windows Terminal',
    ]);
    expect(within(apps).queryByRole('link')).not.toBeInTheDocument();
  });

  it('says so when nothing was tracked', async () => {
    server.use(
      http.get('/api/range/summary', () =>
        HttpResponse.json(rangeSummary('2026-11-07', '2026-11-08')),
      ),
    );

    renderWithApi(<WeekView date="2026-11-07" today="2026-11-08" />);

    expect(await screen.findByText('Nothing was tracked this week.')).toBeInTheDocument();
  });

  it('shows the error when the week cannot be loaded', async () => {
    server.use(http.get('/api/range/summary', () => badRequest('to', 'Too long.')));

    renderWithApi(<WeekView date={demoDate} today={demoDate} />);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'One or more validation errors occurred.',
    );
  });
});
