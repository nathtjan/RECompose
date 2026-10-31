// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Export;
using RECompose.Core.Library;
using RECompose.Core.Settings;

namespace RECompose.Core.Tests.Export;

public sealed class AudioRenderTests
{
    private static readonly AudioSettings Audio = new() { MainChannel = 0, MainWidth = 2, CueChannel = 2, CueWidth = 2 };

    private static RenderTrack T(string id, string group, int channel = 0, bool mute = false, bool solo = false, double gain = -6, string output = "bus") =>
        new(id, group, output, channel, 2, gain, mute, solo, false);

    private static readonly RenderTrack[] Tracks =
    [
        T("drums", TrackGroups.Drums),
        T("keys", TrackGroups.Keys, gain: -3),
        T("click", TrackGroups.Click, channel: 2),
        T("guide", TrackGroups.Guide, channel: 2),
    ];

    private static IReadOnlyList<RenderPass> Plan(RenderOptions options, params RenderTrack[] tracks) =>
        AudioRender.Plan("Champion", tracks.Length == 0 ? Tracks : tracks, Audio, options, []);

    private static RenderOptions Of(params RenderSource[] sources) => new() { Sources = sources };

    private static readonly RenderSource Mix = new(RenderSourceKind.Mix);

    [Fact]
    public void The_mix_holds_the_music_on_one_stereo_pair_and_leaves_out_click_and_guide()
    {
        var pass = Assert.Single(Plan(Of(Mix)));

        Assert.Equal((0, 2), (pass.Channel, pass.Width));
        Assert.Equal(["drums", "keys"], pass.Tracks.Where(t => !t.Mute).Select(t => t.Id));
        Assert.All(pass.Tracks, t => Assert.Equal((0, 2), (t.Channel, t.Width)));
    }

    [Fact]
    public void An_output_keeps_the_routing_and_writes_the_channels_it_maps_to()
    {
        var cue = Assert.Single(Plan(Of(new RenderSource(RenderSourceKind.Cue))));
        var all = Assert.Single(Plan(Of(new RenderSource(RenderSourceKind.AllOutputs))));

        Assert.Equal((2, 2), (cue.Channel, cue.Width));
        Assert.Equal((0, 4), (all.Channel, all.Width));
        Assert.Equal(2, all.Tracks.Single(t => t.Id == "click").Channel);
        Assert.Equal(4, all.Tracks.Count(t => !t.Mute));
    }

    [Fact]
    public void A_bus_takes_its_tracks_whichever_output_the_bus_is_mapped_to_and_a_named_bus_wins_over_the_group()
    {
        var lead = T("lead", TrackGroups.Keys, output: "bus:Lead Vox");
        var pass = Assert.Single(Plan(Of(new RenderSource(RenderSourceKind.Bus, "Lead Vox")), Tracks.Append(lead).ToArray()));
        var keys = Assert.Single(Plan(Of(new RenderSource(RenderSourceKind.Bus, TrackGroups.Keys)), Tracks.Append(lead).ToArray()));

        Assert.Equal(["lead"], pass.Tracks.Where(t => !t.Mute).Select(t => t.Id));
        Assert.Equal(["keys"], keys.Tracks.Where(t => !t.Mute).Select(t => t.Id));
    }

    [Fact]
    public void A_track_renders_alone_even_when_the_mixer_mutes_it_if_mutes_are_ignored()
    {
        var muted = Tracks.Select(t => t.Id == "keys" ? t with { Mute = true } : t).ToArray();
        var source = new RenderSource(RenderSourceKind.Track, "Keys", "keys");

        var respected = Assert.Single(Plan(Of(source), muted));
        var ignored = Assert.Single(Plan(Of(source) with { RespectMute = false }, muted));

        Assert.NotNull(respected.Problem);
        Assert.Null(ignored.Problem);
        Assert.Equal(["keys"], ignored.Tracks.Where(t => !t.Mute).Select(t => t.Id));
    }

    [Fact]
    public void Solo_silences_the_rest_only_when_it_is_respected()
    {
        var soloed = Tracks.Select(t => t.Id == "drums" ? t with { Solo = true } : t).ToArray();
        var keys = new RenderSource(RenderSourceKind.Track, "Keys", "keys");

        Assert.NotNull(Assert.Single(Plan(Of(keys), soloed)).Problem);
        Assert.Null(Assert.Single(Plan(Of(keys) with { RespectSolo = false }, soloed)).Problem);
        var mix = Assert.Single(Plan(Of(Mix), soloed));
        Assert.True(mix.Tracks.Single(t => t.Id == "drums").Solo);
        Assert.Null(mix.Problem);
    }

    [Fact]
    public void Volumes_are_the_mixers_or_flat()
    {
        var keys = new RenderSource(RenderSourceKind.Track, "Keys", "keys");

        Assert.Equal(-3, Assert.Single(Plan(Of(keys))).Tracks.Single(t => t.Id == "keys").GainDb);
        Assert.Equal(0, Assert.Single(Plan(Of(keys) with { RespectTrackVolume = false })).Tracks.Single(t => t.Id == "keys").GainDb);
    }

    [Fact]
    public void File_names_are_safe_and_stay_unique_when_the_folder_has_them_already()
    {
        var options = Of(Mix, Mix, new RenderSource(RenderSourceKind.Track, "Lead/Vox", "x")) with { Format = RenderFormat.Flac24 };
        var passes = AudioRender.Plan("Champion: Live", Tracks.Append(T("x", TrackGroups.Keys)).ToArray(), Audio, options, ["Champion Live - Mix.flac"]);

        Assert.Equal(["Champion Live - Mix (2).flac", "Champion Live - Mix (3).flac", "Champion Live - Track LeadVox.flac"], passes.Select(p => p.FileName));
    }

    [Fact]
    public void Flac_cannot_hold_more_than_eight_channels()
    {
        var wide = Enumerable.Range(0, 5).Select(i => T($"t{i}", TrackGroups.Keys, channel: i * 2)).ToArray();
        var all = new RenderSource(RenderSourceKind.AllOutputs);

        Assert.NotNull(Assert.Single(Plan(Of(all) with { Format = RenderFormat.Flac16 }, wide)).Problem);
        Assert.Null(Assert.Single(Plan(Of(all) with { Format = RenderFormat.Wav24 }, wide)).Problem);
    }
}
