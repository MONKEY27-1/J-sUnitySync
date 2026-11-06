using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace JsUnitySync.UnityPackage.Editor
{
    internal sealed class UnityApiClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly Uri _origin;
        private string _token = string.Empty;

        public string UserId { get; private set; } = string.Empty;
        public string DisplayName { get; private set; } = string.Empty;
        public string ServerAddress { get { return _origin.AbsoluteUri; } }
        public string AccessToken { get { return _token; } }

        public UnityApiClient(string address)
        {
            if (!Uri.TryCreate(address == null ? string.Empty : address.Trim(), UriKind.Absolute, out var origin) ||
                (origin.Scheme != Uri.UriSchemeHttps && !(origin.Scheme == Uri.UriSchemeHttp && origin.IsLoopback)) ||
                origin.AbsolutePath != "/" || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
                throw new ArgumentException("Use an HTTPS server origin; HTTP is permitted only for localhost development.", nameof(address));
            _origin = origin;
            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        }

        public async Task SignInAsync(string email, string password, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password)) throw new InvalidOperationException("Enter your account email and password.");
            var body = JsonUtility.ToJson(new UnityLoginRequest { email = email.Trim(), password = password });
            using (var response = await _http.PostAsync(new Uri(_origin, "api/v1/auth/login"), new StringContent(body, Encoding.UTF8, "application/json"), token))
            {
                if (!response.IsSuccessStatusCode) throw await ReadApiError(response);
                var data = JsonUtility.FromJson<UnityLoginResponse>(await response.Content.ReadAsStringAsync());
                if (data == null || string.IsNullOrEmpty(data.token) || data.profile == null || string.IsNullOrEmpty(data.profile.id))
                    throw new InvalidOperationException("The server returned an invalid account response.");
                _token = data.token;
                UserId = data.profile.id;
                DisplayName = data.profile.displayName;
            }
        }

        public async Task<UnitySessionSummary> GetSessionAsync(string sessionId, CancellationToken token)
        {
            if (string.IsNullOrEmpty(_token)) throw new InvalidOperationException("Sign in before joining a session.");
            using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_origin, "api/v1/sessions/" + Uri.EscapeDataString(sessionId))))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                using (var response = await _http.SendAsync(request, token))
                {
                    if (!response.IsSuccessStatusCode) throw await ReadApiError(response);
                    var session = JsonUtility.FromJson<UnitySessionSummary>(await response.Content.ReadAsStringAsync());
                    if (session == null || session.id != sessionId) throw new InvalidOperationException("The server returned an invalid session response.");
                    return session;
                }
            }
        }

        public void SignOut()
        {
            _token = string.Empty;
            UserId = string.Empty;
            DisplayName = string.Empty;
        }

        private static async Task<Exception> ReadApiError(HttpResponseMessage response)
        {
            var message = "The server rejected the request (HTTP " + (int)response.StatusCode + ").";
            try
            {
                var body = await response.Content.ReadAsStringAsync();
                var error = JsonUtility.FromJson<UnityApiError>(body);
                if (error != null && !string.IsNullOrEmpty(error.message)) message = error.message;
            }
            catch (Exception) { }
            return new InvalidOperationException(message);
        }

        public void Dispose()
        {
            SignOut();
            _http.Dispose();
        }
    }

    [Serializable] internal sealed class UnityApiError { public string code = ""; public string message = ""; }
}
