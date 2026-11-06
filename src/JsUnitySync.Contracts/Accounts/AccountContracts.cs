using System;

namespace JsUnitySync.Contracts.Accounts
{
    public sealed class RegisterRequest
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }
    public sealed class LoginRequest
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
    }
    public sealed class UserProfile
    {
        public string Id { get; set; } = "";
        public string Email { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public long Revision { get; set; }
    }
    public sealed class ProfileUpdateRequest
    {
        public string DisplayName { get; set; } = "";
        public long ExpectedRevision { get; set; }
    }
    public sealed class AuthResponse
    {
        public string Token { get; set; } = "";
        public DateTimeOffset ExpiresAt { get; set; }
        public UserProfile Profile { get; set; } = new UserProfile();
    }
    public sealed class ApiError
    {
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
    }
}
