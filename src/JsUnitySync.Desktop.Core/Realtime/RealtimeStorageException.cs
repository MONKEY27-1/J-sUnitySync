namespace JsUnitySync.Desktop.Core.Realtime;

public sealed class RealtimeStorageException(string message, Exception innerException) : Exception(message, innerException);
