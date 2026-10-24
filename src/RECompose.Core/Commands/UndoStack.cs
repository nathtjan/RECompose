// SPDX-License-Identifier: AGPL-3.0-or-later
namespace RECompose.Core.Commands;

/// <summary>A change that can be undone. <see cref="Description"/> is the toast text ("Fader Drums").</summary>
public sealed record UndoableChange(string Description, Action Undo, Action Redo)
{
    /// <summary>Changes with the same key in quick succession merge (one fader drag = one undo step).</summary>
    public string? MergeKey { get; init; }
}

/// <summary>Undo and redo (spec 18 §1 CommandBus, milestone 1 subset).</summary>
public sealed class UndoStack
{
    private readonly List<UndoableChange> _undo = [];
    private readonly List<UndoableChange> _redo = [];
    private DateTime _lastPush = DateTime.MinValue;

    public TimeSpan MergeWindow { get; init; } = TimeSpan.FromMilliseconds(800);
    public int Limit { get; init; } = 200;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Records a change that has already been applied.</summary>
    public void Push(UndoableChange change, DateTime? now = null)
    {
        var time = now ?? DateTime.UtcNow;
        _redo.Clear();

        if (change.MergeKey is not null && _undo.Count > 0 && _undo[^1].MergeKey == change.MergeKey && time - _lastPush < MergeWindow)
        {
            // Keep the first undo (back to where the drag started) and the latest redo.
            var first = _undo[^1];
            _undo[^1] = first with { Redo = change.Redo, Description = change.Description };
        }
        else
        {
            _undo.Add(change);
            if (_undo.Count > Limit)
                _undo.RemoveAt(0);
        }

        _lastPush = time;
    }

    /// <summary>Undoes the latest change and returns it, or null.</summary>
    public UndoableChange? Undo()
    {
        if (_undo.Count == 0)
            return null;
        var change = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        change.Undo();
        _redo.Add(change);
        _lastPush = DateTime.MinValue;
        return change;
    }

    public UndoableChange? Redo()
    {
        if (_redo.Count == 0)
            return null;
        var change = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        change.Redo();
        _undo.Add(change);
        _lastPush = DateTime.MinValue;
        return change;
    }
}
