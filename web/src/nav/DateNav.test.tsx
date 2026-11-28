import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { DateNav } from './DateNav.tsx';

const today = '2026-11-05';

function link(name: string | RegExp) {
  return screen.getByRole('link', { name });
}

describe('DateNav for a day', () => {
  it('preserves category view on all navigation paths', () => {
    render(<DateNav view="day" date="2026-11-03" today={today} categoryView="categories" />);

    expect(link('Previous day')).toHaveAttribute('href', '/day/2026-11-02?view=categories');
    expect(link('Next day')).toHaveAttribute('href', '/day/2026-11-04?view=categories');
    expect(link('Today')).toHaveAttribute('href', '/?view=categories');
    expect(link('Week')).toHaveAttribute('href', '/week/2026-11-02?view=categories');
    fireEvent.change(screen.getByLabelText('Go to a date'), { target: { value: '2026-10-20' } });
    expect(globalThis.location.search).toBe('?view=categories');
  });

  it('links to the previous and next day, today and the week', () => {
    render(<DateNav view="day" date="2026-11-03" today={today} />);

    expect(link('Previous day')).toHaveAttribute('href', '/day/2026-11-02');
    expect(link('Next day')).toHaveAttribute('href', '/day/2026-11-04');
    expect(link('Today')).toHaveAttribute('href', '/');
    expect(link('Today')).not.toHaveAttribute('aria-current');
    expect(link('Week')).toHaveAttribute('href', '/week/2026-11-02');
    expect(link('Day')).toHaveAttribute('aria-current', 'page');
  });

  it('stops at today', () => {
    render(<DateNav view="day" date={today} today={today} />);

    expect(screen.queryByRole('link', { name: 'Next day' })).not.toBeInTheDocument();
    expect(link('Today')).toHaveAttribute('aria-current', 'date');
    expect(link('Day')).toHaveAttribute('href', '/');
  });

  it('goes to the picked date', () => {
    render(<DateNav view="day" date={today} today={today} />);

    const picker = screen.getByLabelText('Go to a date');
    expect(picker).toHaveValue(today);
    expect(picker).toHaveAttribute('max', today);

    fireEvent.change(picker, { target: { value: '2026-10-20' } });
    expect(globalThis.location.pathname).toBe('/day/2026-10-20');
  });

  it('ignores a cleared picker', () => {
    render(<DateNav view="day" date={today} today={today} />);

    fireEvent.change(screen.getByLabelText('Go to a date'), { target: { value: '' } });
    expect(globalThis.location.pathname).toBe('/');
  });

  it('moves with the arrow keys', async () => {
    const user = userEvent.setup();
    globalThis.history.replaceState(null, '', '/day/2026-11-03');
    render(<DateNav view="day" date="2026-11-03" today={today} />);

    await user.keyboard('{ArrowLeft}');
    expect(globalThis.location.pathname).toBe('/day/2026-11-02');

    await user.keyboard('{ArrowRight}');
    expect(globalThis.location.pathname).toBe('/day/2026-11-04');
  });

  it('does not move past today with the arrow keys', async () => {
    const user = userEvent.setup();
    render(<DateNav view="day" date={today} today={today} />);

    await user.keyboard('{ArrowRight}');
    expect(globalThis.location.pathname).toBe('/');
  });

  it('leaves arrow keys with a modifier, in a text field, or already handled alone', async () => {
    const user = userEvent.setup();
    render(
      <>
        <DateNav view="day" date="2026-11-03" today={today} />
        <input aria-label="Search" />
        <button type="button" onKeyDown={(e) => e.preventDefault()}>
          Handled
        </button>
      </>,
    );

    await user.keyboard('{Alt>}{ArrowLeft}{/Alt}');
    await user.click(screen.getByLabelText('Search'));
    await user.keyboard('{ArrowLeft}');
    screen.getByRole('button', { name: 'Handled' }).focus();
    await user.keyboard('{ArrowLeft}');
    screen.getByLabelText('Go to a date').focus();
    await user.keyboard('{ArrowLeft}');

    expect(globalThis.location.pathname).toBe('/');
  });

  it('stops listening when it goes away', async () => {
    const user = userEvent.setup();
    const { unmount } = render(<DateNav view="day" date="2026-11-03" today={today} />);
    unmount();

    await user.keyboard('{ArrowLeft}');
    expect(globalThis.location.pathname).toBe('/');
  });
});

describe('DateNav for a week', () => {
  it('preserves category view when switching to today or moving weeks', async () => {
    const user = userEvent.setup();
    render(<DateNav view="week" date="2026-11-02" today={today} categoryView="categories" />);

    expect(link('Day')).toHaveAttribute('href', '/?view=categories');
    expect(link('This week')).toHaveAttribute('href', '/week/2026-11-02?view=categories');
    await user.keyboard('{ArrowLeft}');
    expect(globalThis.location.pathname + globalThis.location.search).toBe(
      '/week/2026-10-26?view=categories',
    );
  });

  it('links to the previous and next week by their Mondays', () => {
    render(<DateNav view="week" date="2026-10-28" today={today} />);

    expect(link('Previous week')).toHaveAttribute('href', '/week/2026-10-19');
    expect(link('Next week')).toHaveAttribute('href', '/week/2026-11-02');
    expect(link('This week')).toHaveAttribute('href', '/week/2026-11-02');
    expect(link('Day')).toHaveAttribute('href', '/day/2026-10-26');
    expect(link('Week')).toHaveAttribute('aria-current', 'page');
  });

  it('stops at this week, and "Day" opens today', () => {
    render(<DateNav view="week" date="2026-11-02" today={today} />);

    expect(screen.queryByRole('link', { name: 'Next week' })).not.toBeInTheDocument();
    expect(link('This week')).toHaveAttribute('aria-current', 'page');
    expect(link('Day')).toHaveAttribute('href', '/');
  });

  it('goes to the week of the picked date', () => {
    render(<DateNav view="week" date="2026-11-02" today={today} />);

    fireEvent.change(screen.getByLabelText('Go to a date'), { target: { value: '2026-10-22' } });
    expect(globalThis.location.pathname).toBe('/week/2026-10-19');
  });

  it('moves a week with the arrow keys', async () => {
    const user = userEvent.setup();
    render(<DateNav view="week" date="2026-11-02" today={today} />);

    await user.keyboard('{ArrowLeft}');
    expect(globalThis.location.pathname).toBe('/week/2026-10-26');
  });
});
