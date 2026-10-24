// SPDX-License-Identifier: AGPL-3.0-or-later
namespace RECompose.Core.Music;

/// <summary>Major or minor (DM-11).</summary>
public enum KeyMode
{
    Major,
    Minor,
}

/// <summary>
/// A musical key: pitch class 0..11 (C = 0) and mode (DM-11).
/// Spelling is fixed: C, Db, D, Eb, E, F, F#, G, Ab, A, Bb, B (+ "m" for minor).
/// Parsing is lenient so keys in file names are understood (D# → Eb).
/// </summary>
public readonly record struct Key
{
    private static readonly string[] Spellings =
        ["C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];

    public Key(int pitchClass, KeyMode mode = KeyMode.Major)
    {
        PitchClass = Mod12(pitchClass);
        Mode = mode;
    }

    /// <summary>0..11, C = 0.</summary>
    public int PitchClass { get; }

    public KeyMode Mode { get; }

    /// <summary>The key moved by <paramref name="semitones"/> (negative = down).</summary>
    public Key Transpose(int semitones) => new(PitchClass + semitones, Mode);

    /// <summary>ASCII spelling used in files and logs: "Bb", "F#m".</summary>
    public override string ToString() =>
        Spellings[PitchClass] + (Mode == KeyMode.Minor ? "m" : string.Empty);

    /// <summary>Spelling for the UI with real accidentals: "B♭", "F♯m".</summary>
    public string ToDisplayString() =>
        ToString().Replace("b", "♭", StringComparison.Ordinal)
                  .Replace("#", "♯", StringComparison.Ordinal);

    /// <summary>Parses "Bb", "B♭", "a#", "D#m", "Ebmaj", "C minor". Returns false if not a key.</summary>
    public static bool TryParse(string? text, out Key key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = text.Trim();
        var pitch = char.ToUpperInvariant(s[0]) switch
        {
            'C' => 0,
            'D' => 2,
            'E' => 4,
            'F' => 5,
            'G' => 7,
            'A' => 9,
            'B' => 11,
            _ => -1,
        };
        if (pitch < 0)
            return false;

        var i = 1;
        if (i < s.Length)
        {
            switch (s[i])
            {
                case '#' or '♯':
                    pitch++;
                    i++;
                    break;
                case 'b' or '♭':
                    pitch--;
                    i++;
                    break;
            }
        }

        var mode = s[i..].Trim().ToLowerInvariant() switch
        {
            "" or "maj" or "major" => (KeyMode?)KeyMode.Major,
            "m" or "min" or "minor" => KeyMode.Minor,
            _ => null,
        };
        if (mode is null)
            return false;

        key = new Key(pitch, mode.Value);
        return true;
    }

    /// <summary>Like <see cref="TryParse"/> but throws <see cref="FormatException"/>.</summary>
    public static Key Parse(string text) =>
        TryParse(text, out var key) ? key : throw new FormatException($"Not a key: \"{text}\"");

    private static int Mod12(int value) => ((value % 12) + 12) % 12;
}
