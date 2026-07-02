using System.Media;
using Raiven.Core.Config;

namespace Raiven.App;

public static class ChimePlayer
{
    public static void Play(RaivenConfig config)
    {
        if (config.ChimeWavPath is { } path && File.Exists(path))
        {
            // Not disposed here: Play() is asynchronous and disposing immediately
            // can cut the sound off. The player is short-lived; GC handles it.
            new SoundPlayer(path).Play();
        }
        else
        {
            SystemSounds.Asterisk.Play();
        }
    }
}
