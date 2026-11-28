import { http, HttpResponse } from 'msw';
import type { PauseDuration, SettingsDto, ValidationProblemDetails } from '../api/client.ts';
import {
  appDay,
  categoryRangeSummary,
  daySummary,
  rangeSummary,
  settings,
  status,
  timeline,
} from './fixtures.ts';

const isDate = (value: unknown): value is string =>
  typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value);

/** The 400 the API answers a bad date with. */
export function badRequest(name: string, message: string) {
  return HttpResponse.json<ValidationProblemDetails>(
    {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
      title: 'One or more validation errors occurred.',
      status: 400,
      errors: { [name]: [message] },
    },
    { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
  );
}

/** A 400 with errors for several fields, as the API answers invalid settings. */
export function validationProblem(errors: Record<string, string[]>) {
  return HttpResponse.json<ValidationProblemDetails>(
    {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
      title: 'One or more validation errors occurred.',
      status: 400,
      errors,
    },
    { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
  );
}

/** The 401 the API answers when the browser isn't signed in. */
export function unauthorized() {
  return HttpResponse.json(
    {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.2',
      title: 'Unauthorized',
      status: 401,
      detail: 'Open the dashboard from the Timeglass tray icon.',
    },
    { status: 401, headers: { 'Content-Type': 'application/problem+json' } },
  );
}

/**
 * The API on demo data. Every test gets these; a test overrides one with `server.use(...)`.
 */
export const handlers = [
  http.get('/api/status', () => HttpResponse.json(status())),
  http.get('/api/days/:date/summary', ({ params }) =>
    isDate(params.date)
      ? HttpResponse.json(daySummary(params.date))
      : badRequest('date', 'Use yyyy-MM-dd.'),
  ),
  http.get('/api/days/:date/timeline', ({ params }) =>
    isDate(params.date)
      ? HttpResponse.json(timeline(params.date))
      : badRequest('date', 'Use yyyy-MM-dd.'),
  ),
  http.get('/api/days/:date/apps/:appKey', ({ params }) =>
    isDate(params.date) && typeof params.appKey === 'string'
      ? HttpResponse.json(appDay(params.date, params.appKey))
      : badRequest('date', 'Use yyyy-MM-dd.'),
  ),
  http.get('/api/range/summary', ({ request }) => {
    const url = new URL(request.url);
    const from = url.searchParams.get('from');
    const to = url.searchParams.get('to');
    if (!isDate(from) || !isDate(to) || from > to) {
      return badRequest('from', 'Give from and to as yyyy-MM-dd, from no later than to.');
    }
    return HttpResponse.json(rangeSummary(from, to));
  }),
  http.get('/api/range/categories', ({ request }) => {
    const url = new URL(request.url);
    const from = url.searchParams.get('from');
    const to = url.searchParams.get('to');
    if (!isDate(from) || !isDate(to) || from > to) {
      return badRequest('from', 'Give from and to as yyyy-MM-dd, from no later than to.');
    }
    return HttpResponse.json(categoryRangeSummary(from, to));
  }),
  http.get('/api/range/category-applications', ({ request }) => {
    const url = new URL(request.url);
    const from = url.searchParams.get('from');
    const to = url.searchParams.get('to');
    return isDate(from) && isDate(to)
      ? HttpResponse.json(rangeSummary(from, to))
      : badRequest('from', 'Give from and to as yyyy-MM-dd.');
  }),
  http.get('/api/settings', () => HttpResponse.json(settings())),
  http.put('/api/settings', async ({ request }) =>
    HttpResponse.json((await request.json()) as SettingsDto),
  ),
  http.post('/api/pause', async ({ request }) => {
    const { duration } = (await request.json()) as { duration: PauseDuration };
    return HttpResponse.json(
      status({
        state: 'paused',
        currentApp: null,
        since: null,
        pausedUntil: duration === 'oneHour' ? '2026-11-05T17:05:00+01:00' : null,
      }),
    );
  }),
  http.post('/api/resume', () => HttpResponse.json(status())),
  http.delete('/api/intervals', async ({ request }) => {
    const url = new URL(request.url);
    const { confirm } = (await request.json()) as { confirm?: string };
    return confirm === `${url.searchParams.get('from')}..${url.searchParams.get('to')}`
      ? new HttpResponse(null, { status: 204 })
      : badRequest('confirm', 'Confirm the range.');
  }),
];
