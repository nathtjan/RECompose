// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using RECompose.Core.Library;
using RECompose.Core.Sections;
using RECompose.Core.Services;

namespace RECompose.Core.Tests.Persistence;

// A stem past 4 GB needs zip64 and must stream: writes about 9 GB to the temp folder, so it only runs when asked for
// (set RECOMPOSE_LARGE_TESTS=1).
public sealed class LargeExchangeTests : IDisposable
{
    private const long StemBytes = 4_500_000_000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rec-large-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_stem_over_four_gigabytes_round_trips_without_being_held_in_memory()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("RECOMPOSE_LARGE_TESTS") == "1", "Set RECOMPOSE_LARGE_TESTS=1 to run");
        Directory.CreateDirectory(_root);
        var stem = Path.Combine(_root, "keys.wav");
        using (var file = new FileStream(stem, FileMode.Create, FileAccess.Write))
        {
            file.SetLength(StemBytes);
            file.Position = 0;
            file.Write("HEAD"u8);
            file.Position = StemBytes - 4;
            file.Write("TAIL"u8);
        }

        var song = new Song
        {
            Id = "a",
            Title = "Big",
            Source = new SongSource { Path = "x" },
            OriginalKey = "C",
            Bpm = 100,
            DurationSec = 100,
            Tracks = [new Track { Id = "t0", Name = "Keys", File = stem }],
            Sections = [Section.Create("Verse 1", 0, SectionOrigin.Manual)],
        };
        var service = new Service { Id = "s", Name = "Big", Entries = [ServiceEdit.NewEntry(song)] };
        var package = Path.Combine(_root, "big.recservice");
        using (var output = File.Create(package))
            Assert.Empty(ServiceExchange.Export(service, [song], output, f => File.OpenRead(f)));

        var media = Path.Combine(_root, "other", "media");
        ServiceImport result;
        using (var input = File.OpenRead(package))
            result = ServiceExchange.Import(input, [], f => File.Exists(f) && f != stem, () => "new", media);

        Assert.Empty(result.Missing);
        var copy = Assert.Single(result.NewSongs).Tracks[0].File;
        using var imported = File.OpenRead(copy);
        Assert.Equal(StemBytes, imported.Length);
        var buffer = new byte[4];
        imported.ReadExactly(buffer);
        Assert.Equal("HEAD"u8.ToArray(), buffer);
        imported.Position = StemBytes - 4;
        imported.ReadExactly(buffer);
        Assert.Equal("TAIL"u8.ToArray(), buffer);

        Assert.True(Process.GetCurrentProcess().PeakWorkingSet64 < 1L << 30, "the stem was held in memory");
    }
}
