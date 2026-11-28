import createClient from 'openapi-fetch';
import type { components, paths } from './schema';

type Schemas = components['schemas'];

export type StatusDto = Schemas['StatusDto'];
export type TrackingStateKind = Schemas['TrackingStateKind'];
export type AppDto = Schemas['AppDto'];
export type AppTotalDto = Schemas['AppTotalDto'];
export type DaySummaryDto = Schemas['DaySummaryDto'];
export type TimelineDto = Schemas['TimelineDto'];
export type TimelineBlockDto = Schemas['TimelineBlockDto'];
export type AppDayDto = Schemas['AppDayDto'];
export type CategoryAppTitlesDto = Schemas['CategoryAppTitlesDto'];
export type TitleTotalDto = Schemas['TitleTotalDto'];
export type RangeSummaryDto = Schemas['RangeSummaryDto'];
export type DayTotalDto = Schemas['DayTotalDto'];
export type SettingsDto = Schemas['SettingsDto'];
export type AppAliasDto = Schemas['AppAliasDto'];
export type PauseDuration = Schemas['PauseDuration'];
export type ValidationProblemDetails = Schemas['HttpValidationProblemDetails'];

/**
 * Creates a client for the local API, typed from `openapi.json` (#31).
 *
 * The dashboard is served from the same origin as the API, and the vite dev server proxies
 * `/api`, so the default base URL is the page's own origin. The browser sends the sign-in cookie
 * by itself; there is no token in the page.
 */
export function createApiClient(baseUrl: string = globalThis.location.origin) {
  return createClient<paths>({
    baseUrl,
    credentials: 'same-origin',
    // Look fetch up on each call rather than once, so test interceptors installed later apply.
    fetch: (request) => globalThis.fetch(request),
  });
}

export type ApiClient = ReturnType<typeof createApiClient>;
