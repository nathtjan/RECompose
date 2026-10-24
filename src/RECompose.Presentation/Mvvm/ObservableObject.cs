// SPDX-License-Identifier: AGPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace RECompose.Presentation.Mvvm;

/// <summary>Base class for view models: INotifyPropertyChanged with a Set helper.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>An ICommand around a delegate. Exceptions from async commands go to <see cref="Failed"/>.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => { execute(); return Task.CompletedTask; }, canExecute)
    {
    }

    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute)
    {
    }

    public RelayCommand(Func<object?, Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>Called with any exception an async command throws.</summary>
    public static Action<Exception>? Failed { get; set; }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public async void Execute(object? parameter)
    {
        try
        {
            await _execute(parameter);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Runs work on the UI thread (Avalonia's dispatcher in the app, inline in tests).</summary>
public interface IUiDispatcher
{
    void Post(Action action);

    /// <summary>Runs <paramref name="action"/> after a delay on the UI thread.</summary>
    void After(TimeSpan delay, Action action);
}

/// <summary>A dispatcher that runs everything inline, for tests.</summary>
public sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();

    public void After(TimeSpan delay, Action action) => _ = Task.Delay(delay).ContinueWith(_ => action(), TaskScheduler.Default);
}
