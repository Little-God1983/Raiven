using Raiven.Core.Config;
using Raiven.Core.Events;
using Raiven.Core.Questions;

namespace Raiven.Core.Tests;

public class QuestionGateTests
{
    private const string DuplicateMessage = "Claude needs your permission to use AskUserQuestion";
    private static readonly DateTimeOffset T0 = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static ClaudeNotificationEvent Prompt(
        string message = DuplicateMessage, string? type = "permission_prompt", string session = "s1") =>
        new(session, @"C:\t.jsonl", @"E:\Repos\RAIVEN", message, type);

    private static QuestionGate Gate(bool suppress = true) =>
        new(new RaivenConfig { SuppressDuplicateAskUserQuestionPrompt = suppress });

    [Fact]
    public void PermissionCopy_OfAnAnnouncedDialog_IsSuppressed()
    {
        var gate = Gate();
        gate.NoteAskUserQuestion("s1", T0);

        Assert.False(gate.ShouldAnnounce(Prompt(), T0.AddSeconds(6)));
    }

    [Fact]
    public void PermissionCopy_WithNoAnnouncementBehindIt_IsAnnounced()
    {
        // No PreToolUse hook registered (or RAIVEN was down when it fired): the permission
        // prompt is the only signal for this dialog, so suppressing it would mean silence.
        Assert.True(Gate().ShouldAnnounce(Prompt(), T0));
    }

    [Fact]
    public void PermissionCopy_AnnouncementBelongsToAnotherSession_IsAnnounced()
    {
        var gate = Gate();
        gate.NoteAskUserQuestion("other-session", T0);

        Assert.True(gate.ShouldAnnounce(Prompt(session: "s1"), T0.AddSeconds(6)));
    }

    [Fact]
    public void PermissionCopy_LongAfterTheAnnouncement_IsAnnounced()
    {
        var gate = new QuestionGate(new RaivenConfig(), TimeSpan.FromSeconds(120));
        gate.NoteAskUserQuestion("s1", T0);

        Assert.True(gate.ShouldAnnounce(Prompt(), T0.AddSeconds(121)));
    }

    [Fact]
    public void PermissionCopy_AtTheWindowEdge_IsStillSuppressed()
    {
        var gate = new QuestionGate(new RaivenConfig(), TimeSpan.FromSeconds(120));
        gate.NoteAskUserQuestion("s1", T0);

        Assert.False(gate.ShouldAnnounce(Prompt(), T0.AddSeconds(120)));
    }

    [Fact]
    public void PermissionCopy_SecondOneAfterASingleAnnouncement_IsAnnounced()
    {
        // One announcement excuses exactly one duplicate; anything further is a real prompt.
        var gate = Gate();
        gate.NoteAskUserQuestion("s1", T0);

        Assert.False(gate.ShouldAnnounce(Prompt(), T0.AddSeconds(6)));
        Assert.True(gate.ShouldAnnounce(Prompt(), T0.AddSeconds(12)));
    }

    [Fact]
    public void PermissionPrompt_ForAnotherTool_IsAnnouncedEvenRightAfterAnAnnouncement()
    {
        var gate = Gate();
        gate.NoteAskUserQuestion("s1", T0);

        Assert.True(gate.ShouldAnnounce(
            Prompt("Claude needs your permission to use Bash"), T0.AddSeconds(6)));
    }

    [Fact]
    public void PermissionCopy_WithSuppressionTurnedOff_IsAnnounced()
    {
        var gate = Gate(suppress: false);
        gate.NoteAskUserQuestion("s1", T0);

        Assert.True(gate.ShouldAnnounce(Prompt(), T0.AddSeconds(6)));
    }

    [Fact]
    public void HousekeepingNotification_IsNeverAnnounced()
    {
        Assert.False(Gate().ShouldAnnounce(Prompt("Login successful", "auth_success"), T0));
    }

    [Fact]
    public void OrdinaryQuestion_IsAnnounced()
    {
        Assert.True(Gate().ShouldAnnounce(Prompt("Claude is waiting for your input", "idle_prompt"), T0));
    }
}
