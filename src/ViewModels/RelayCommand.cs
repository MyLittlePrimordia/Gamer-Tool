using System;
using System.Windows.Input;

namespace GamerTool.ViewModels;

/// <summary>Parameterless ICommand - wraps CommandManager.RequerySuggested so WPF re-evaluates CanExecute on the usual input/focus events without any manual invalidation plumbing.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}

/// <summary>Generic ICommand carrying a strongly-typed CommandParameter (e.g. the DisplayPreset a "Select" button in an ItemsControl was clicked for).</summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(CoerceParameter(parameter)) ?? true;

    public void Execute(object? parameter) => _execute(CoerceParameter(parameter));

    private static T? CoerceParameter(object? parameter) => parameter is T typed ? typed : default;

    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}
