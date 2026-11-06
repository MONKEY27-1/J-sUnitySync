using JsUnitySync.Desktop.Core.Mvvm;

namespace JsUnitySync.Desktop.Tests;

public sealed class CommandTests
{
    [Fact]
    public void Relay_DisabledCommandDoesNotExecute()
    {
        var calls = 0;
        var command = new RelayCommand(() => calls++, () => false);
        command.Execute(null);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AsyncCommand_IsSingleFlightAndRestoresAvailability()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var command = new AsyncCommand(async _ => { calls++; await gate.Task; }, _ => { });
        var notifications = 0;
        command.CanExecuteChanged += (_, _) => notifications++;
        var first = command.ExecuteAsync();
        Assert.True(command.IsRunning);
        Assert.False(command.CanExecute(null));
        await command.ExecuteAsync();
        Assert.Equal(1, calls);
        gate.SetResult();
        await first;
        Assert.True(command.CanExecute(null));
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void AsyncCommand_ICommandBoundaryObservesExceptions()
    {
        Exception? captured = null;
        using var command = new AsyncCommand(_ => throw new IOException("test"), error => captured = error);
        command.Execute(null);
        Assert.IsType<IOException>(captured);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task AsyncCommand_DisposeCancelsAndPermanentlyDisables()
    {
        var cancelled = false;
        var command = new AsyncCommand(async token =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        }, _ => { });
        var run = command.ExecuteAsync();
        command.Dispose();
        await run;
        Assert.True(cancelled);
        Assert.False(command.CanExecute(null));
    }
}
