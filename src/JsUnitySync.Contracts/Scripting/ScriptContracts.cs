namespace JsUnitySync.Contracts.Scripting;

public sealed class ScriptDocumentSummary
{
    public string ProjectId { get; set; } = "";
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
    public long Revision { get; set; }
    public string UpdatedById { get; set; } = "";
    public long UpdatedUnixSeconds { get; set; }
}

public sealed class SaveScriptDocumentRequest
{
    public string Content { get; set; } = "";
    /// <summary>Revision loaded by the editor. Use zero only to create a document.</summary>
    public long ExpectedRevision { get; set; }
}
