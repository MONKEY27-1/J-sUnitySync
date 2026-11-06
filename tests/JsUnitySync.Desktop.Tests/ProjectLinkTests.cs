using JsUnitySync.Desktop.Core.Projects;

namespace JsUnitySync.Desktop.Tests;

public sealed class ProjectLinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"jsunitysync-project-link-{Guid.NewGuid():N}");
    private string ProjectFolder => Path.Combine(_root, "My Unity Game");
    private string LinkStore => Path.Combine(_root, "local", "links.json");

    private void CreateProject(string? version = "m_EditorVersion: 6000.0.1f1\n")
    {
        Directory.CreateDirectory(Path.Combine(ProjectFolder, "Assets"));
        Directory.CreateDirectory(Path.Combine(ProjectFolder, "ProjectSettings"));
        File.WriteAllText(Path.Combine(ProjectFolder, "ProjectSettings", "ProjectVersion.txt"), version);
    }
    private static string Id() => "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private LocalProjectLink Link(string path, string? user = null, string? server = null, string? project = null) =>
        new(server ?? "https://sync.example.com/", user ?? Id(), Id(), project ?? Id(), "Arena", path);

    [Fact]
    public void Validator_AcceptsStandardUnityStructureWithoutChangingContents()
    {
        CreateProject();
        var versionPath = Path.Combine(ProjectFolder, "ProjectSettings", "ProjectVersion.txt");
        var before = File.ReadAllBytes(versionPath);
        Assert.True(UnityProjectFolderValidator.TryValidate(ProjectFolder, out var result, out var error), error);
        Assert.Equal(Path.GetFullPath(ProjectFolder), result.FullPath);
        Assert.Equal("My Unity Game", result.FolderName);
        Assert.Equal(before, File.ReadAllBytes(versionPath));
        Assert.DoesNotContain(Directory.GetFiles(ProjectFolder, "*", SearchOption.AllDirectories), x => !x.EndsWith("ProjectVersion.txt", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Validator_RejectsEmptySelection(string value) =>
        Assert.False(UnityProjectFolderValidator.TryValidate(value, out _, out _));

    [Fact]
    public void Validator_RejectsNonUnityAndOversizedManifest()
    {
        Directory.CreateDirectory(ProjectFolder);
        Assert.False(UnityProjectFolderValidator.TryValidate(ProjectFolder, out _, out _));
        CreateProject(new string('x', 64 * 1024 + 1));
        Assert.False(UnityProjectFolderValidator.TryValidate(ProjectFolder, out _, out _));
    }

    [Fact]
    public void Store_PersistsEndpointAccountScopedLocalPathAndUnlinksWithoutDeletingProject()
    {
        CreateProject();
        var store = new JsonProjectLinkStore(LinkStore);
        var link = Link(ProjectFolder);
        store.Add(link);
        Assert.Equal(Path.GetFullPath(ProjectFolder), Assert.Single(store.Load(link.ServerAddress, link.UserId)).LocalPath);
        Assert.Empty(store.Load("https://other.example.com/", link.UserId));
        Assert.Empty(store.Load(link.ServerAddress, "01ARZ3NDEKTSV4RRFFQ69G5FAW"));
        Assert.Throws<InvalidOperationException>(() => store.Add(link));
        store.Remove(link.ServerAddress, link.UserId, link.ProjectId);
        Assert.Empty(store.Load(link.ServerAddress, link.UserId));
        Assert.True(File.Exists(Path.Combine(ProjectFolder, "ProjectSettings", "ProjectVersion.txt")));
    }

    [Fact]
    public void Store_OneFolderCannotBeLinkedToTwoProjectsForSameAccountAndServer()
    {
        CreateProject();
        var store = new JsonProjectLinkStore(LinkStore);
        var link = Link(ProjectFolder);
        store.Add(link);
        Assert.Throws<InvalidOperationException>(() => store.Add(link with { ProjectId = "01ARZ3NDEKTSV4RRFFQ69G5FAX" }));
        Assert.Single(store.Load(link.ServerAddress, link.UserId));
    }

    [Fact]
    public void Store_CorruptJsonIsPreservedRatherThanOverwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LinkStore)!);
        const string corrupted = "not-json";
        File.WriteAllText(LinkStore, corrupted);
        var store = new JsonProjectLinkStore(LinkStore);
        Assert.Throws<System.Text.Json.JsonException>(() => store.Load("https://sync.example.com/", Id()));
        Assert.Equal(corrupted, File.ReadAllText(LinkStore));
    }

    [Fact]
    public void Store_RejectsMalformedIdentifiersAndInvalidPaths()
    {
        CreateProject();
        var store = new JsonProjectLinkStore(LinkStore);
        Assert.Throws<InvalidDataException>(() => store.Add(Link(ProjectFolder, user: "wrong")));
        Assert.Throws<InvalidDataException>(() => store.Add(Link(Path.Combine(_root, "missing"))));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
