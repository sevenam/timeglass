namespace Timeglass.Windows.Autostart;

/// <summary>
/// Starts an executable when the user signs in, through a value in the <c>Run</c> key.
/// </summary>
/// <remarks>
/// The registry is the only record of whether autostart is on: <see cref="IsEnabled"/> reads it
/// every time, so a change made outside the app (Task Manager's <i>Startup apps</i>, a cleanup
/// tool, the user in regedit) shows up and nothing can drift out of sync.
/// </remarks>
public sealed class AutostartRegistration
{
    /// <summary>The value name used for the app.</summary>
    public const string DefaultValueName = "Timeglass";

    private readonly IStartupRegistry _registry;

    /// <param name="registry">Where the values live; <see cref="CurrentUserStartupRegistry.Instance"/> in the app.</param>
    /// <param name="executablePath">Full path of the program to start.</param>
    /// <param name="valueName">Name of the registry value.</param>
    public AutostartRegistration(IStartupRegistry registry, string executablePath, string valueName = DefaultValueName)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);

        _registry = registry;
        ExecutablePath = Path.GetFullPath(executablePath);
        ValueName = valueName;
    }

    /// <summary>The program that is started.</summary>
    public string ExecutablePath { get; }

    /// <summary>The registry value's name.</summary>
    public string ValueName { get; }

    /// <summary>The value's data: the quoted path, so a path with spaces starts the right file.</summary>
    public string Command => $"\"{ExecutablePath}\"";

    /// <summary>
    /// Whether signing in starts <see cref="ExecutablePath"/>. Off when the value is missing, when
    /// the user turned it off in Task Manager or Settings, or when it points at another file (an
    /// old copy of the app that was moved); <see cref="Enable"/> fixes the last two.
    /// </summary>
    public bool IsEnabled =>
        _registry.GetRunValue(ValueName) is { } command
        && ParseExecutable(command) is { } path
        && string.Equals(path, ExecutablePath, StringComparison.OrdinalIgnoreCase)
        && !IsTurnedOff(_registry.GetApprovedValue(ValueName));

    /// <summary>
    /// The program the value starts, whichever it is and whether or not Task Manager turned it
    /// off; <see langword="null"/> without a value, or with one that holds no path.
    /// </summary>
    public string? RegisteredExecutable =>
        _registry.GetRunValue(ValueName) is { } command ? ParseExecutable(command) : null;

    /// <summary>
    /// Points a value that starts another file at <see cref="ExecutablePath"/>: the app moved, for
    /// example from an unzipped copy to an installed one (#40). Does nothing without a value, and
    /// leaves Task Manager's flag alone, so autostart the user turned off stays off.
    /// </summary>
    /// <returns>Whether the value was changed.</returns>
    public bool Adopt()
    {
        if (RegisteredExecutable is not { } registered
            || string.Equals(registered, ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _registry.SetRunValue(ValueName, Command);
        return true;
    }

    /// <summary>
    /// Registers <see cref="Command"/>, replacing whatever the value held, and clears a
    /// <i>disabled</i> flag Task Manager may have left, so the app really starts.
    /// </summary>
    public void Enable()
    {
        _registry.SetRunValue(ValueName, Command);
        _registry.DeleteApprovedValue(ValueName);
    }

    /// <summary>Removes the value and any Task Manager flag for it.</summary>
    public void Disable()
    {
        _registry.DeleteRunValue(ValueName);
        _registry.DeleteApprovedValue(ValueName);
    }

    /// <summary><see cref="Enable"/> or <see cref="Disable"/>.</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            Enable();
        }
        else
        {
            Disable();
        }
    }

    /// <summary>
    /// Whether a <c>StartupApproved</c> flag says the user turned the program off. The first byte
    /// is even (<c>02</c>, <c>06</c>) for on and odd (<c>03</c>, <c>07</c>) for off; the rest is
    /// when it was changed. No flag means on.
    /// </summary>
    internal static bool IsTurnedOff(byte[]? approved) => approved is [var state, ..] && (state & 1) == 1;

    /// <summary>
    /// The executable of a <c>Run</c> command line: the quoted part, or the whole text if it isn't
    /// quoted. <see langword="null"/> if there is none.
    /// </summary>
    internal static string? ParseExecutable(string command)
    {
        var text = command.Trim();
        string path;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end < 0)
            {
                return null;
            }

            path = text[1..end];
        }
        else
        {
            path = text;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
