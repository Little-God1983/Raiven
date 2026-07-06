using System.Runtime.InteropServices;
using Raiven.Core.Diagnostics;

namespace Raiven.Core.Tests;

public class StartupFailureMessageTests
{
    private const int RegdbClassNotRegistered = unchecked((int)0x80040154);

    [Fact]
    public void Describe_ClassNotRegisteredComError_NamesRuntimeInstallerAndDownloadPage()
    {
        // The 0x80040154 COMException from AppNotificationManager.Register() means the
        // Windows App Runtime is missing/unregistered (issue #5). The dialog must point
        // at Microsoft's standalone installer - NOT winget, whose catalog has no 2.2
        // package and whose 2.1 package omits the packages that register the
        // notification COM classes (see issue #6 discussion).
        var ex = new COMException("Class not registered", RegdbClassNotRegistered);

        var message = StartupFailureMessage.Describe(ex);

        Assert.Contains("Class not registered", message);
        Assert.Contains("Windows App Runtime", message);
        Assert.Contains("windowsappruntimeinstall", message);
        Assert.Contains("https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads", message);
        Assert.DoesNotContain("winget install", message);
    }

    [Fact]
    public void Describe_ClassNotRegisteredWrappedInOuterException_StillDetected()
    {
        var ex = new InvalidOperationException(
            "Notifier construction failed",
            new COMException("Class not registered", RegdbClassNotRegistered));

        var message = StartupFailureMessage.Describe(ex);

        Assert.Contains("Windows App Runtime", message);
        Assert.Contains("windowsappruntimeinstall", message);
    }

    [Fact]
    public void Describe_UnrelatedException_ReturnsPlainMessageWithoutRuntimeGuidance()
    {
        var ex = new InvalidOperationException("Something else broke");

        var message = StartupFailureMessage.Describe(ex);

        Assert.Equal("Something else broke", message);
        Assert.DoesNotContain("Windows App Runtime", message);
    }
}
