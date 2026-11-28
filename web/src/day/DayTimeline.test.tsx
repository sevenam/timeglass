import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { demoDate, timeline } from '../test/fixtures.ts';
import { renderWithApi } from '../test/render.tsx';
import { server } from '../test/server.ts';
import { DayTimeline } from './DayTimeline.tsx';

const colorFor = (key: string) => (key === 'vscode' ? 'rgb(0, 0, 255)' : 'rgb(128, 128, 128)');

async function lane() {
  return screen.findByRole('group', { name: /Timeline of the day/ });
}

describe('DayTimeline', () => {
  it('shows a loading state until the timeline arrives', async () => {
    renderWithApi(<DayTimeline date={demoDate} colorFor={colorFor} />);

    expect(screen.getByRole('status')).toHaveTextContent('Loading the timeline…');
    await lane();
  });

  it('draws one block per stretch, labelled with app, time and duration', async () => {
    renderWithApi(<DayTimeline date={demoDate} colorFor={colorFor} />);

    const blocks = within(await lane()).getAllByRole('button');
    expect(blocks.map((b) => b.getAttribute('aria-label'))).toEqual([
      'Outlook, 08:12–08:40, 27m',
      'Visual Studio Code, 08:40–09:30, 50m',
      'Microsoft Teams, 09:30–09:47, 17m',
      'Microsoft Edge, 09:47–11:40, 1h 52m',
      'Away, 11:40–12:20, 40m',
      'Windows Terminal, 12:20–12:35, 15m',
      'Visual Studio Code, 12:35–now, 3h 30m',
    ]);
  });

  it('colours blocks with the app list colours and draws away time as a gap', async () => {
    renderWithApi(<DayTimeline date={demoDate} colorFor={colorFor} />);

    const blocks = within(await lane()).getAllByRole('button');
    expect(blocks[1]?.style.backgroundColor).toBe('rgb(0, 0, 255)');
    expect(blocks[0]?.style.backgroundColor).toBe('rgb(128, 128, 128)');
    expect(blocks[4]).toHaveClass('timeline__block--away');
    expect(blocks[4]?.style.backgroundColor).toBe('');
  });

  it('shows the hours of the tracked part of the day', async () => {
    const { container } = renderWithApi(<DayTimeline date={demoDate} colorFor={colorFor} />);

    await lane();
    const ticks = [...container.querySelectorAll('.timeline__tick')].map((t) => t.textContent);
    expect(ticks).toEqual([
      '08:00',
      '09:00',
      '10:00',
      '11:00',
      '12:00',
      '13:00',
      '14:00',
      '15:00',
      '16:00',
      '17:00',
    ]);
  });

  it('shows a tooltip on hover', async () => {
    const user = userEvent.setup();
    renderWithApi(<DayTimeline date={demoDate} colorFor={colorFor} />);

    const blocks = within(await lane()).getAllByRole('button');
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();

    await user.hover(blocks[3] as HTMLElement);
    const tooltip = screen.getByRole('tooltip');
    expect(tooltip).toHaveTextContent('Microsoft Edge');
    expect(tooltip).toHaveTextContent('09:47–11:40 · 1h 52m');

    await user.unhover(blocks[3] as HTMLElement);
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();
  });

  it('is one tab stop, moved through with the arrow keys, Home and End', async () => {
    const user = userEvent.setup();
    renderWithApi(<DayTimeline date={demoDate} colorFor={colorFor} />);

    const blocks = within(await lane()).getAllByRole('button');
    expect(blocks.filter((b) => b.tabIndex === 0)).toHaveLength(1);

    await user.tab();
    expect(blocks[0]).toHaveFocus();
    expect(screen.getByRole('tooltip')).toHaveTextContent('Outlook');

    await user.keyboard('{ArrowRight}{ArrowRight}');
    expect(blocks[2]).toHaveFocus();
    expect(screen.getByRole('tooltip')).toHaveTextContent('Microsoft Teams');

    await user.keyboard('{End}');
    expect(blocks[6]).toHaveFocus();
    expect(screen.getByRole('tooltip')).toHaveTextContent('12:35–now');

    await user.keyboard('{ArrowRight}');
    expect(blocks[6]).toHaveFocus();

    await user.keyboard('{Home}{ArrowLeft}');
    expect(blocks[0]).toHaveFocus();
    expect(blocks[0]?.tabIndex).toBe(0);
    expect(blocks[6]?.tabIndex).toBe(-1);
  });

  it('draws nothing for a day without blocks', async () => {
    server.use(
      http.get('/api/days/:date/timeline', () => HttpResponse.json({ ...timeline(), blocks: [] })),
    );
    renderWithApi(<DayTimeline date={demoDate} colorFor={colorFor} />);

    await waitFor(() => expect(screen.queryByRole('status')).not.toBeInTheDocument());
    expect(screen.queryByRole('group', { name: /Timeline of the day/ })).not.toBeInTheDocument();
  });

  it('explains when a filtered day has no retained matching timeline', async () => {
    server.use(
      http.get('/api/days/:date/timeline', () => HttpResponse.json({ ...timeline(), blocks: [] })),
    );
    renderWithApi(<DayTimeline date={demoDate} colorFor={colorFor} includeBlock={() => true} />);
    expect(
      await screen.findByText('No matching timeline data is available for this day.'),
    ).toBeInTheDocument();
  });

  it('shows an error when the timeline fails', async () => {
    server.use(http.get('/api/days/:date/timeline', () => new HttpResponse(null, { status: 500 })));

    renderWithApi(<DayTimeline date={demoDate} colorFor={colorFor} />);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Timeglass ran into a problem. Try again in a moment.',
    );
  });
});
