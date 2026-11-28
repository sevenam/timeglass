import { FieldErrorList } from './AppKeyList.tsx';
import type { CategoryRow } from './settingsForm.ts';

export function CategoryEditor({
  rows,
  onChange,
  errors,
}: {
  rows: CategoryRow[];
  onChange: (rows: CategoryRow[]) => void;
  errors: string[] | undefined;
}) {
  function move(index: number, direction: number) {
    const next = [...rows];
    const target = index + direction;
    const current = next[index];
    const other = next[target];
    if (!current || !other) return;
    next[index] = other;
    next[target] = current;
    onChange(next);
  }
  return (
    <fieldset className="settings__field">
      <legend>Categories</legend>
      <p className="settings__hint">
        Match any keyword against application names or recorded window titles, ignoring case. The
        first matching category wins; everything else is Ungrouped. Older time retained without
        titles can only match application names. Tracked away time is included automatically.
        Separate keywords with commas.
      </p>
      {rows.length === 0 ? (
        <p className="settings__none">No categories yet.</p>
      ) : (
        <ol className="categories" aria-label="Categories in priority order">
          {rows.map((row, i) => (
            <li className="categories__item" key={i}>
              <div className="categories__header">
                <span className="categories__priority">Priority {i + 1}</span>
                <div className="categories__actions">
                  <button
                    type="button"
                    className="settings__button"
                    disabled={i === 0}
                    onClick={() => move(i, -1)}
                    aria-label={`Move category ${i + 1} up`}
                  >
                    Move up
                  </button>
                  <button
                    type="button"
                    className="settings__button"
                    disabled={i === rows.length - 1}
                    onClick={() => move(i, 1)}
                    aria-label={`Move category ${i + 1} down`}
                  >
                    Move down
                  </button>
                  <button
                    type="button"
                    className="settings__button"
                    onClick={() => onChange(rows.filter((_, n) => n !== i))}
                    aria-label={`Remove category ${i + 1}`}
                  >
                    Remove
                  </button>
                </div>
              </div>
              <div className="categories__fields">
                <label>
                  Category name
                  <input
                    type="text"
                    aria-label={`Category ${i + 1} name`}
                    placeholder="e.g. Investigator"
                    value={row.name}
                    onChange={(e) =>
                      onChange(rows.map((r, n) => (n === i ? { ...r, name: e.target.value } : r)))
                    }
                  />
                </label>
                <label>
                  Keywords
                  <input
                    type="text"
                    aria-label={`Keywords, category ${i + 1}`}
                    placeholder="e.g. investigator, research"
                    value={row.keywords}
                    onChange={(e) =>
                      onChange(
                        rows.map((r, n) => (n === i ? { ...r, keywords: e.target.value } : r)),
                      )
                    }
                  />
                </label>
              </div>
            </li>
          ))}
        </ol>
      )}
      <button
        type="button"
        className="settings__button"
        onClick={() => onChange([...rows, { name: '', keywords: '' }])}
      >
        Add category
      </button>
      <FieldErrorList errors={errors} />
    </fieldset>
  );
}
