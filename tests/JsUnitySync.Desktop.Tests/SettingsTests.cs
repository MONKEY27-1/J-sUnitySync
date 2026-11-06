using System.Text.Json;
using JsUnitySync.Desktop.Core.Settings;

namespace JsUnitySync.Desktop.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"JsUnitySync-settings-tests-{Guid.NewGuid():N}");
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Theory]
    [InlineData("https://sync.example.com", "https://sync.example.com/")]
    [InlineData("http://localhost:5080", "http://localhost:5080/")]
    [InlineData("http://127.0.0.1:5080/", "http://127.0.0.1:5080/")]
    [InlineData("http://[::1]:5080/", "http://[::1]:5080/")]
    public void Endpoint_ValidOriginsAreNormalized(string input, string expected)
    {
        Assert.True(ServerEndpoint.TryParse(input, out var uri));
        Assert.Equal(expected, uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uri")]
    [InlineData("file:///C:/project")]
    [InlineData("http://remote.example.com")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com?token=secret")]
    [InlineData("https://example.com#fragment")]
    [InlineData("https://example.com/api")]
    public void Endpoint_UnsafeOrAmbiguousAddressesAreRejected(string input) =>
        Assert.False(ServerEndpoint.TryParse(input, out _));

    [Fact]
    public void Store_MissingFileUsesDefaultsWithoutWriting()
    {
        Assert.Equal(new DesktopSettings(), new JsonSettingsStore(SettingsPath).Load());
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void Store_SaveReloadAndReplacementLeaveNoTemporaryFiles()
    {
        var store = new JsonSettingsStore(SettingsPath);
        store.Save(new("https://one.example.com/", true));
        store.Save(new("https://two.example.com/", false));
        Assert.Equal(new("https://two.example.com/", false), store.Load());
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("{\"ServerAddress\":\"file:///C:/unsafe\"}")]
    public void Store_CorruptFileThrowsAndPreservesOriginal(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, content);
        Assert.ThrowsAny<Exception>(() => new JsonSettingsStore(SettingsPath).Load());
        Assert.Equal(content, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Store_InvalidSavePreservesExistingSettings()
    {
        var store = new JsonSettingsStore(SettingsPath);
        store.Save(new());
        Assert.Throws<InvalidDataException>(() => store.Save(new("http://remote.example.com/")));
        Assert.Equal(new DesktopSettings(), store.Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
