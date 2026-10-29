// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using RECompose.Core.Planning;
using RECompose.Core.Sections;
using RECompose.Core.Voice;
using RECompose.Engine;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>
/// Voice control (milestone 7, VC-*): the engine sends what the talkback mic heard, the listen worker turns it into text,
/// and Core's grammar, resolver, gate and safeguards turn the text into the same live actions as a key or a pedal.
/// </summary>
public sealed partial class MainViewModel
{
    public const double ProposalSec = 4; // VC-46
    public const double CaptionSec = 3;  // VC-55

    private VoiceWorker? _voiceWorker;
    private readonly VoiceSafeguards _voiceSafeguards = new();
    private VoiceState? _voiceState;
    private string? _voiceError;
    private string _voiceHeard = string.Empty, _voiceCaption = string.Empty, _proposalText = string.Empty;
    private int _captionSerial, _proposalSerial;
    private VoiceResolution? _proposal;
    private bool _talkHeld;
    private SpeechClips? _clips;
    private readonly List<SpokenWord> _spoken = []; // the MD's confirmations, for the gate (VC-40)

    private SpeechClips Clips => _clips ??= new SpeechClips(Path.Combine(_store.Root, "cache", "tts"));

    /// <summary>Settings › Audio while it is open: what is heard is only described there ("would have done…", VC-1).</summary>
    internal AudioSettingsViewModel? VoiceTest { get; set; }

    /// <summary>VC-55: `heard "let s go to the bridge"`.</summary>
    public string VoiceHeard { get => _voiceHeard; private set => Set(ref _voiceHeard, value); }

    /// <summary>VC-55: what was done ("Bridge 1 next", "ignored — guide talking"); fades after 3 s.</summary>
    public string VoiceCaption { get => _voiceCaption; private set => Set(ref _voiceCaption, value); }

    /// <summary>VC-46: the proposed action, waiting for "yes", GO or a pedal.</summary>
    public string ProposalText { get => _proposalText; private set => Set(ref _proposalText, value); }

    public bool IsProposing => _proposal is not null;

    /// <summary>VC-5: Off, Listening, Hearing…, PTT or Error.</summary>
    public string VoiceIndicator =>
        _settings.Audio.VoiceChannel < 0 ? "Off"
        : _micDenied || _voiceError is not null || _voiceState is { On: false } ? "Error"
        : _voiceState?.Hearing == true ? "Hearing…"
        : _settings.Audio.VoicePushToTalk && !_talkHeld ? "PTT"
        : "Listening";

    public bool VoiceOn => _settings.Audio.VoiceChannel >= 0;
    public bool VoiceOk => VoiceIndicator is "Listening" or "Hearing…" or "PTT";
    public string VoiceToolTip => _micDenied ? $"{MicrophoneAccess.Problem} Click to open it." : _voiceError ?? "Voice control";

    /// <summary>AT-61: with the microphone blocked, the Voice indicator opens the system's privacy page; otherwise Settings › Audio.</summary>
    public RelayCommand VoiceIndicatorCommand => _voiceIndicator ??= new RelayCommand(() =>
    {
        if (_micDenied)
            MicrophoneAccess.OpenSettings();
        else
            OpenSettingsCommand.Execute(null);
    });

    private RelayCommand? _voiceIndicator;
    private bool _micDenied;
    private long _micCheckedAt;

    /// <summary>Reads the microphone permission at most every 2 s, so turning it on in the system settings is picked up.</summary>
    private void CheckMicrophone(bool now = false)
    {
        if (!now && Environment.TickCount64 - _micCheckedAt < 2000)
            return;
        _micCheckedAt = Environment.TickCount64;
        var denied = _settings.Audio.VoiceChannel >= 0 && MicrophoneAccess.Denied();
        if (denied == _micDenied)
            return;
        _micDenied = denied;
        AppLog.Write("Voice", denied ? "Microphone access is off in the system settings" : "Microphone access is on");
        RefreshVoice();
    }

    /// <summary>The mic's level, 0 to 1 (−60 to 0 dBFS), for the indicator's meter.</summary>
    public double VoiceLevel => Math.Clamp(((_voiceState?.LevelDb ?? -100) + 60) / 60, 0, 1);

    private void HookVoice(EngineClient engine)
    {
        engine.UtteranceReceived += u => _ui.Post(() => _ = OnUtteranceAsync(u));
        engine.VoiceError += message => _ui.Post(() =>
        {
            _voiceError = message;
            AppLog.Write("Voice", $"Input not opened: {message}");
            RefreshVoice();
        });
    }

    /// <summary>Tells the engine the talkback mic of Settings › Audio (VC-1).</summary>
    private async Task SendVoiceInputAsync()
    {
        if (_engine is not { } engine)
            return;
        try
        {
            _voiceError = null;
            var a = _settings.Audio;
            if (!await engine.SetVoiceInputAsync(a.VoiceChannel, a.VoiceDevice) && a.VoiceChannel >= 0)
                _voiceError ??= "The voice input is not open.";
        }
        catch (Exception ex) when (ex is EngineErrorException or EngineDisconnectedException or OperationCanceledException)
        {
            _voiceError = ex.Message;
            AppLog.Error("Voice", ex);
        }

        CheckMicrophone(now: true);
        RefreshVoice();
    }

    private void ApplyVoice(VoiceState? state)
    {
        CheckMicrophone();
        if (state == _voiceState)
            return;
        _voiceState = state;
        if (state is { On: true })
            _voiceError = null;
        RefreshVoice();
        VoiceTest?.ShowLevel(VoiceLevel);
    }

    private void RefreshVoice()
    {
        OnPropertyChanged(nameof(VoiceIndicator));
        OnPropertyChanged(nameof(VoiceOn));
        OnPropertyChanged(nameof(VoiceOk));
        OnPropertyChanged(nameof(VoiceLevel));
        OnPropertyChanged(nameof(VoiceToolTip));
    }

    /// <summary>The Talk control (push-to-talk) went down or up.</summary>
    private void Talk(bool held)
    {
        _talkHeld = held;
        RefreshVoice();
    }

    private async Task OnUtteranceAsync(Utterance utterance)
    {
        if (_settings.Audio.VoicePushToTalk && !_talkHeld && VoiceTest is null)
            return;

        // shortcut: the span is placed from the playhead when the utterance arrives (tens of ms late); the gate allows 300 ms.
        var end = _planPosition - utterance.TrailingSec;
        var start = end - (utterance.DurationSec - utterance.TrailingSec);
        Recognised heard;
        try
        {
            _voiceWorker ??= new VoiceWorker(_enginePath, await _analysis.Model.EnsureAsync());
            heard = await _voiceWorker.RecogniseAsync(utterance.Pcm16, VoicePrompt());
        }
        catch (Exception ex) when (ex is EngineUnavailableException or SpeechModelException or IOException)
        {
            _voiceError = ex.Message;
            AppLog.Write("Voice", $"Recogniser: {ex.Message}");
            RefreshVoice();
            return;
        }

        await HearAsync(heard.Text, heard.Confidence, start, end);
    }

    /// <summary>VC-11: the song's section names and the command words.</summary>
    private string VoicePrompt() => VoiceText.Prompt(_song?.Plan.Sections ?? []);

    /// <summary>What was heard, from the recogniser (or a test): parse, gate, resolve, confirm or act, caption and log.</summary>
    internal async Task HearAsync(string text, double confidence, double startSec, double endSec)
    {
        var normalized = VoiceText.Normalize(text);
        if (normalized.Length == 0)
            return;
        var a = _settings.Audio;
        var custom = _song?.Model.Sections.Where(s => s.Type == SectionType.Other).Select(s => s.Name) ?? [];
        var commands = VoiceGrammar.Parse(text, custom, a.VoiceBareNames);

        if (VoiceTest is { } test)
        {
            var described = _song is null || commands.Count == 0
                ? commands.Count == 0 ? "no command" : "nothing loaded"
                : string.Join(", ", commands.Select(c => VoiceLines.Caption(VoiceResolver.Resolve(c, EnsureLive()!.Committed, _planPosition))));
            test.ShowHeard($"heard \"{normalized}\"", $"would have done: {described}");
            return;
        }

        VoiceHeard = $"heard \"{normalized}\"";
        if (commands.Count == 0)
        {
            ShowVoiceCaption("didn't catch that");
            return;
        }

        foreach (var command in commands)
            await RunVoiceAsync(command, normalized, confidence, startSec, endSec);
    }

    private async Task RunVoiceAsync(VoiceCommand command, string heard, double confidence, double startSec, double endSec)
    {
        var a = _settings.Audio;
        if (_song is null || State == TransportState.Stopped || EnsureLive() is not { } live)
        {
            AppLog.Line(VoiceLines.Ignored(command, VoiceLines.NotPlaying, heard: heard)); // VC-2
            return;
        }

        var resolution = VoiceResolver.Resolve(command, live.Committed, _planPosition);
        if (a.VoiceGate && IsGuideEcho(command, startSec, endSec))
        {
            AppLog.Line(VoiceLines.Ignored(command, VoiceLines.GuideTalking, resolution.Label));
            ShowVoiceCaption("ignored — guide talking");
            return;
        }

        if (resolution.Kind == VoiceResultKind.Ignored)
        {
            AppLog.Line(VoiceLines.Log(resolution));
            ShowVoiceCaption(VoiceLines.Caption(resolution));
            return;
        }

        _voiceSafeguards.MinConfidence = a.VoiceMinConfidence;
        switch (_voiceSafeguards.Check(resolution, confidence, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency))
        {
            case VoiceVerdict.LowConfidence:
                AppLog.Line(VoiceLines.Ignored(command, VoiceLines.LowConfidence, heard: heard));
                ShowVoiceCaption(VoiceLines.LowConfidence);
                return;
            case VoiceVerdict.Duplicate:
                return; // VC-45: said twice within 2 s
        }

        switch (resolution.Kind)
        {
            case VoiceResultKind.Cancel:
                ClearProposal();
                await CancelLiveAsync();
                AppLog.Line(VoiceLines.Log(resolution));
                ShowVoiceCaption(VoiceLines.Caption(resolution));
                break;
            case VoiceResultKind.Confirm:
                await ConfirmVoiceAsync();
                break;
            case VoiceResultKind.Stop:
                ClearProposal();
                AppLog.Line(VoiceLines.Log(resolution));
                ShowVoiceCaption(VoiceLines.Caption(resolution));
                await StopAsync(); // shortcut: the transport's own stop fade (50 ms), not the 1 s of VC-20
                break;
            case VoiceResultKind.Action when a.VoiceConfirm:
                Propose(resolution);
                break;
            case VoiceResultKind.Action:
                await ApplyVoiceAsync(resolution);
                break;
        }
    }

    /// <summary>VC-40: the guide (or a clip the app played) was saying one of the command's keywords at the time.</summary>
    private bool IsGuideEcho(VoiceCommand command, double startSec, double endSec)
    {
        if (_song is null)
            return false;
        var guide = _song.Model.GuideAnalysis?.Words is { Count: > 0 } words
            ? VoiceGate.GuideWords(_song.Plan, words, startSec - 1, endSec + 1)
            : [];
        return VoiceGate.IsEcho(command.Keywords, startSec, endSec, guide.Concat(_spoken));
    }

    private async Task ApplyVoiceAsync(VoiceResolution resolution)
    {
        var action = resolution.Action!;
        var result = await LiveAsync(action.Kind, action.ItemId, action.SectionId, ActionSource.Voice, action.Timing);
        if (result.Outcome == LiveOutcome.Rejected)
        {
            AppLog.Line(VoiceLines.Ignored(resolution.Command, result.Message.TrimEnd('.').ToLowerInvariant(), resolution.Label));
            ShowVoiceCaption(result.Message);
            return;
        }

        var alreadyNext = result.Outcome == LiveOutcome.AlreadyNext;
        if (alreadyNext)
            SayToMd(VoiceLines.Confirmation(resolution, alreadyNext: true));
        AppLog.Line(VoiceLines.Log(resolution, alreadyNext));
        ShowVoiceCaption(VoiceLines.Caption(resolution, alreadyNext));
    }

    /// <summary>VC-46: shown for 4 s; "yes", GO or a pedal confirms it.</summary>
    private void Propose(VoiceResolution resolution)
    {
        _proposal = resolution;
        var serial = ++_proposalSerial;
        ProposalText = $"VOICE? {VoiceLines.Caption(resolution)} · say \"yes\" or press GO";
        OnPropertyChanged(nameof(IsProposing));
        _ = Task.Delay(TimeSpan.FromSeconds(ProposalSec)).ContinueWith(_ => _ui.Post(() =>
        {
            if (serial != _proposalSerial || _proposal is not { } expired)
                return;
            ClearProposal();
            AppLog.Line($"Voice: {expired.Label ?? expired.Command.Intent.ToString().ToLowerInvariant()} not confirmed");
        }), TaskScheduler.Default);
    }

    /// <summary>Confirms the proposed action; false when there is none.</summary>
    internal async Task<bool> ConfirmVoiceAsync()
    {
        if (_proposal is not { } proposal)
            return false;
        ClearProposal();
        await ApplyVoiceAsync(proposal);
        return true;
    }

    private void ClearProposal()
    {
        if (_proposal is null)
            return;
        _proposal = null;
        ++_proposalSerial;
        ProposalText = string.Empty;
        OnPropertyChanged(nameof(IsProposing));
    }

    private void ShowVoiceCaption(string caption)
    {
        VoiceCaption = caption;
        var serial = ++_captionSerial;
        _ = Task.Delay(TimeSpan.FromSeconds(CaptionSec)).ContinueWith(_ => _ui.Post(() =>
        {
            if (serial != _captionSerial)
                return;
            VoiceCaption = string.Empty;
            VoiceHeard = string.Empty;
        }), TaskScheduler.Default);
    }

    /// <summary>VC-50: a short phrase in the MD's ear, when an MD confirm output is set. Also the gate's own speech.</summary>
    internal void SayToMd(string phrase)
    {
        if (_settings.Audio.MdChannel is not { } channel || _engine is not { } engine || phrase.Length == 0)
            return;
        _ = SayAsync(engine, phrase, channel, _settings.Audio.MdWidth, _settings.Audio.MdGainDb);
    }

    private async Task SayAsync(EngineClient engine, string phrase, int channel, int width, double gainDb)
    {
        try
        {
            if (await Clips.GetAsync(phrase) is not { } file)
                return;
            var seconds = await engine.PlayClipAsync(file, channel, width, gainDb);
            var at = _planPosition;
            _spoken.RemoveAll(w => w.End < at - 30);
            _spoken.AddRange(VoiceGate.Clip(phrase, at, at + seconds));
        }
        catch (Exception ex) when (ex is EngineErrorException or EngineDisconnectedException or OperationCanceledException)
        {
            AppLog.Error("Voice", ex);
        }
    }

    /// <summary>Renders the confirmations this song can need, so the first one is not late (PB-71).</summary>
    private void PrepareClips(SongPlan plan)
    {
        if (_settings.Audio.MdChannel is null)
            return;
        var phrases = new[] { "Cancelled", "Vamping", "Moving on", "Repeat", "Ending after this" }
            .Concat(plan.Sections.SelectMany(s => new[] { $"{VoiceText.Spoken(s.Label)} next", $"{VoiceText.Spoken(s.Label)} is already next" }))
            .ToList();
        var clips = Clips;
        _ = Task.Run(async () =>
        {
            foreach (var phrase in phrases)
                await clips.GetAsync(phrase);
        });
    }

    private async Task DisposeVoiceAsync()
    {
        if (_voiceWorker is { } worker)
            await worker.DisposeAsync();
        _voiceWorker = null;
    }
}
