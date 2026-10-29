// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Presentation.Services;

namespace RECompose.Presentation.Tests;

// Spoken clips (PB-71, VC-50) with the platform's own voice, where this computer has one.
public sealed class SpeechClipsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rec-tts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task VC50_a_phrase_is_rendered_once_and_then_comes_from_the_cache()
    {
        var clips = new SpeechClips(_folder);
        var ct = TestContext.Current.CancellationToken;
        if (await clips.GetAsync("Verse two next", ct) is not { } path)
        {
            Assert.Skip("No text-to-speech voice on this computer.");
            return;
        }

        Assert.True(new FileInfo(path).Length > 10_000);
        var written = File.GetLastWriteTimeUtc(path);
        Assert.Equal(path, await clips.GetAsync("Verse two next", ct));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.NotEqual(path, clips.PathFor("Bridge next"));
        Assert.Empty(Directory.GetFiles(_folder, "*.part.wav"));
        Assert.NotNull(await clips.GetAsync("It's the bridge", ct)); // a quote in the phrase
    }
}
