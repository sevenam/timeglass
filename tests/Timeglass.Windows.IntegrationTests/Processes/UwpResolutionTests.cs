using System.Diagnostics;
using System.Runtime.InteropServices;
using Timeglass.Core.Domain;
using Timeglass.Windows.Foreground;
using Windows.Management.Deployment;
using Windows.Win32.Foundation;

namespace Timeglass.Windows.IntegrationTests.Processes;

/// <summary>
/// Opens Calculator, a UWP app drawn inside an <c>ApplicationFrameHost.exe</c> frame, and checks
/// that its window resolves to Calculator's own exe. Skipped where Calculator isn't installed,
/// such as Windows Server runners.
/// </summary>
[Trait("Category", Desktop.Category)]
[Collection(ForegroundCollection.Name)]
public sealed partial class UwpResolutionTests : IDisposable
{
    private const string CalculatorPackageFamily = "Microsoft.WindowsCalculator_8wekyb3d8bbwe";
    private const string FrameWindowClass = "ApplicationFrameWindow";
    private static readonly string[] CalculatorProcessNames = ["CalculatorApp", "Calculator"];
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);

    private readonly HashSet<int> _calculatorsBefore = CalculatorProcessIds();

    [Fact(Timeout = Desktop.TestTimeout + 30_000)]
    public async Task A_frame_window_resolves_to_the_app_inside_it()
    {
        if (!CalculatorIsInstalled())
        {
            Assert.Skip("Calculator is not installed on this machine.");
        }

        var framesBefore = FrameWindows().ToHashSet();
        // calc.exe is a stub that starts the Calculator app and exits.
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "calc.exe")) { UseShellExecute = true })?.Dispose();

        var inspector = WindowInspector.CreateDefault();
        var deadline = DateTime.UtcNow + OpenTimeout;
        List<nint> newFrameHandles = [];
        List<WindowSnapshot> newFrames = [];
        while (DateTime.UtcNow < deadline)
        {
            newFrameHandles = [.. FrameWindows().Where(h => !framesBefore.Contains(h))];
            newFrames = [.. newFrameHandles.Select(h => inspector.Read((HWND)h)).OfType<WindowSnapshot>()];
            var calculator = newFrames.FirstOrDefault(s => IsCalculator(s.ExeName));
            if (calculator is not null)
            {
                calculator.ExePath.ShouldContain("WindowsApps", Case.Insensitive);
                calculator.ProcessId.ShouldBeGreaterThan(0);
                calculator.Title.ShouldNotBeNullOrWhiteSpace();
                return;
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        if (newFrames.Count == 0)
        {
            Assert.Skip("Calculator did not open a frame window on this desktop.");
        }

        // The app's own window joins the frame only once the frame is shown. With the display off,
        // or the frame on another virtual desktop, it never does, and the frame host is all there
        // is to resolve; that is correct behaviour, not the bug. Checked here with plain Win32,
        // independently of the code under test.
        if (!newFrameHandles.Any(HasChildOfAnotherProcess))
        {
            Assert.Skip("Calculator's window never joined its frame (display off, or another virtual desktop).");
        }

        // A frame window appeared but still resolves to the frame host: that is the bug this test is for.
        throw new ShouldAssertException(
            $"Calculator's frame window resolved to {string.Join(", ", newFrames.Select(s => s.ExeName))}, " +
            $"expected one of {string.Join(", ", CalculatorProcessNames)}.");
    }

    public void Dispose()
    {
        foreach (var process in CalculatorProcessNames.SelectMany(Process.GetProcessesByName))
        {
            using (process)
            {
                if (!_calculatorsBefore.Contains(process.Id))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Already exited, or not ours to end.
                    }
                }
            }
        }
    }

    private static bool CalculatorIsInstalled()
    {
        try
        {
            return new PackageManager().FindPackagesForUser(string.Empty, CalculatorPackageFamily).Any();
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static bool IsCalculator(string exeName) =>
        CalculatorProcessNames.Any(name => string.Equals(exeName, name + ".exe", StringComparison.OrdinalIgnoreCase));

    private static HashSet<int> CalculatorProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var process in CalculatorProcessNames.SelectMany(Process.GetProcessesByName))
        {
            using (process)
            {
                ids.Add(process.Id);
            }
        }

        return ids;
    }

    /// <summary>All top-level UWP frame windows on the desktop.</summary>
    private static List<nint> FrameWindows()
    {
        var windows = new List<nint>();
        nint window = 0;
        while ((window = FindWindowEx(0, window, FrameWindowClass, null)) != 0)
        {
            windows.Add(window);
        }

        return windows;
    }

    /// <summary>True if a direct child of <paramref name="frame"/> belongs to another process than the frame.</summary>
    private static bool HasChildOfAnotherProcess(nint frame)
    {
        var frameProcess = ProcessIdOf(frame);
        nint child = 0;
        while ((child = FindWindowEx(frame, child, null, null)) != 0)
        {
            var childProcess = ProcessIdOf(child);
            if (childProcess != 0 && childProcess != frameProcess)
            {
                return true;
            }
        }

        return false;
    }

    private static uint ProcessIdOf(nint window)
    {
        _ = GetWindowThreadProcessId(window, out var processId);
        return processId;
    }

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
}
