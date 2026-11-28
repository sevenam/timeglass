import { act, renderHook } from '@testing-library/react';
import { navigate, parseRoute, paths, useRoute } from './router.ts';

describe('parseRoute', () => {
  it.each([
    ['/', { name: 'today' }],
    ['', { name: 'today' }],
    ['/day/2026-11-05', { name: 'day', date: '2026-11-05' }],
    ['/day/2026-11-05/', { name: 'day', date: '2026-11-05' }],
    ['/day/2026-11-05/app/outlook', { name: 'app', date: '2026-11-05', appKey: 'outlook' }],
    ['/day/2026-11-05/app/my%20app', { name: 'app', date: '2026-11-05', appKey: 'my app' }],
    ['/day/5-11-2026', { name: 'notFound' }],
    ['/day/2026-11-05/app', { name: 'notFound' }],
    ['/day/2026-11-05/app/%E0%A4%A', { name: 'notFound' }],
    ['/day/2026-02-30', { name: 'notFound' }],
    ['/week', { name: 'thisWeek' }],
    ['/week/2026-11-02', { name: 'week', date: '2026-11-02' }],
    ['/week/2026-13-01', { name: 'notFound' }],
    ['/week/2026-11-02/extra', { name: 'notFound' }],
    ['/trends', { name: 'trends' }],
    ['/trends/outlook', { name: 'notFound' }],
    ['/settings', { name: 'settings' }],
    ['/settings/', { name: 'settings' }],
    ['/settings/privacy', { name: 'notFound' }],
  ])('reads %s', (path, route) => {
    expect(parseRoute(path)).toEqual(route);
  });

  it('round-trips the paths it builds', () => {
    expect(parseRoute(paths.app('2026-11-05', 'a/b c'))).toEqual({
      name: 'app',
      date: '2026-11-05',
      appKey: 'a/b c',
    });
    expect(parseRoute(paths.day('2026-11-05'))).toEqual({ name: 'day', date: '2026-11-05' });
    expect(parseRoute(paths.today())).toEqual({ name: 'today' });
    expect(parseRoute(paths.week('2026-11-02'))).toEqual({ name: 'week', date: '2026-11-02' });
    expect(parseRoute(paths.settings())).toEqual({ name: 'settings' });
    expect(parseRoute(paths.trends())).toEqual({ name: 'trends' });
    expect(parseRoute(paths.category('2026-11-02', '2026-11-08', 'R&D / Prizm', 'week'))).toEqual({
      name: 'category',
      from: '2026-11-02',
      to: '2026-11-08',
      categoryName: 'R&D / Prizm',
      source: 'week',
    });
    expect(
      parseRoute(paths.categoryApp('2026-11-02', '2026-11-08', 'R&D / Prizm', 'week', 'app/a b')),
    ).toEqual({
      name: 'categoryApp',
      from: '2026-11-02',
      to: '2026-11-08',
      categoryName: 'R&D / Prizm',
      source: 'week',
      appKey: 'app/a b',
    });
    expect(parseRoute(paths.categoryBack('2026-10-30', '2026-11-05', 'trends'))).toEqual({
      name: 'trends',
      view: 'categories',
      period: 7,
    });
    expect(parseRoute('/category/2026-11-02/2026-11-08?category=A&source=invalid')).toEqual({
      name: 'notFound',
    });
  });
});

describe('useRoute', () => {
  it('follows navigate and the back button', () => {
    const { result } = renderHook(() => useRoute());
    expect(result.current).toEqual({ name: 'today' });

    act(() => navigate('/day/2026-11-05/app/outlook'));
    expect(result.current).toEqual({ name: 'app', date: '2026-11-05', appKey: 'outlook' });
    expect(globalThis.location.pathname).toBe('/day/2026-11-05/app/outlook');

    act(() => {
      globalThis.history.replaceState(null, '', '/day/2026-11-04');
      globalThis.dispatchEvent(new PopStateEvent('popstate'));
    });
    expect(result.current).toEqual({ name: 'day', date: '2026-11-04' });
  });

  it('does not add a history entry for the page it is on', () => {
    const before = globalThis.history.length;
    act(() => navigate('/'));
    expect(globalThis.history.length).toBe(before);
  });
});
