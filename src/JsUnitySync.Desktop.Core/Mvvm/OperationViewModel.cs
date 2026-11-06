using JsUnitySync.Desktop.Core.Networking;

namespace JsUnitySync.Desktop.Core.Mvvm;

public abstract class OperationViewModel : ObservableObject
{
    private bool _isBusy;
    private string _message = "";
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !IsBusy;
    public string Message { get => _message; protected set => Set(ref _message, value); }

    protected async Task RunAsync(Func<Task> operation)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await operation(); }
        catch (OperationCanceledException) { Message = "Operation cancelled. Refresh to confirm the latest server state."; }
        catch (ApiClientException error) { Message = error.Message; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { Message = "Local storage could not be accessed. Previous files have not been replaced."; }
        finally { IsBusy = false; }
    }
}
