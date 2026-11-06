using System.Windows.Input;

namespace JsUnitySync.Desktop.Core.Mvvm;

/// <summary>Single-flight, cancellable UI command. Async-void boundary always observes errors.</summary>
public sealed class AsyncCommand(Func<CancellationToken, Task> execute, Action<Exception> onError) : ObservableObject, ICommand, IDisposable
{
    private CancellationTokenSource? _cancellation;
    private bool _isRunning;
    private bool _disposed;
    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_disposed && !IsRunning;

    public async void Execute(object? parameter)
    {
        try { await ExecuteAsync(); }
        catch (Exception error) { onError(error); }
    }

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsRunning = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            _cancellation = null;
            IsRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Cancel() => _cancellation?.Cancel();
    public void Dispose()
    {
        _disposed = true;
        Cancel();
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
