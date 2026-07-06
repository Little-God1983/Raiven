namespace Raiven.Core.Diagnostics;

/// <summary>
/// Turns a startup exception into the message shown in the fatal-error dialog.
/// </summary>
public static class StartupFailureMessage
{
    // REGDB_E_CLASSNOTREG: what AppNotificationManager.Register() throws when the
    // Windows App Runtime is missing or its notification COM classes are unregistered.
    private const int RegdbClassNotRegistered = unchecked((int)0x80040154);

    public static string Describe(Exception ex)
    {
        if (!ChainContainsClassNotRegistered(ex))
            return ex.Message;

        // Do NOT recommend winget here: its catalog has no WindowsAppRuntime 2.2
        // package, and the 2.1 package it does have installs only the Framework
        // package - not the Main/Singleton packages that register the notification
        // COM classes - so it cannot fix this error (see issues #5/#6).
        return ex.Message +
            "\n\nThis usually means the Windows App Runtime 2.2 is not installed on this machine." +
            "\n\nTo fix it, download and run Microsoft's standalone installer" +
            " (windowsappruntimeinstall-x64.exe, latest 2.2.x release) from:" +
            "\nhttps://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads" +
            "\n\nThen start RAIVEN again.";
    }

    private static bool ChainContainsClassNotRegistered(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current.HResult == RegdbClassNotRegistered)
                return true;
        }
        return false;
    }
}
