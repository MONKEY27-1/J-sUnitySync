using JsUnitySync.Domain.Ids;

namespace JsUnitySync.Server.Persistence;

public sealed class UserAccount
{
    public string Id { get; set; } = StableId.NewId().ToString();
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public long Revision { get; set; } = 1;
}

public sealed class UserSession
{
    public string Id { get; set; } = StableId.NewId().ToString();
    public string UserId { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public long ExpiresUnixSeconds { get; set; }
    public UserAccount User { get; set; } = null!;
}
