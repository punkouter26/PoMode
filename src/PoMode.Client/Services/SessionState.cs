using System.Net.Http.Json;
using PoMode.Shared.Account;

namespace PoMode.Client.Services;

/// <summary>
/// The browser's session, established once before any page talks to the API. A visitor with no
/// session is made a guest on the spot: the app is usable before anyone decides whether to sign in,
/// and every job still lands in a library that belongs to someone.
/// </summary>
public sealed class SessionState(HttpClient http)
{
    private Task<SessionDto>? _ready;

    public SessionDto? Current { get; private set; }

    public event Action? Changed;

    /// <summary>Resolves the session, minting a guest if there is none. Shared by every caller, so
    /// two components asking at once cause one round trip rather than two guests.</summary>
    public Task<SessionDto> EnsureAsync() => _ready ??= ResolveAsync();

    /// <summary>Ends the session and starts a fresh guest one, so the page never sits signed out.</summary>
    public async Task SignOutAsync()
    {
        await http.PostAsync("auth/logout", content: null);
        _ready = ResolveAsync();
        await _ready;
    }

    private async Task<SessionDto> ResolveAsync()
    {
        var session = await http.GetFromJsonAsync<SessionDto>("api/auth/session")
                      ?? new SessionDto(SessionKind.None, null, false);
        if (session.Kind == SessionKind.None)
        {
            var response = await http.PostAsync("auth/guest", content: null);
            response.EnsureSuccessStatusCode();
            session = await response.Content.ReadFromJsonAsync<SessionDto>() ?? session;
        }
        Current = session;
        Changed?.Invoke();
        return session;
    }
}
