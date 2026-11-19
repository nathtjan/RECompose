// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using RECompose.Core.Import;

namespace RECompose.Core.Library;

/// <summary>What a dropped or picked folder looks like (SL-47).</summary>
public enum PadGuess
{
    /// <summary>Stems of a song.</summary>
    Song,

    /// <summary>One or more pad sets: files named by key.</summary>
    PadSet,

    /// <summary>Some files are named by key but not enough to be sure: the person chooses.</summary>
    Unsure,
}

/// <summary>A pad set found in a folder: every file named by a key, by pitch class (two spellings of one pitch both listed).</summary>
public sealed record PadCandidate(string Folder, string Name, IReadOnlyDictionary<int, IReadOnlyList<string>> Keys, IReadOnlyList<string> Unkeyed)
{
    /// <summary>The first file of each key: a pad set to start the review from.</summary>
    public PadSet ToPadSet() => new()
    {
        Id = PadSet.NewId(),
        Name = Name,
        Folder = Folder,
        Samples = Keys.ToDictionary(k => k.Key, k => k.Value[0]),
    };

    /// <summary>Keys with more than one file ("C#" and "Db").</summary>
    public IEnumerable<int> Conflicts => Keys.Where(k => k.Value.Count > 1).Select(k => k.Key);
}

/// <summary>The guess for a folder, the pad sets in it (one per folder), and why, for the review.</summary>
public sealed record PadScan(PadGuess Guess, IReadOnlyList<PadCandidate> Sets, string Reason);

/// <summary>
/// SL-47: tells a pad folder from a song folder. A folder whose audio files are mostly named by key and cover most of
/// the twelve pitch classes is a pad set; with a click or guide track it is a song. A folder of pad set folders gives
/// one set per folder. The app always shows its guess for review, and the person can import either way.
/// </summary>
public static partial class PadImport
{
    private static readonly string[] PadWords = ["pad", "pads", "ambient", "drone", "swell", "atmos", "atmosphere", "texture"];

    /// <summary>
    /// The pitch class a pad file is named by, or null: "OB Pad C#", "pad_eb", "Warm - F sharp", "Gb Major", "A♭".
    /// The last key in the name wins, since titles come first. Major and minor are ignored: a pad is one pitch.
    /// </summary>
    public static int? ParseKey(string name)
    {
        int? found = null;
        foreach (Match match in KeyPattern().Matches(name))
        {
            var pc = "c d ef g a b".IndexOf(char.ToLowerInvariant(match.Groups[1].Value[0]), StringComparison.Ordinal);
            var accidental = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            pc += accidental.ToLowerInvariant() switch
            {
                "#" or "♯" or "sharp" => 1,
                "b" or "♭" or "flat" => -1,
                _ => 0,
            };
            found = ((pc % 12) + 12) % 12;
        }

        return found;
    }

    /// <summary>Classifies the audio files of <paramref name="folder"/> (absolute paths, one folder deep).</summary>
    public static PadScan Classify(string folder, IEnumerable<string> files)
    {
        var audio = files
            .Where(f => ImportRules.AudioExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, NaturalComparer.Instance)
            .ToList();
        if (audio.Count == 0)
            return new PadScan(PadGuess.Song, [], "No audio files.");

        // A folder of pad set folders: one set per subfolder.
        var top = Path.TrimEndingDirectorySeparator(folder);
        var byFolder = audio.GroupBy(f => Path.GetDirectoryName(f) ?? top).ToList();
        if (byFolder.Count > 1)
        {
            var scored = byFolder.Select(g => (Folder: g.Key, Score: Score(g.Key, g.ToList()))).ToList();
            var sets = scored.Where(s => s.Score.Guess == PadGuess.PadSet).Select(s => s.Score.Set!).ToList();
            if (sets.Count >= 2 && scored.All(s => s.Score.Guess != PadGuess.Song || !s.Score.HasStems))
                return new PadScan(PadGuess.PadSet, sets, $"{sets.Count} folders of key-named files.");
        }

        var whole = Score(top, audio);
        return new PadScan(whole.Guess, whole.Set is { } set ? [set] : [], whole.Reason);
    }

    /// <summary>
    /// The key to make <paramref name="missing"/> from: the nearest key that has a file, and the semitones to
    /// transpose it by. On a tie the higher key is transposed down. Null when no key has a file.
    /// </summary>
    public static (int From, int Semitones)? NearestSource(IEnumerable<int> available, int missing)
    {
        (int From, int Semitones)? best = null;
        foreach (var pc in available)
        {
            var up = ((missing - pc) % 12 + 12) % 12; // transposing pc up by this reaches missing
            var shift = up > 6 ? up - 12 : up;
            if (best is null || Math.Abs(shift) < Math.Abs(best.Value.Semitones) || (Math.Abs(shift) == Math.Abs(best.Value.Semitones) && shift < 0))
                best = (pc, shift);
        }

        return best;
    }

    /// <summary>
    /// The audio files of <paramref name="folder"/> as a pad set, whatever the guess: for "Import pad set…" and for
    /// turning a song into a pad set.
    /// </summary>
    public static PadCandidate Gather(string folder, IEnumerable<string> files)
    {
        var audio = files.Where(f => ImportRules.AudioExtensions.Contains(Path.GetExtension(f))).OrderBy(f => f, NaturalComparer.Instance).ToList();
        var keys = new SortedDictionary<int, List<string>>();
        var unkeyed = new List<string>();
        foreach (var file in audio)
        {
            if (ParseKey(Path.GetFileNameWithoutExtension(file)) is { } pc)
            {
                if (!keys.TryGetValue(pc, out var list))
                    keys[pc] = list = [];
                list.Add(file);
            }
            else
                unkeyed.Add(file);
        }

        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        var named = keys.Count > 0 ? keys.Values.SelectMany(f => f) : audio; // a single unnamed pad: its own name
        var name = PadSetDetector.CommonName(named.Select(f => Path.GetFileNameWithoutExtension(f)), folderName);
        return new PadCandidate(folder, name, keys.ToDictionary(k => k.Key, k => (IReadOnlyList<string>)k.Value), unkeyed);
    }

    private sealed record Scored(PadGuess Guess, PadCandidate? Set, string Reason, bool HasStems);

    private static Scored Score(string folder, List<string> audio)
    {
        var folderName = Path.GetFileName(folder);
        var names = audio.Select(f => Path.GetFileNameWithoutExtension(f)).ToList();

        if (names.Any(n => ImportRules.Detect(n).Role != TrackRole.Music))
            return new Scored(PadGuess.Song, null, "It has a click or guide track.", true);

        var candidate = Gather(folder, audio);
        var keys = candidate.Keys;
        var unkeyed = candidate.Unkeyed;
        var keyed = audio.Count - unkeyed.Count;
        var padWord = names.Append(folderName).Any(HasPadWord);
        var bpm = ImportRules.ParseBpm(folderName) is not null;
        // Song stems name instruments: an unkeyed file named for one other than keys or a pad says "song".
        var stems = unkeyed.Select(f => Path.GetFileNameWithoutExtension(f)).Count(n => !HasPadWord(n) && ImportRules.Detect(n) is { Recognised: true } d && d.Group != TrackGroups.Keys);

        PadCandidate Candidate() => candidate;

        var share = (double)keyed / audio.Count;
        var count = keys.Count;
        string Covers() => count == 12 ? "All twelve keys have a file." : $"{count} of the twelve keys have a file.";

        if (count == 12 && share >= 0.75 && stems == 0)
            return new Scored(PadGuess.PadSet, Candidate(), Covers(), false);
        if (count >= 8 && share >= 0.75 && stems == 0 && !bpm && padWord)
            return new Scored(PadGuess.PadSet, Candidate(), Covers(), false);
        if ((count >= 6 && share >= 0.5 && !bpm) || (padWord && !bpm && stems == 0 && (count >= 1 || audio.Count <= 2)))
            return new Scored(PadGuess.Unsure, Candidate(), count >= 6 ? Covers() : "The names say pad, but few keys have a file.", stems > 0);
        return new Scored(PadGuess.Song, null, "The files aren't named by key.", stems > 0);
    }

    private static bool HasPadWord(string? name) =>
        name is not null && TokenSplit().Split(name.ToLowerInvariant()).Any(t => PadWords.Contains(t));

    // A note letter on its own: an accidental right after it ("C#", "Eb", "A♭") or a word one ("F sharp", "B-flat"),
    // then an optional major/minor ("Cmaj", "D minor"); never inside a word ("Ambient", "Bed").
    [GeneratedRegex(@"(?<![\p{L}\p{N}])([a-g])(?:([#♯♭b])|[\s_-]?(sharp|flat))?(?:[\s_-]?(?:major|minor|maj|min|m))?(?![\p{L}\p{N}#♯♭])", RegexOptions.IgnoreCase)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex TokenSplit();
}
