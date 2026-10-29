// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RECompose.Core.Library;
using RECompose.Core.Planning;
using RECompose.Core.Services;

namespace RECompose.Core.Rendering;

/// <summary>
/// One stem at one key and tempo, as the render tool makes it (REL-10). <c>Fingerprint</c> is the source file as it
/// was when rendered (<see cref="RenderKeys.Fingerprint"/>).
/// </summary>
public sealed record RenderJob(string SongId, string SongTitle, string TrackId, string SourceFile, string Fingerprint, int Semitones, double Tempo)
{
    public string FileName => RenderKeys.FileName(Fingerprint, Semitones, Tempo);
}

/// <summary>A finished render in the cache index.</summary>
public sealed record RenderRecord
{
    public string Fingerprint { get; init; } = string.Empty;
    public int Semitones { get; init; }
    public double Tempo { get; init; } = 1;
    public string File { get; init; } = string.Empty;
    public string SourceFile { get; init; } = string.Empty;
    public long Bytes { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastUsedAt { get; init; }

    public bool Matches(string fingerprint, int semitones, double tempo) =>
        Fingerprint == fingerprint && Semitones == semitones && Math.Abs(Tempo - tempo) < 1e-6;
}

/// <summary>A render that failed, so the check can say so and the queue does not retry it in a loop (REL-50).</summary>
public sealed record RenderFailure(string Fingerprint, int Semitones, double Tempo, string Problem);

/// <summary>The cache index (<c>cache/render/index.json</c>, REL-15, REL-16).</summary>
public sealed record RenderIndex
{
    public List<RenderRecord> Renders { get; init; } = [];
    public List<RenderFailure> Failures { get; init; } = [];

    public long Bytes => Renders.Sum(r => r.Bytes);

    /// <summary>A render made from this very source at this key and tempo; a changed source is a miss (REL-16).</summary>
    public RenderRecord? Find(string fingerprint, int semitones, double tempo) =>
        Renders.FirstOrDefault(r => r.Matches(fingerprint, semitones, tempo));

    public bool HasFailed(string fingerprint, int semitones, double tempo) =>
        Failures.Any(f => f.Fingerprint == fingerprint && f.Semitones == semitones && Math.Abs(f.Tempo - tempo) < 1e-6);

    public RenderIndex With(RenderRecord record) => this with
    {
        Renders = [.. Renders.Where(r => !r.Matches(record.Fingerprint, record.Semitones, record.Tempo)), record],
        Failures = [.. Failures.Where(f => !(f.Fingerprint == record.Fingerprint && f.Semitones == record.Semitones && Math.Abs(f.Tempo - record.Tempo) < 1e-6))],
    };

    public RenderIndex WithFailure(RenderFailure failure) => this with
    {
        Failures = [.. Failures.Where(f => !(f.Fingerprint == failure.Fingerprint && f.Semitones == failure.Semitones && Math.Abs(f.Tempo - failure.Tempo) < 1e-6)), failure],
    };

    public RenderIndex Touch(IEnumerable<RenderRecord> used, DateTimeOffset now)
    {
        var set = used.ToHashSet();
        return this with { Renders = [.. Renders.Select(r => set.Contains(r) ? r with { LastUsedAt = now } : r)] };
    }

    /// <summary>
    /// Least recently used first, until the cache fits <paramref name="budgetBytes"/>. Renders a service in the next
    /// seven days needs are never evicted, even over budget (REL-15). Returns the index kept and the records removed.
    /// </summary>
    public (RenderIndex Kept, IReadOnlyList<RenderRecord> Evicted) Evict(long budgetBytes, IReadOnlySet<string> protectedFiles)
    {
        var total = Bytes;
        var evicted = new List<RenderRecord>();
        foreach (var r in Renders.OrderBy(r => r.LastUsedAt).ThenBy(r => r.CreatedAt))
        {
            if (total <= budgetBytes)
                break;
            if (protectedFiles.Contains(r.File))
                continue;
            evicted.Add(r);
            total -= r.Bytes;
        }

        return (this with { Renders = [.. Renders.Except(evicted)] }, evicted);
    }
}

public static class RenderKeys
{
    /// <summary>The source as it is now: path, size and modification time, hashed. Any change is a new source (REL-16).</summary>
    public static string Fingerprint(string path, long bytes, DateTime lastWriteUtc)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{Path.GetFullPath(path).ToUpperInvariant()}|{bytes}|{lastWriteUtc.Ticks}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..20];
    }

    /// <summary>"3f2a…_+2_0950.flac": the fingerprint, the semitones and the tempo in thousandths.</summary>
    public static string FileName(string fingerprint, int semitones, double tempo) =>
        string.Create(CultureInfo.InvariantCulture, $"{fingerprint}_{semitones:+0;-0;0}_{(int)Math.Round(tempo * 1000):0000}.flac");
}

/// <summary>Which renders an entry needs (REL-10, REL-11): every stem at every key and the tempo its plan uses.</summary>
public static class RenderPlanner
{
    /// <summary>The (semitones, tempo) a track needs rendered for this plan; nothing for a plan at the written key and tempo.</summary>
    public static IReadOnlyList<(int Semitones, double Tempo)> Variants(SongPlan plan, bool transposes)
    {
        var tempo = plan.TempoFactor;
        var keys = transposes
            ? plan.Segments().Where(s => s.Src is not null).Select(s => s.Key).Distinct().Order().ToList()
            : [0];
        return [.. keys.Where(k => k != 0 || Math.Abs(tempo - 1) > 1e-9).Select(k => (k, tempo))];
    }

    /// <summary>
    /// The jobs for one entry. <paramref name="transposes"/> says whether a track follows the key (the entry's mix preset);
    /// <paramref name="fingerprint"/> is null for a stem that is missing, which is not rendered.
    /// </summary>
    public static List<RenderJob> Jobs(Song song, SongPlan plan, Func<Track, bool> transposes, Func<string, string?> fingerprint)
    {
        var jobs = new List<RenderJob>();
        foreach (var track in song.Tracks.Where(t => !t.Hidden))
        {
            var variants = Variants(plan, transposes(track));
            if (variants.Count == 0 || fingerprint(track.File) is not { } print)
                continue;
            foreach (var (semitones, tempo) in variants)
                jobs.Add(new RenderJob(song.Id, song.Title, track.Id, track.File, print, semitones, tempo));
        }

        return jobs;
    }

    /// <summary>What the service check reports for an entry (REL-1 item 4).</summary>
    public static RenderNeed Need(IReadOnlyList<RenderJob> jobs, RenderIndex index) => new(
        jobs.Count,
        jobs.Count(j => index.Find(j.Fingerprint, j.Semitones, j.Tempo) is not null),
        jobs.Count(j => index.HasFailed(j.Fingerprint, j.Semitones, j.Tempo)));

    /// <summary>Services from today to seven days ahead keep their renders through eviction (REL-15).</summary>
    public static bool IsProtected(Service service, DateTime now) =>
        service.Date is { } d && d.Date >= now.Date && d.Date <= now.Date.AddDays(7);
}
