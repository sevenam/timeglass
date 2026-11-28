import type { RangeSummaryDto } from '../api/client.ts';
import { DayTimeline } from '../day/DayTimeline.tsx';
import { formatLongDate } from '../format.ts';

export function CategoryTimelines({
  summary,
  categoryName,
  colorFor,
  appKey,
}: {
  summary: RangeSummaryDto;
  categoryName: string;
  colorFor: (key: string) => string;
  appKey?: string;
}) {
  const days = summary.days.filter((day) =>
    appKey ? day.apps.some((app) => app.app.key === appKey) : day.activeMs > 0,
  );
  return (
    <>
      <h3>When</h3>
      {days.map((day) => (
        <div key={day.date}>
          {summary.from !== summary.to && <h4>{formatLongDate(day.date)}</h4>}
          <DayTimeline
            date={day.date}
            byCategory
            colorFor={colorFor}
            includeBlock={(block) =>
              block.kind === 'active' &&
              block.category?.name.toLowerCase() === categoryName.toLowerCase() &&
              (appKey === undefined || block.app?.key === appKey)
            }
          />
        </div>
      ))}
    </>
  );
}
