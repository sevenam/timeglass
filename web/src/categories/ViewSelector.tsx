export type CategoryView = 'apps' | 'categories';

export function ViewSelector({
  value,
  onChange,
}: {
  value: CategoryView;
  onChange: (value: CategoryView) => void;
}) {
  return (
    <div className="category-view" role="group" aria-label="View by">
      <span>View by</span>
      <button
        type="button"
        className="date-nav__view"
        aria-pressed={value === 'apps'}
        onClick={() => onChange('apps')}
      >
        Application
      </button>
      <button
        type="button"
        className="date-nav__view"
        aria-pressed={value === 'categories'}
        onClick={() => onChange('categories')}
      >
        Category
      </button>
    </div>
  );
}
