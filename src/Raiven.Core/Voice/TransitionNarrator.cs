namespace Raiven.Core.Voice;

/// <summary>
/// Supplies the short spoken lines RAIVEN uses to narrate an interruption: one when a
/// question cuts into a summary that is playing, and one when that summary resumes
/// afterward. Injected so playback tests can assert deterministically.
/// </summary>
public interface ITransitionNarrator
{
    /// <summary>Line spoken right after a summary is cut off, before the question plays.</summary>
    string InterruptLine();

    /// <summary>Line spoken right before an interrupted summary is replayed from the top.</summary>
    string ResumeLine();
}

/// <summary>
/// Default narrator: five hardcoded "interrupt" lines and five "resume" lines, chosen at
/// random for variety. Plain conversational text (no markdown/symbols) so Kokoro reads it
/// cleanly, matching RAIVEN's first-person voice.
/// </summary>
public sealed class RandomTransitionNarrator(Random? rng = null) : ITransitionNarrator
{
    private readonly Random _rng = rng ?? Random.Shared;

    private static readonly string[] Interrupts =
    [
        "Let me stop there for a second. A question just came in.",
        "Hold that thought, there's a question.",
        "One moment, Claude needs your attention.",
        "Quick pause, something needs you.",
        "Sorry to cut in, there's a question waiting.",
    ];

    private static readonly string[] Resumes =
    [
        "Where was I...",
        "Right, back to what I was saying.",
        "Anyway, as I was saying...",
        "Okay, picking up where I left off.",
        "So, returning to the earlier update.",
    ];

    public string InterruptLine() => Interrupts[_rng.Next(Interrupts.Length)];
    public string ResumeLine() => Resumes[_rng.Next(Resumes.Length)];
}
