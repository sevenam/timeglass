import { formatDayMonth } from '../dates.ts';
import { formatDuration } from '../format.ts';
import { axisTicks, type Trend } from './trendSeries.ts';

const WIDTH = 720;
const HEIGHT = 260;
const LEFT = 56;
const RIGHT = 28;
const TOP = 12;
const BOTTOM = 28;
const PLOT_WIDTH = WIDTH - LEFT - RIGHT;
const PLOT_HEIGHT = HEIGHT - TOP - BOTTOM;
/** At most this many dates under the x axis, so they never overlap. */
const MAX_LABELS = 7;
/** Points get their own dot (with a tooltip) up to this many buckets; beyond, only the line. */
const MAX_DOTS = 31;

/**
 * The trend as a line chart: one line per app, the average active time per day on the y axis and
 * the days (or weeks) of the period on the x axis. It is a picture for sighted users; the table
 * next to it holds the same apps with their numbers.
 */
export function TrendChart({ trend, byCategory = false }: { trend: Trend; byCategory?: boolean }) {
  const { buckets, series, unit } = trend;
  const ticks = axisTicks(trend.maxMs);
  const top = ticks.at(-1) ?? 1;
  const n = buckets.length;
  const x = (i: number) => LEFT + (n <= 1 ? PLOT_WIDTH / 2 : (i * PLOT_WIDTH) / (n - 1));
  const y = (ms: number) => TOP + PLOT_HEIGHT * (1 - ms / top);
  const labelEvery = Math.max(1, Math.ceil(n / MAX_LABELS));
  const label = (i: number) => {
    const b = buckets[i];
    if (b === undefined) {
      return '';
    }
    return unit === 'week' ? `Week of ${formatDayMonth(b.start)}` : formatDayMonth(b.start);
  };
  const names = series.map((s) => s.name).join(', ');

  return (
    <svg
      className="trend-chart"
      viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
      role="img"
      aria-label={`Average ${byCategory ? 'tracked' : 'active'} time per day, ${unit} by ${unit}, for ${names}`}
    >
      {ticks.map((t) => (
        <g key={t} className="trend-chart__tick">
          <line x1={LEFT} x2={WIDTH - RIGHT} y1={y(t)} y2={y(t)} />
          <text x={LEFT - 8} y={y(t)} textAnchor="end" dominantBaseline="middle">
            {formatDuration(t)}
          </text>
        </g>
      ))}
      {buckets.map((b, i) =>
        i % labelEvery === 0 ? (
          <text
            key={b.start}
            className="trend-chart__date"
            x={x(i)}
            y={HEIGHT - 8}
            textAnchor={n <= 1 ? 'middle' : i === 0 ? 'start' : 'middle'}
          >
            {formatDayMonth(b.start)}
          </text>
        ) : null,
      )}
      {series.map((s) => (
        <g key={s.key} className="trend-chart__series" data-app={s.key}>
          <polyline
            className="trend-chart__line"
            points={s.values.map((v, i) => `${x(i)},${y(v)}`).join(' ')}
            stroke={s.color}
          />
          {n <= MAX_DOTS &&
            s.values.map((v, i) => (
              <circle
                key={buckets[i]?.start ?? i}
                className="trend-chart__dot"
                cx={x(i)}
                cy={y(v)}
                r={3}
                fill={s.color}
              >
                <title>{`${s.name}, ${label(i)}: ${formatDuration(v)}${unit === 'week' ? ' per day' : ''}`}</title>
              </circle>
            ))}
        </g>
      ))}
    </svg>
  );
}
