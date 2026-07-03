using Raiven.Core.Config;

namespace Raiven.Core.Tests;

public class RaivenConfigTests
{
    private static string TempConfigPath() =>
        Path.Combine(Path.GetTempPath(), $"raiven-cfg-{Guid.NewGuid():N}", "config.json");

    [Fact]
    public void LoadOrCreate_MissingFile_CreatesDefaultsOnDisk()
    {
        var path = TempConfigPath();

        var config = RaivenConfig.LoadOrCreate(path);

        Assert.True(File.Exists(path));
        Assert.Equal(9876, config.Port);
        Assert.Equal("af_heart", config.Voice);
        Assert.Equal("claude-haiku-4-5", config.Model);
        Assert.Equal(30000, config.MaxTranscriptChars);
        Assert.Equal(240, config.SessionExpiryMinutes);
        Assert.Null(config.ChimeWavPath);
        Assert.Equal("cli", config.SummaryBackend);
        Assert.Equal("haiku", config.CliModelAlias);
    }

    [Fact]
    public void LoadOrCreate_ExistingFile_ReadsValues()
    {
        var path = TempConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"Port":5000,"Voice":"am_michael"}""");

        var config = RaivenConfig.LoadOrCreate(path);

        Assert.Equal(5000, config.Port);
        Assert.Equal("am_michael", config.Voice);
        Assert.Equal("claude-haiku-4-5", config.Model); // unspecified -> default
    }

    [Fact]
    public void LoadOrCreate_CorruptFile_FallsBackToDefaults()
    {
        var path = TempConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{{{not json");

        var config = RaivenConfig.LoadOrCreate(path);

        Assert.Equal(9876, config.Port);
    }

    [Fact]
    public void LoadOrCreate_MissingFile_HasAutoPlayDefaults()
    {
        var config = RaivenConfig.LoadOrCreate(TempConfigPath());

        Assert.True(config.AutoPlaySummary);
        Assert.Equal(5, config.AutoPlayDelaySeconds);
    }

    [Fact]
    public void LoadOrCreate_ExistingFile_ReadsAutoPlayValues()
    {
        var path = TempConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"AutoPlaySummary":false,"AutoPlayDelaySeconds":9}""");

        var config = RaivenConfig.LoadOrCreate(path);

        Assert.False(config.AutoPlaySummary);
        Assert.Equal(9, config.AutoPlayDelaySeconds);
    }

    [Fact]
    public void LoadOrCreate_MissingFile_HasNotificationDefaults()
    {
        var config = RaivenConfig.LoadOrCreate(TempConfigPath());

        Assert.True(config.NotifyOnFinishedTurn);
        Assert.Equal("summary", config.FinishedTurnVoice);
        Assert.Equal(0, config.FinishedTurnWordLimit);
        Assert.True(config.NotifyOnQuestion);
        Assert.Equal("announce", config.QuestionVoice);
        Assert.Equal(0, config.QuestionWordLimit);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsMutatedValues()
    {
        var path = TempConfigPath();
        var config = RaivenConfig.LoadOrCreate(path);
        config.NotifyOnQuestion = false;
        config.QuestionVoice = "message";
        config.QuestionWordLimit = 25;

        config.Save(path);
        var reloaded = RaivenConfig.LoadOrCreate(path);

        Assert.False(reloaded.NotifyOnQuestion);
        Assert.Equal("message", reloaded.QuestionVoice);
        Assert.Equal(25, reloaded.QuestionWordLimit);
        Assert.Equal(9876, reloaded.Port); // untouched keys survive the round trip
    }
}
