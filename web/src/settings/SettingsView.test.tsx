import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import type { SettingsDto } from '../api/client.ts';
import { demoDate, settings } from '../test/fixtures.ts';
import { validationProblem } from '../test/handlers.ts';
import { renderWithApi } from '../test/render.tsx';
import { server } from '../test/server.ts';
import { SettingsView } from './SettingsView.tsx';

/** Records the bodies the dashboard PUTs to /api/settings, answering as the API would. */
function captureSaves() {
  const saved: SettingsDto[] = [];
  server.use(
    http.put('/api/settings', async ({ request }) => {
      const body = (await request.json()) as SettingsDto;
      saved.push(body);
      return HttpResponse.json(body);
    }),
  );
  return saved;
}

async function renderSettings(current: SettingsDto = settings()) {
  server.use(http.get('/api/settings', () => HttpResponse.json(current)));
  const user = userEvent.setup();
  renderWithApi(<SettingsView today={demoDate} />);
  await screen.findByRole('button', { name: 'Save settings' });
  return user;
}

describe('SettingsView', () => {
  it('saves categories in priority order', async () => {
    const saved = captureSaves();
    const user = await renderSettings();
    expect(screen.getByText('No categories yet.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Add category' }));
    await user.type(screen.getByRole('textbox', { name: 'Category 1 name' }), 'Investigator');
    await user.type(
      screen.getByRole('textbox', { name: 'Keywords, category 1' }),
      'investigator, research',
    );
    await user.click(screen.getByRole('button', { name: 'Save settings' }));
    await screen.findByText(/Saved\. The new settings apply right away/);
    expect(saved[0]?.categories).toEqual([
      { name: 'Investigator', keywords: ['investigator', 'research'] },
    ]);
  });

  it('reorders and removes categories without losing their keywords', async () => {
    const saved = captureSaves();
    const user = await renderSettings(
      settings({
        categories: [
          { name: 'Investigator', keywords: ['research', 'investigator'] },
          { name: 'Prizm', keywords: ['prizm'] },
          { name: 'Other', keywords: ['other'] },
        ],
      }),
    );
    expect(screen.getByRole('button', { name: 'Move category 1 up' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Move category 3 down' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Move category 2 up' }));
    expect(screen.getByRole('textbox', { name: 'Category 1 name' })).toHaveValue('Prizm');
    expect(screen.getByRole('textbox', { name: 'Keywords, category 1' })).toHaveValue('prizm');
    await user.click(screen.getByRole('button', { name: 'Remove category 3' }));
    await user.click(screen.getByRole('button', { name: 'Save settings' }));
    await screen.findByText(/Saved\. The new settings apply right away/);
    expect(saved[0]?.categories).toEqual([
      { name: 'Prizm', keywords: ['prizm'] },
      { name: 'Investigator', keywords: ['research', 'investigator'] },
    ]);
  });
  it('shows the current settings', async () => {
    await renderSettings(
      settings({
        idleThresholdSeconds: 600,
        excludedApps: ['keepass'],
        recordWindowTitles: true,
        appsWithoutTitles: ['edge'],
        appAliases: [{ key: 'outlook', name: 'Mail', exes: [], titlePattern: null }],
        startWithWindows: true,
      }),
    );

    expect(screen.getByRole('checkbox', { name: /Count me as away/ })).toBeChecked();
    expect(screen.getByRole('spinbutton', { name: /after/i })).toHaveValue(10);
    expect(within(screen.getByRole('list', { name: 'Excluded apps' })).getByText('keepass'));
    expect(within(screen.getByRole('list', { name: 'Apps without titles' })).getByText('edge'));
    expect(screen.getByRole('textbox', { name: 'Name, row 1' })).toHaveValue('Mail');
    expect(screen.getByRole('checkbox', { name: /Start Timeglass/ })).toBeChecked();
  });

  it('hides the startup switch where the app cannot start with Windows', async () => {
    await renderSettings(settings({ startWithWindows: null }));

    expect(screen.queryByRole('checkbox', { name: /Start Timeglass/ })).not.toBeInTheDocument();
  });

  it('saves the changes and says so', async () => {
    const saved = captureSaves();
    const user = await renderSettings();

    const minutes = screen.getByRole('spinbutton', { name: /after/i });
    await user.clear(minutes);
    await user.type(minutes, '2');
    await user.type(
      screen.getByRole('combobox', { name: 'Add to excluded apps' }),
      'keepass{Enter}',
    );
    await user.type(screen.getByRole('combobox', { name: 'Add to never away in' }), 'vlc');
    await user.click(
      within(screen.getByRole('group', { name: 'Never away in' })).getByRole('button', {
        name: 'Add',
      }),
    );
    await user.click(screen.getByRole('checkbox', { name: /Start Timeglass/ }));
    await user.click(screen.getByRole('button', { name: 'Save settings' }));

    expect(
      await screen.findByText(/Saved\. The new settings apply right away\./),
    ).toBeInTheDocument();
    expect(saved).toEqual([
      settings({
        idleThresholdSeconds: 120,
        excludedApps: ['keepass'],
        idleExemptApps: ['vlc'],
        startWithWindows: true,
      }),
    ]);
  });

  it('saves the after-hours rule and thresholds', async () => {
    const saved = captureSaves();
    const user = await renderSettings();
    await user.click(screen.getByRole('checkbox', { name: /Exclude a short final return/ }));
    const away = screen.getByRole('spinbutton', { name: /Minimum absence/ });
    const returned = screen.getByRole('spinbutton', { name: /Maximum return/ });
    await user.clear(away);
    await user.type(away, '90');
    await user.clear(returned);
    await user.type(returned, '15');
    await user.click(screen.getByRole('button', { name: 'Save settings' }));
    await waitFor(() => expect(saved).toHaveLength(1));
    expect(saved[0]).toMatchObject({
      discardAfterHoursReturns: true,
      afterHoursMinimumAwayMinutes: 90,
      afterHoursMaximumReturnMinutes: 15,
    });
  });

  it('turns idle detection and window titles off', async () => {
    const saved = captureSaves();
    const user = await renderSettings(settings({ appsWithoutTitles: ['edge'] }));

    await user.click(screen.getByRole('checkbox', { name: /Count me as away/ }));
    await user.click(screen.getByRole('checkbox', { name: /Record window titles/ }));

    expect(screen.queryByRole('spinbutton', { name: /after/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'Apps without titles' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Save settings' }));

    await waitFor(() => expect(saved).toHaveLength(1));
    expect(saved[0]).toMatchObject({ idleThresholdSeconds: null, recordWindowTitles: false });
  });

  it('removes an app from a list', async () => {
    const saved = captureSaves();
    const user = await renderSettings(settings({ excludedApps: ['keepass', 'bank'] }));

    await user.click(screen.getByRole('button', { name: 'Remove keepass from excluded apps' }));
    await user.click(screen.getByRole('button', { name: 'Save settings' }));

    await waitFor(() => expect(saved).toHaveLength(1));
    expect(saved[0]?.excludedApps).toEqual(['bank']);
  });

  it('suggests apps from recent weeks', async () => {
    await renderSettings();

    await waitFor(() =>
      expect(document.querySelector('datalist option[value="vscode"]')?.textContent).toBe(
        'Visual Studio Code',
      ),
    );
  });

  it('adds and removes app renames', async () => {
    const saved = captureSaves();
    const user = await renderSettings();

    await user.click(screen.getByRole('button', { name: 'Add a rename or alias' }));
    await user.type(screen.getByRole('textbox', { name: 'App key, row 1' }), 'game');
    await user.type(screen.getByRole('textbox', { name: 'Name, row 1' }), 'My game');
    await user.type(
      screen.getByRole('textbox', { name: 'Executables, row 1' }),
      'game.exe, launcher.exe',
    );
    await user.click(screen.getByRole('button', { name: 'Add a rename or alias' }));
    await user.click(screen.getByRole('button', { name: 'Remove row 2' }));
    await user.click(screen.getByRole('button', { name: 'Save settings' }));

    await waitFor(() => expect(saved).toHaveLength(1));
    expect(saved[0]?.appAliases).toEqual([
      { key: 'game', name: 'My game', exes: ['game.exe', 'launcher.exe'], titlePattern: null },
    ]);
  });

  it('saves new work hours (#110)', async () => {
    const saved = captureSaves();
    const user = await renderSettings();
    const hours = screen.getByRole('group', { name: 'Work hours' });

    const from = within(hours).getByLabelText('from');
    await user.clear(from);
    await user.type(from, '08:30');
    await user.click(screen.getByRole('button', { name: 'Save settings' }));

    expect(
      await screen.findByText(/Saved\. The new settings apply right away\./),
    ).toBeInTheDocument();
    expect(saved).toEqual([settings({ workHoursStartMinutes: 8 * 60 + 30 })]);
  });

  it('checks the form before saving', async () => {
    const saved = captureSaves();
    const user = await renderSettings(settings({ excludedApps: ['edge'] }));

    const minutes = screen.getByRole('spinbutton', { name: /after/i });
    await user.clear(minutes);
    await user.type(minutes, '500');
    await user.type(
      screen.getByRole('combobox', { name: 'Add to apps without titles' }),
      'edge{Enter}',
    );
    await user.click(screen.getByRole('button', { name: 'Save settings' }));

    expect(
      await screen.findByText('Enter a number of minutes from 0.5 to 240.'),
    ).toBeInTheDocument();
    expect(screen.getByText(/edge is also excluded/)).toBeInTheDocument();
    expect(minutes).toHaveAttribute('aria-invalid', 'true');
    expect(saved).toEqual([]);
  });

  it('shows the errors the API finds next to their fields', async () => {
    server.use(
      http.put('/api/settings', () =>
        validationProblem({
          appAliases: ['AppAliases: game has no executables.'],
          logLevel: ['LogLevel is not valid.'],
        }),
      ),
    );
    const user = await renderSettings(
      settings({ appAliases: [{ key: 'game', name: 'Game', exes: [], titlePattern: null }] }),
    );

    await user.click(screen.getByRole('button', { name: 'Save settings' }));

    const aliases = screen.getByRole('group', { name: 'App names' });
    expect(
      await within(aliases).findByText('AppAliases: game has no executables.'),
    ).toBeInTheDocument();
    expect(screen.getByText('LogLevel is not valid.')).toBeInTheDocument();
    expect(screen.queryByText(/Saved\./)).not.toBeInTheDocument();
  });

  it('says when saving failed', async () => {
    server.use(http.put('/api/settings', () => new HttpResponse(null, { status: 500 })));
    const user = await renderSettings();

    await user.click(screen.getByRole('button', { name: 'Save settings' }));

    expect(await screen.findByText(/Timeglass ran into a problem/)).toBeInTheDocument();
  });

  it('says when the settings cannot be loaded', async () => {
    server.use(http.get('/api/settings', () => HttpResponse.error()));

    renderWithApi(<SettingsView today={demoDate} />);

    expect(await screen.findByText(/Timeglass is not running/)).toBeInTheDocument();
  });
});
