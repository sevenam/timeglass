import { useState, type FormEvent } from 'react';
import type { AppDto, SettingsDto } from '../api/client.ts';
import { ApiError, errorMessage } from '../api/errors.ts';
import { useRangeSummary, useSaveSettings, useSettings } from '../api/queries.ts';
import { addDays } from '../dates.ts';
import { AliasEditor } from './AliasEditor.tsx';
import { AppKeyList, FieldErrorList } from './AppKeyList.tsx';
import { DeleteData } from './DeleteData.tsx';
import { ExportData } from './ExportData.tsx';
import { CategoryEditor } from './CategoryEditor.tsx';
import {
  toForm,
  toSettings,
  validate,
  type FieldErrors,
  type SettingsForm,
} from './settingsForm.ts';

/** How far back apps are suggested from when adding one to a list. */
const SUGGESTION_DAYS = 28;

/** The fields the form shows errors next to; anything else is listed above the Save button. */
const FIELDS = new Set([
  'idleThresholdSeconds',
  'idleExemptApps',
  'excludedApps',
  'appsWithoutTitles',
  'appAliases',
  'categories',
  'workHoursStartMinutes',
  'workHoursEndMinutes',
  'afterHoursMinimumAwayMinutes',
  'afterHoursMaximumReturnMinutes',
]);

/**
 * Settings and privacy (#36): what used to need an edit of settings.json, and deleting recorded
 * time. Saving checks the form first and then lets the API check it with the app's own rules.
 */
export function SettingsView({ today }: { today: string }) {
  const settings = useSettings();

  return (
    <section className="settings" aria-labelledby="settings-heading" aria-busy={settings.isPending}>
      <h2 id="settings-heading">Settings</h2>
      {settings.isPending ? (
        <p role="status" className="settings__hint">
          Loading the settings…
        </p>
      ) : settings.isError ? (
        <p role="alert" className="day__error">
          {errorMessage(settings.error)}
        </p>
      ) : (
        <SettingsEditor initial={settings.data} today={today} />
      )}
      <ExportData today={today} />
      <DeleteData today={today} />
    </section>
  );
}

function SettingsEditor({ initial, today }: { initial: SettingsDto; today: string }) {
  const [form, setForm] = useState<SettingsForm>(() => toForm(initial));
  const [errors, setErrors] = useState<FieldErrors>({});
  const [saved, setSaved] = useState(false);
  const save = useSaveSettings();
  const recent = useRangeSummary(addDays(today, -(SUGGESTION_DAYS - 1)), today);
  const suggestions: AppDto[] = recent.data?.apps.map((a) => a.app) ?? [];

  const change = (update: Partial<SettingsForm>) => {
    setForm((current) => ({ ...current, ...update }));
    setSaved(false);
  };

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    const found = validate(form);
    setErrors(found);
    setSaved(false);
    if (Object.keys(found).length > 0) {
      return;
    }
    save.mutate(toSettings(form), {
      onSuccess: (result) => {
        setForm(toForm(result));
        setSaved(true);
      },
      onError: (error) => {
        if (error instanceof ApiError && error.status === 400 && error.problem?.errors) {
          setErrors(error.problem.errors);
        }
      },
    });
  };

  const otherErrors = Object.entries(errors)
    .filter(([field]) => !FIELDS.has(field))
    .flatMap(([, messages]) => messages);
  const failed =
    save.isError && !(save.error instanceof ApiError && save.error.status === 400)
      ? errorMessage(save.error)
      : null;
  const hasErrors = Object.keys(errors).length > 0;

  return (
    <form className="settings__form" onSubmit={onSubmit} noValidate>
      <section className="settings__section" aria-labelledby="settings-tracking">
        <h3 id="settings-tracking">Tracking</h3>
        <fieldset className="settings__field">
          <legend>Away detection</legend>
          <label className="settings__check">
            <input
              type="checkbox"
              checked={form.idleEnabled}
              onChange={(e) => change({ idleEnabled: e.target.checked })}
            />
            Count me as away when I don’t use the keyboard or mouse
          </label>
          {form.idleEnabled && (
            <label className="settings__inline">
              after
              <input
                type="number"
                inputMode="decimal"
                min={0.5}
                max={240}
                step={0.5}
                value={form.idleMinutes}
                aria-invalid={errors.idleThresholdSeconds !== undefined}
                onChange={(e) => change({ idleMinutes: e.target.value })}
              />
              minutes
            </label>
          )}
          <FieldErrorList errors={errors.idleThresholdSeconds} />
        </fieldset>

        <AppKeyList
          label="Never away in"
          description="Apps where you can be watching or listening without typing, such as video players and meeting apps. While one is in front, you never count as away."
          keys={form.idleExemptApps}
          onChange={(idleExemptApps) => change({ idleExemptApps })}
          suggestions={suggestions}
          errors={errors.idleExemptApps}
        />

        <fieldset className="settings__field">
          <legend>Work hours</legend>
          <p className="settings__hint">
            The workday on the main screen counts the time from your first to your last activity
            that falls inside these hours.
          </p>
          <label className="settings__inline">
            from
            <input
              type="time"
              value={form.workStart}
              aria-invalid={errors.workHoursStartMinutes !== undefined}
              onChange={(e) => change({ workStart: e.target.value })}
            />
          </label>
          <label className="settings__inline">
            to
            <input
              type="time"
              value={form.workEnd}
              aria-invalid={errors.workHoursEndMinutes !== undefined}
              onChange={(e) => change({ workEnd: e.target.value })}
            />
          </label>
          <FieldErrorList errors={errors.workHoursStartMinutes} />
          <FieldErrorList errors={errors.workHoursEndMinutes} />
        </fieldset>

        <fieldset className="settings__field">
          <legend>After-hours returns</legend>
          <label className="settings__check">
            <input
              type="checkbox"
              checked={form.discardAfterHoursReturns}
              onChange={(e) => change({ discardAfterHoursReturns: e.target.checked })}
            />
            Exclude a short final return after a long absence
          </label>
          <p className="settings__hint">
            After work hours, exclude the last completed return and its preceding absence when they
            meet these limits. Recorded intervals and exports are kept unchanged; days reduced to
            totals cannot be adjusted.
          </p>
          <label className="settings__inline">
            Minimum absence
            <input
              type="number"
              min={1}
              max={1440}
              step={1}
              value={form.afterHoursMinimumAwayMinutes}
              aria-invalid={errors.afterHoursMinimumAwayMinutes !== undefined}
              onChange={(e) => change({ afterHoursMinimumAwayMinutes: e.target.value })}
            />
            minutes
          </label>
          <FieldErrorList errors={errors.afterHoursMinimumAwayMinutes} />
          <label className="settings__inline">
            Maximum return
            <input
              type="number"
              min={1}
              max={240}
              step={1}
              value={form.afterHoursMaximumReturnMinutes}
              aria-invalid={errors.afterHoursMaximumReturnMinutes !== undefined}
              onChange={(e) => change({ afterHoursMaximumReturnMinutes: e.target.value })}
            />
            minutes
          </label>
          <FieldErrorList errors={errors.afterHoursMaximumReturnMinutes} />
        </fieldset>

        {form.startWithWindows !== null && (
          <fieldset className="settings__field">
            <legend>Startup</legend>
            <label className="settings__check">
              <input
                type="checkbox"
                checked={form.startWithWindows}
                onChange={(e) => change({ startWithWindows: e.target.checked })}
              />
              Start Timeglass when I sign in to Windows
            </label>
          </fieldset>
        )}
      </section>

      <section className="settings__section" aria-labelledby="settings-privacy">
        <h3 id="settings-privacy">Privacy</h3>
        <fieldset className="settings__field">
          <legend>Window titles</legend>
          <label className="settings__check">
            <input
              type="checkbox"
              checked={form.recordWindowTitles}
              onChange={(e) => change({ recordWindowTitles: e.target.checked })}
            />
            Record window titles, such as the document or page you had open
          </label>
          <p className="settings__hint">
            Titles stay on this computer. Turning this off only affects time recorded from now on.
          </p>
        </fieldset>

        {form.recordWindowTitles && (
          <AppKeyList
            label="Apps without titles"
            description="Time in these apps is recorded, but never their window titles."
            keys={form.appsWithoutTitles}
            onChange={(appsWithoutTitles) => change({ appsWithoutTitles })}
            suggestions={suggestions}
            errors={errors.appsWithoutTitles}
          />
        )}

        <AppKeyList
          label="Excluded apps"
          description="Nothing at all is recorded while one of these apps is in front."
          keys={form.excludedApps}
          onChange={(excludedApps) => change({ excludedApps })}
          suggestions={suggestions}
          errors={errors.excludedApps}
        />
      </section>

      <section className="settings__section" aria-labelledby="settings-apps">
        <h3 id="settings-apps">Apps</h3>
        <AliasEditor
          rows={form.aliases}
          onChange={(aliases) => change({ aliases })}
          errors={errors.appAliases}
        />
      </section>

      <section className="settings__section" aria-labelledby="settings-categories">
        <h3 id="settings-categories">Categories</h3>
        <CategoryEditor
          rows={form.categories}
          onChange={(categories) => change({ categories })}
          errors={errors.categories}
        />
      </section>

      <div className="settings__save">
        <FieldErrorList errors={otherErrors} />
        {hasErrors && otherErrors.length === 0 && (
          <p className="settings__errors" role="alert">
            Some settings need fixing; see above.
          </p>
        )}
        {failed !== null && (
          <p className="settings__errors" role="alert">
            {failed}
          </p>
        )}
        <button
          type="submit"
          className="settings__button settings__button--primary"
          disabled={save.isPending}
        >
          {save.isPending ? 'Saving…' : 'Save settings'}
        </button>
        {saved && (
          <p role="status" className="settings__saved">
            Saved. The new settings apply right away.
          </p>
        )}
      </div>
    </form>
  );
}
