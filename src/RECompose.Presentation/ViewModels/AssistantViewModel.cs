// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Messages;
using RECompose.Core.Assistant;
using RECompose.Core.Commands;
using RECompose.Core.Settings;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

public enum AssistantRowKind
{
    User,
    Assistant,
    Done,
    Error,
    Undid,
    Card,
}

/// <summary>One row of the panel (UI-17): a bubble, a ✓ tool row, an ⊗ error, ↶ an undo, or a Perform confirmation card.</summary>
public sealed class AssistantRow : ObservableObject
{
    private string _status = string.Empty;

    public AssistantRow(AssistantRowKind kind, string text)
    {
        Kind = kind;
        Text = kind switch
        {
            AssistantRowKind.Done => $"✓ {text}",
            AssistantRowKind.Error => $"⊗ {text}",
            AssistantRowKind.Undid => $"↶ Undid: {text}",
            _ => text,
        };
    }

    public AssistantRowKind Kind { get; }
    public string Text { get; }
    public bool IsUser => Kind == AssistantRowKind.User;
    public bool IsAssistant => Kind == AssistantRowKind.Assistant;
    public bool IsCard => Kind == AssistantRowKind.Card;
    public bool IsNote => Kind is AssistantRowKind.Done or AssistantRowKind.Error or AssistantRowKind.Undid;
    public bool IsError => Kind == AssistantRowKind.Error;

    /// <summary>Card: what Apply does again against the state at that moment.</summary>
    internal (string Tool, JsonObject Input)? Call { get; init; }

    public RelayCommand? ApplyCommand { get; init; }
    public RelayCommand? DiscardCommand { get; init; }

    /// <summary>Card: empty while waiting, then "Applied" or "Discarded".</summary>
    public string Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
                OnPropertyChanged(nameof(IsPending));
        }
    }

    public bool IsPending => IsCard && Status.Length == 0;
}

/// <summary>
/// The assistant panel (UI-17, AS-2…50): the conversation, the tool loop (at most 10 rounds), one undo group per message,
/// confirmation cards in Perform, and Settings › Assistant (AS-1).
/// </summary>
public sealed class AssistantViewModel : ObservableObject
{
    public const string KeyName = "anthropic-api-key";

    private readonly MainViewModel _main;
    private readonly List<MessageParam> _history = [];
    private readonly List<(UndoableChange Group, string Text)> _groups = [];
    private readonly List<string> _cardNotes = [];
    private string _conversationId = NewConversationId();
    private ISecretStore? _secrets;
    private bool _isOpen;
    private bool _isWorking;
    private bool _showSettings;
    private string _draft = string.Empty;
    private string _keyText = string.Empty;
    private string _testResult = string.Empty;
    private AssistantClient? _client;

    public AssistantViewModel(MainViewModel main)
    {
        _main = main;
        SendCommand = new RelayCommand(() => SendAsync(Draft), () => !IsWorking && Draft.Trim().Length > 0);
        UndoCommand = new RelayCommand(UndoLast, () => _groups.Count > 0 && !IsWorking);
        NewConversationCommand = new RelayCommand(NewConversation, () => !IsWorking);
        SettingsCommand = new RelayCommand(() => ShowSettings = !ShowSettings);
        TestCommand = new RelayCommand(TestAsync);
        SaveKeyCommand = new RelayCommand(SaveKey);
    }

    public ObservableCollection<AssistantRow> Rows { get; } = [];

    public RelayCommand SendCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand NewConversationCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand TestCommand { get; }
    public RelayCommand SaveKeyCommand { get; }

    /// <summary>The OS credential store; tests set a memory one.</summary>
    internal ISecretStore Secrets
    {
        get => _secrets ??= new OsSecretStore(_main.DataRoot);
        set => _secrets = value;
    }

    /// <summary>Tests send the requests to a fake API.</summary>
    internal Func<string, string?, AssistantClient> ClientFactory { get; set; } = (key, workspace) => new AssistantClient(key, workspace);

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (Set(ref _isOpen, value))
                OnPropertyChanged(nameof(NeedsKey));
        }
    }

    public bool HasKey => !string.IsNullOrEmpty(Secrets.Get(KeyName));

    /// <summary>The setup card (UI-80): no key yet.</summary>
    public bool NeedsKey => !HasKey;

    public bool IsWorking
    {
        get => _isWorking;
        private set
        {
            if (Set(ref _isWorking, value))
            {
                SendCommand.Refresh();
                UndoCommand.Refresh();
                NewConversationCommand.Refresh();
            }
        }
    }

    public string Draft
    {
        get => _draft;
        set
        {
            if (Set(ref _draft, value ?? string.Empty))
                SendCommand.Refresh();
        }
    }

    public bool IsEmpty => Rows.Count == 0;

    // ---- Settings › Assistant (AS-1) -----------------------------------------------------------------------

    public bool ShowSettings { get => _showSettings; set => Set(ref _showSettings, value); }

    /// <summary>A new key to store; the stored one is never shown.</summary>
    public string KeyText { get => _keyText; set => Set(ref _keyText, value ?? string.Empty); }

    public string KeyStatus => HasKey ? "A key is stored in the system's credential store." : "No key yet.";

    public string WorkspaceId
    {
        get => _main.AssistantSettings.WorkspaceId ?? string.Empty;
        set => Save(s => s with { WorkspaceId = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
    }

    public string Model
    {
        get => _main.AssistantSettings.Model;
        set => Save(s => s with { Model = string.IsNullOrWhiteSpace(value) ? AssistantPrompt.DefaultModel : value.Trim() });
    }

    public IReadOnlyList<string> SuggestedModels => AssistantPrompt.SuggestedModels;

    /// <summary>Changes during Perform: Ask me to confirm (true) or Not allowed (AS-1, AS-45).</summary>
    public bool ConfirmInPerform
    {
        get => _main.AssistantSettings.ConfirmInPerform;
        set => Save(s => s with { ConfirmInPerform = value });
    }

    public string TestResult { get => _testResult; private set => Set(ref _testResult, value); }

    public string PrivacyNote => AssistantPrompt.PrivacyNote;

    private void Save(Func<AssistantSettings, AssistantSettings> change)
    {
        _main.SaveAssistantSettings(change(_main.AssistantSettings));
        _client = null;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Stores the typed key; an empty field keeps the stored one.</summary>
    private void SaveKey()
    {
        if (KeyText.Trim().Length > 0)
        {
            try
            {
                Secrets.Set(KeyName, KeyText.Trim());
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
                TestResult = e.Message;
                return;
            }

            KeyText = string.Empty;
            AppLog.Write("Assistant", "API key stored");
        }

        _client = null;
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(NeedsKey));
        OnPropertyChanged(nameof(KeyStatus));
    }

    private async Task TestAsync()
    {
        if (Client() is not { } client)
        {
            TestResult = "Add a key first.";
            return;
        }

        TestResult = "Testing…";
        try
        {
            await client.TestAsync(Model);
            TestResult = "✓ Connected";
        }
        catch (Exception e)
        {
            TestResult = AssistantClient.ErrorText(e);
        }
    }

    private AssistantClient? Client()
    {
        if (_client is not null)
            return _client;
        var key = Secrets.Get(KeyName);
        return string.IsNullOrEmpty(key) ? null : _client = ClientFactory(key, _main.AssistantSettings.WorkspaceId);
    }

    // ---- The conversation (AS-10, AS-20) --------------------------------------------------------------------

    private void Add(AssistantRow row)
    {
        Rows.Add(row);
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Sends one message and runs the tool loop until the model answers in words.</summary>
    public async Task SendAsync(string text)
    {
        text = text.Trim();
        if (IsWorking || text.Length == 0)
            return;
        if (Client() is not { } client)
        {
            OnPropertyChanged(nameof(NeedsKey));
            return;
        }

        Draft = string.Empty;
        Add(new AssistantRow(AssistantRowKind.User, text));
        IsWorking = true;
        var mark = _main.UndoMark;
        var done = new List<string>();
        TrimHistory();
        var before = _history.Count;

        List<ContentBlockParam> content = [new TextBlockParam { Text = text }];
        if (_cardNotes.Count > 0)
        {
            content.Add(new TextBlockParam { Text = string.Join("\n", _cardNotes) });
            _cardNotes.Clear();
        }

        content.Add(new TextBlockParam { Text = _main.AssistantState() });
        _history.Add(new UserMessageParam { Content = content });

        try
        {
            for (var round = 1; ; round++)
            {
                var reply = await client.SendAsync(Model, _history);
                List<ContentBlockParam> echo = [];
                List<ContentBlockParam> results = [];
                var words = new List<string>();
                foreach (var block in reply.Content)
                {
                    if (block.TryPickText(out var t))
                    {
                        echo.Add(new TextBlockParam { Text = t.Text });
                        words.Add(t.Text);
                    }
                    else if (block.TryPickThinking(out var thinking))
                    {
                        echo.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                    }
                    else if (block.TryPickRedactedThinking(out var redacted))
                    {
                        echo.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
                    }
                    else if (block.TryPickToolUse(out var use))
                    {
                        echo.Add(new ToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                        var result = Execute(use.Name, JsonSerializer.SerializeToNode(use.Input) as JsonObject ?? [], done);
                        results.Add(new ToolResultBlockParam { ToolUseID = use.ID, Content = result.ToJsonString(), IsError = result["ok"]?.GetValue<bool>() == false && result["status"] is null });
                    }
                }

                _history.Add(new AssistantMessageParam { Content = echo });

                if (reply.StopReason == "refusal")
                {
                    Add(new AssistantRow(AssistantRowKind.Error, "The assistant declined this request."));
                    break;
                }

                if (results.Count == 0)
                {
                    if (string.Join("\n\n", words).Trim() is { Length: > 0 } answer)
                        Add(new AssistantRow(AssistantRowKind.Assistant, answer));
                    break;
                }

                results.Add(new TextBlockParam { Text = _main.AssistantState() });
                _history.Add(new UserMessageParam { Content = results });
                if (round == AssistantPrompt.MaxRounds)
                {
                    // The model never saw an answer to these results; drop the round so the next message starts clean.
                    _history.RemoveRange(before, _history.Count - before);
                    Add(new AssistantRow(AssistantRowKind.Error, AssistantPrompt.ApiError($"stopped after {AssistantPrompt.MaxRounds} rounds of tool calls.")));
                    break;
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Error bubbles are not sent to the model (AS-20): the unanswered message goes from the history.
            _history.RemoveRange(before, _history.Count - before);
            Add(new AssistantRow(AssistantRowKind.Error, AssistantClient.ErrorText(e)));
            AppLog.Write("Assistant", $"Error: {e.Message}");
        }
        finally
        {
            if (_main.GroupUndo(mark, done.Count == 1 ? $"Assistant: {done[0]}" : $"Assistant: {done.Count} changes") is { } group)
            {
                _groups.Add((group, string.Join(" ", done)));
                UndoCommand.Refresh();
            }

            IsWorking = false;
        }
    }

    /// <summary>
    /// AS-20 keeps the last 40 messages. Older ones go, starting at a message the user typed, and the retained turns lose their
    /// thinking blocks, which were made with the full history (text and tool calls stay).
    /// </summary>
    private void TrimHistory()
    {
        if (_history.Count < AssistantPrompt.HistoryLimit)
            return;
        var cut = _history.Count - AssistantPrompt.HistoryLimit + 1;
        while (cut < _history.Count && !IsTypedMessage(_history[cut]))
            cut++;
        _history.RemoveRange(0, cut);
        for (var i = 0; i < _history.Count; i++)
        {
            if (_history[i].Value is AssistantMessageParam { Content.Value: IReadOnlyList<ContentBlockParam> blocks } assistant)
                _history[i] = assistant with { Content = blocks.Where(b => b.Value is not (ThinkingBlockParam or RedactedThinkingBlockParam)).ToList() };
        }
    }

    private static bool IsTypedMessage(MessageParam message) =>
        message.Value is UserMessageParam { Content.Value: IReadOnlyList<ContentBlockParam> blocks } && blocks.All(b => b.Value is TextBlockParam);

    /// <summary>One tool call: reads answer at once; changes apply in Prepare and become a card in Perform (AS-45).</summary>
    private JsonObject Execute(string tool, JsonObject input, List<string> done)
    {
        var spec = AssistantPrompt.Tool(tool);
        if (spec is { Mutates: true } && _main.IsPerform)
        {
            if (!ConfirmInPerform)
                return AssistantResult.Error("Changes are not allowed in Perform. The MD can allow them with confirmation in Settings › Assistant.").ToJson();
            var check = AssistantCommands.Run(tool, input, _main.AssistantWorld());
            if (!check.Ok)
                return check.ToJson();
            AssistantRow? card = null;
            card = new AssistantRow(AssistantRowKind.Card, check.Summary)
            {
                Call = (tool, input),
                ApplyCommand = new RelayCommand(() => ApplyCard(card!)),
                DiscardCommand = new RelayCommand(() => DiscardCard(card!)),
            };
            Add(card);
            return new JsonObject { ["ok"] = false, ["status"] = "pending_confirmation", ["summary"] = $"Shown to the MD for confirmation: {check.Summary}" };
        }

        var result = AssistantCommands.Run(tool, input, _main.AssistantWorld());
        if (result is { Ok: true, Change: { } change })
        {
            _main.ApplyAssistantChange(change);
            done.Add(result.Summary);
            Add(new AssistantRow(AssistantRowKind.Done, result.Summary));
        }
        else if (!result.Ok)
        {
            AppLog.Write("Assistant", $"{tool}: {result.Summary}");
        }

        return result.ToJson();
    }

    /// <summary>Apply on a card: the call runs again against the state now, so edits made since are kept (AS-45).</summary>
    private void ApplyCard(AssistantRow card)
    {
        if (!card.IsPending || card.Call is not { } call)
            return;
        var result = AssistantCommands.Run(call.Tool, call.Input, _main.AssistantWorld());
        if (result is { Ok: true, Change: { } change })
        {
            var mark = _main.UndoMark;
            _main.ApplyAssistantChange(change);
            if (_main.GroupUndo(mark, $"Assistant: {result.Summary}") is { } group)
                _groups.Add((group, result.Summary));
            card.Status = "Applied";
            _cardNotes.Add($"The MD applied: {result.Summary}");
            UndoCommand.Refresh();
        }
        else
        {
            card.Status = "Could not apply";
            Add(new AssistantRow(AssistantRowKind.Error, result.Summary));
            _cardNotes.Add($"Applying failed: {result.Summary}");
        }
    }

    private void DiscardCard(AssistantRow card)
    {
        if (!card.IsPending)
            return;
        card.Status = "Discarded";
        _cardNotes.Add($"The MD discarded: {card.Text}");
        AppLog.Write("Assistant", $"Discarded: {card.Text}");
    }

    /// <summary>The panel's Undo (AS-40): the latest assistant group not yet undone.</summary>
    private void UndoLast()
    {
        if (_groups.Count == 0)
            return;
        var (group, text) = _groups[^1];
        if (!_main.UndoGroup(group))
        {
            Add(new AssistantRow(AssistantRowKind.Error, "Later changes were made after it. Undo those first with Ctrl+Z."));
            return;
        }

        _groups.RemoveAt(_groups.Count - 1);
        Add(new AssistantRow(AssistantRowKind.Undid, text));
        _cardNotes.Add($"The MD undid: {text}");
        UndoCommand.Refresh();
    }

    /// <summary>New conversation (AS-20): the transcript goes to assistant/&lt;id&gt;.json; undo history is kept.</summary>
    private void NewConversation()
    {
        if (Rows.Count > 0)
        {
            try
            {
                var folder = Path.Combine(_main.DataRoot, "assistant");
                Directory.CreateDirectory(folder);
                var transcript = new JsonArray([.. Rows.Select(r => (JsonNode)new JsonObject { ["kind"] = r.Kind.ToString(), ["text"] = r.Text, ["status"] = r.Status })]);
                File.WriteAllText(Path.Combine(folder, $"{_conversationId}.json"), transcript.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                AppLog.Error("Archiving the assistant conversation", e);
            }
        }

        Rows.Clear();
        _history.Clear();
        _cardNotes.Clear();
        _conversationId = NewConversationId();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static string NewConversationId() => DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
}
