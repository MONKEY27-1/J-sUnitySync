using System.Windows.Input;

namespace JsUnitySync.Desktop.Core.Mvvm;

public sealed class AsyncParameterCommand<T>(Func<T, Task> execute, Action<Exception> onError) : ICommand
{
    private int _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => Volatile.Read(ref _running) == 0 && parameter is T;
    public async void Execute(object? parameter)
    {
        if (parameter is not T value || Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(value); }
        catch (Exception error) { onError(error); }
        finally { Interlocked.Exchange(ref _running, 0); CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
