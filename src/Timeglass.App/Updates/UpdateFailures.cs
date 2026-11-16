namespace Timeglass.App.Updates;

/// <summary>
/// Which exceptions from the updater the app carries on after. An update is never worth losing
/// tracking over, so nearly all of them: offline, rate limited, disk full, a damaged download.
/// </summary>
/// <remarks>
/// The rule names what is let through rather than what is caught, because the updater's failures
/// can't be listed: Velopack throws its own types and, when a patch fails, a bare
/// <see cref="Exception"/>.
/// </remarks>
internal static class UpdateFailures
{
    /// <summary>
    /// <see langword="false"/> for errors that mean the process itself is in trouble (out of
    /// memory, corrupted memory, stack overflow), which no caller should hide.
    /// </summary>
    public static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException or AccessViolationException or StackOverflowException);
}
