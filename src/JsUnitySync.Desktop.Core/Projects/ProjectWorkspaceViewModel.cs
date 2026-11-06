using System.Collections.ObjectModel;
using JsUnitySync.Contracts.Projects;
using JsUnitySync.Contracts.Scripting;
using JsUnitySync.Desktop.Core.Accounts;
using JsUnitySync.Desktop.Core.Mvvm;
using JsUnitySync.Desktop.Core.Networking;
using JsUnitySync.Desktop.Core.Settings;
using JsUnitySync.Desktop.Core.Teams;

namespace JsUnitySync.Desktop.Core.Projects;

public sealed class ProjectWorkspaceViewModel : OperationViewModel, IDisposable
{
    private readonly CollaborationClient _client;
    private readonly AccountViewModel _account;
    private readonly TeamWorkspaceViewModel _teams;
    private readonly IProjectLinkStore _links;
    private readonly CancellationTokenSource _lifetime = new();
    private ProjectSummary? _selectedProject;
    private string _projectName = "";
    private bool _showArchived;
    private IReadOnlyList<LocalProjectLink> _localLinks = [];
    private bool _disposed;
    private string _scriptPath = "Assets/Scripts/NewScript.cs";
    private string _scriptContent = "";
    private string _scriptBaseline = "";
    private string? _scriptProjectId;
    private long _scriptRevision;
    private string _scriptMessage = "Scripts are stored as shared server documents; local Unity files are not changed.";
    private ProjectFileVersionSummary? _selectedFileVersion;

    public ProjectWorkspaceViewModel(CollaborationClient client, AccountViewModel account, TeamWorkspaceViewModel teams, IProjectLinkStore links)
    {
        _client = client; _account = account; _teams = teams; _links = links;
        Projects = []; _teams.PropertyChanged += TeamChanged; _account.SessionChanged += SessionChanged;
        RefreshCommand = new AsyncCommand(_ => RunAsync(RefreshAsync), Fail);
        CreateCommand = new AsyncCommand(_ => RunAsync(CreateAsync), Fail);
        RenameCommand = new AsyncCommand(_ => RunAsync(RenameAsync), Fail);
        ArchiveCommand = new AsyncCommand(_ => RunAsync(ArchiveAsync), Fail);
        RestoreCommand = new AsyncCommand(_ => RunAsync(RestoreAsync), Fail);
        UnlinkCommand = new AsyncCommand(_ => RunAsync(UnlinkAsync), Fail);
        LoadScriptCommand = new AsyncCommand(_ => RunAsync(LoadScriptAsync), Fail);
        SaveScriptCommand = new AsyncCommand(_ => RunAsync(SaveScriptAsync), Fail);
        RefreshHistoryCommand = new AsyncCommand(_ => RunAsync(RefreshHistoryAsync), Fail);
        RestoreFileVersionCommand = new AsyncCommand(_ => RunAsync(RestoreFileVersionAsync), Fail);
        FileHistory = [];
    }

    public ObservableCollection<ProjectSummary> Projects { get; }
    public ProjectSummary? SelectedProject { get => _selectedProject; set { if (Set(ref _selectedProject, value)) { ProjectName = value?.Name ?? ""; OnPropertyChanged(nameof(SelectedLocalPath)); OnPropertyChanged(nameof(IsProjectSelected)); OnPropertyChanged(nameof(IsProjectArchived)); OnPropertyChanged(nameof(IsProjectActive)); if (RefreshHistoryCommand is not null) RefreshHistoryCommand.Execute(null); } } }
    public string ProjectName { get => _projectName; set => Set(ref _projectName, value); }
    public bool ShowArchived { get => _showArchived; set { if (Set(ref _showArchived, value)) RefreshCommand.Execute(null); } }
    public bool IsProjectSelected => SelectedProject is not null;
    public bool IsProjectArchived => SelectedProject?.IsArchived == true;
    public bool IsProjectActive => SelectedProject is { IsArchived: false };
    public bool IsSignedIn => _account.IsSignedIn;
    public bool IsSignedOut => !_account.IsSignedIn;
    public bool CanManageTeam => _account.IsSignedIn && _teams.SelectedTeam?.Role >= 1;
    public string ScriptPath { get => _scriptPath; set => Set(ref _scriptPath, value); }
    public string ScriptContent { get => _scriptContent; set => Set(ref _scriptContent, value); }
    public long ScriptRevision { get => _scriptRevision; private set => Set(ref _scriptRevision, value); }
    public string ScriptMessage { get => _scriptMessage; private set => Set(ref _scriptMessage, value); }
    public ObservableCollection<ProjectFileVersionSummary> FileHistory { get; }
    public ProjectFileVersionSummary? SelectedFileVersion { get => _selectedFileVersion; set => Set(ref _selectedFileVersion, value); }
    public string? SelectedLocalPath => _localLinks.FirstOrDefault(x => x.ProjectId == SelectedProject?.Id)?.LocalPath;
    public TeamWorkspaceViewModel Teams => _teams;
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand CreateCommand { get; }
    public AsyncCommand RenameCommand { get; }
    public AsyncCommand ArchiveCommand { get; }
    public AsyncCommand RestoreCommand { get; }
    public AsyncCommand UnlinkCommand { get; }
    public AsyncCommand LoadScriptCommand { get; }
    public AsyncCommand SaveScriptCommand { get; }
    public AsyncCommand RefreshHistoryCommand { get; }
    public AsyncCommand RestoreFileVersionCommand { get; }

    public async Task RefreshAsync()
    {
        if (!_account.IsSignedIn) { Projects.Clear(); _localLinks = []; SelectedProject = null; Message = "Sign in and select a team to manage projects."; return; }
        var team = _teams.SelectedTeam;
        if (team is null) { Projects.Clear(); _localLinks = []; SelectedProject = null; Message = "Create or join a team before registering shared projects."; return; }
        var previous = SelectedProject?.Id;
        var projects = await _client.SendAsync<ProjectSummary[]>(HttpMethod.Get,
            $"api/v1/teams/{team.Id}/projects?includeArchived={ShowArchived.ToString().ToLowerInvariant()}", null, _lifetime.Token);
        Replace(Projects, projects);
        SelectedProject = Projects.FirstOrDefault(x => x.Id == previous) ?? Projects.FirstOrDefault();
        _localLinks = _links.Load(_client.ServerAddress, _account.Profile!.Id);
        OnPropertyChanged(nameof(SelectedLocalPath));
        Message = "Project metadata refreshed from the selected team. Local paths remain on this device.";
    }

    public async Task LinkFolderAsync(string path)
    {
        await RunAsync(async () =>
        {
            if (SelectedProject is null || _account.Profile is null || _teams.SelectedTeam is null)
            { Message = "Select a shared project and team before linking a local Unity folder."; return; }
            if (!UnityProjectFolderValidator.TryValidate(path, out var folder, out var validationError))
            { Message = validationError; return; }
            var link = new LocalProjectLink(_client.ServerAddress, _account.Profile.Id, _teams.SelectedTeam.Id, SelectedProject.Id, SelectedProject.Name, folder.FullPath);
            try { _links.Add(link); }
            catch (InvalidOperationException error) { Message = error.Message; return; }
            catch (IOException) { Message = "Local link could not be saved. The selected Unity project was not modified."; return; }
            _localLinks = _links.Load(_client.ServerAddress, _account.Profile.Id);
            OnPropertyChanged(nameof(SelectedLocalPath));
            Message = $"Linked {folder.FolderName}. Only its standard Unity folder structure was checked; project files were not opened or modified.";
        });
    }

    private async Task CreateAsync()
    {
        var team = _teams.SelectedTeam ?? throw new ApiClientException(400, "Select a team first.");
        var created = await _client.SendAsync<ProjectSummary>(HttpMethod.Post, $"api/v1/teams/{team.Id}/projects", new CreateProjectRequest { Name = ProjectName }, _lifetime.Token);
        ProjectName = ""; Projects.Add(created); SelectedProject = created; Message = "Shared project registered for this team. No Unity files were uploaded or created.";
    }
    private async Task RenameAsync()
    {
        if (SelectedProject is null) return;
        var old = SelectedProject;
        var updated = await _client.SendAsync<ProjectSummary>(HttpMethod.Put, $"api/v1/teams/{old.TeamId}/projects/{old.Id}", new UpdateProjectRequest { Name = ProjectName, ExpectedRevision = old.Revision }, _lifetime.Token);
        ReplaceItem(old, updated); SelectedProject = updated; Message = "Project name saved. Local project files were untouched.";
    }
    private async Task ArchiveAsync()
    {
        if (SelectedProject is null) return;
        var project = SelectedProject;
        await _client.SendAsync<object>(HttpMethod.Post, $"api/v1/teams/{project.TeamId}/projects/{project.Id}/archive", new ArchiveProjectRequest { ExpectedRevision = project.Revision }, _lifetime.Token);
        Message = "Project archived on the server; no local files were deleted."; await RefreshAsync();
    }
    private async Task RestoreAsync()
    {
        if (SelectedProject is null) return;
        var project = SelectedProject;
        await _client.SendAsync<object>(HttpMethod.Post, $"api/v1/teams/{project.TeamId}/projects/{project.Id}/restore", new ArchiveProjectRequest { ExpectedRevision = project.Revision }, _lifetime.Token);
        Message = "Project metadata restored on the server; local files are unchanged."; await RefreshAsync();
    }
    private async Task LoadScriptAsync()
    {
        if (SelectedProject is null) throw new ApiClientException(400, "Select a project first.");
        if (_scriptProjectId is not null && ScriptContent != _scriptBaseline)
            throw new ApiClientException(409, "Unsaved script edits are still in the editor. Save or copy them before loading another version.");
        var path = ScriptPath.Trim();
        var route = $"api/v1/projects/{SelectedProject.Id}/scripts/document?path={Uri.EscapeDataString(path)}";
        try
        {
            var document = await _client.SendAsync<ScriptDocumentSummary>(HttpMethod.Get, route, null, _lifetime.Token);
            ScriptPath = document.Path;
            ScriptContent = document.Content;
            _scriptBaseline = document.Content;
            _scriptProjectId = SelectedProject.Id;
            ScriptRevision = document.Revision;
            ScriptMessage = $"Loaded shared revision {document.Revision}. Refresh to see another collaborator's accepted changes.";
        }
        catch (ApiClientException error) when (error.Status == 404)
        {
            _scriptProjectId = SelectedProject.Id;
            ScriptContent = "";
            _scriptBaseline = "";
            ScriptRevision = 0;
            ScriptMessage = "No shared document exists at this path yet. Add script text and save to create it.";
        }
    }

    private async Task SaveScriptAsync()
    {
        if (SelectedProject is null || _scriptProjectId != SelectedProject.Id)
            throw new ApiClientException(409, "Load or create a document for the selected project before saving.");
        var path = ScriptPath.Trim();
        var saved = await _client.SendAsync<ScriptDocumentSummary>(HttpMethod.Put,
            $"api/v1/projects/{SelectedProject.Id}/scripts/document?path={Uri.EscapeDataString(path)}",
            new SaveScriptDocumentRequest { Content = ScriptContent, ExpectedRevision = ScriptRevision }, _lifetime.Token);
        ScriptPath = saved.Path;
        ScriptContent = saved.Content;
        _scriptBaseline = saved.Content;
        ScriptRevision = saved.Revision;
        ScriptMessage = $"Saved shared revision {saved.Revision}. Use Refresh before retrying if another collaborator may have saved first.";
    }

    private async Task RefreshHistoryAsync()
    {
        if (!_account.IsSignedIn || SelectedProject is null) { FileHistory.Clear(); SelectedFileVersion = null; return; }
        var values = await _client.SendAsync<ProjectFileVersionSummary[]>(HttpMethod.Get, $"api/v1/projects/{SelectedProject.Id}/history", null, _lifetime.Token);
        FileHistory.Clear();
        foreach (var value in values) FileHistory.Add(value);
        SelectedFileVersion = FileHistory.FirstOrDefault();
    }

    private async Task RestoreFileVersionAsync()
    {
        var project = SelectedProject ?? throw new ApiClientException(400, "Select a project first.");
        var version = SelectedFileVersion ?? throw new ApiClientException(400, "Select a historical version first.");
        if (version.Kind == "script" && _scriptProjectId == project.Id && ScriptPath == version.Path && ScriptContent != _scriptBaseline)
            throw new ApiClientException(409, "Save or copy the unsaved editor text before restoring this script version.");
        long currentRevision;
        if (version.Kind == "script")
        {
            var documents = await _client.SendAsync<ScriptDocumentSummary[]>(HttpMethod.Get, $"api/v1/projects/{project.Id}/scripts", null, _lifetime.Token);
            currentRevision = documents.Single(x => x.Path == version.Path).Revision;
        }
        else
        {
            var assets = await _client.SendAsync<ProjectAssetSummary[]>(HttpMethod.Get, $"api/v1/projects/{project.Id}/assets", null, _lifetime.Token);
            currentRevision = assets.Single(x => x.Path == version.Path).Revision;
        }
        await _client.SendAsync<ProjectFileVersionSummary>(HttpMethod.Post, $"api/v1/projects/{project.Id}/history/restore",
            new RestoreProjectFileVersionRequest { Kind = version.Kind, Path = version.Path, SourceRevision = version.Revision, ExpectedCurrentRevision = currentRevision }, _lifetime.Token);
        await RefreshHistoryAsync();
        if (version.Kind == "script" && _scriptProjectId == project.Id && ScriptPath == version.Path)
        {
            var current = await _client.SendAsync<ScriptDocumentSummary>(HttpMethod.Get, $"api/v1/projects/{project.Id}/scripts/document?path={Uri.EscapeDataString(version.Path)}", null, _lifetime.Token);
            ScriptContent = current.Content; _scriptBaseline = current.Content; ScriptRevision = current.Revision;
        }
    }

    private async Task UnlinkAsync()
    {
        if (SelectedProject is null || _account.Profile is null) return;
        var id = SelectedProject.Id;
        await Task.Run(() => _links.Remove(_client.ServerAddress, _account.Profile.Id, id), _lifetime.Token);
        _localLinks = _links.Load(_client.ServerAddress, _account.Profile.Id);
        OnPropertyChanged(nameof(SelectedLocalPath)); Message = "Device-local folder link removed. Server project and folder were not deleted.";
    }
    private void ReplaceItem(ProjectSummary old, ProjectSummary value)
    { var index = Projects.IndexOf(old); if (index >= 0) Projects[index] = value; }
    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values)
    { collection.Clear(); foreach (var item in values) collection.Add(item); }
    private void TeamChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(TeamWorkspaceViewModel.SelectedTeam)) { OnPropertyChanged(nameof(CanManageTeam)); RefreshCommand.Execute(null); } }
    private void SessionChanged(object? sender, EventArgs e) { OnPropertyChanged(nameof(IsSignedIn)); OnPropertyChanged(nameof(IsSignedOut)); OnPropertyChanged(nameof(CanManageTeam)); RefreshCommand.Execute(null); }
    private void Fail(Exception error) => Message = error is ApiClientException api ? api.Message : "Project operation failed. Refresh before retrying.";
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _teams.PropertyChanged -= TeamChanged; _account.SessionChanged -= SessionChanged; _lifetime.Cancel(); _lifetime.Dispose();
        RefreshCommand.Dispose(); CreateCommand.Dispose(); RenameCommand.Dispose(); ArchiveCommand.Dispose(); RestoreCommand.Dispose(); UnlinkCommand.Dispose(); LoadScriptCommand.Dispose(); SaveScriptCommand.Dispose(); RefreshHistoryCommand.Dispose(); RestoreFileVersionCommand.Dispose();
    }
}
