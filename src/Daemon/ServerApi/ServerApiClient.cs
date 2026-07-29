using System.Net;

namespace AngstromCommander.Daemon.ServerApi;

/// <summary>The Daemon's HTTP surface toward the Server: enrollment and connection-token auth.</summary>
internal sealed class ServerApiClient(HttpClient http)
{
    public async Task<PairingCodeResult> RequestPairingCodeAsync(string publicKeySpki, string platform, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(
            "/api/enrollment/code", new { publicKeySpki, platform }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PairingCodeResult>(cancellationToken))!;
    }

    /// <summary>Null while the code is still unclaimed; throws when the code expired.</summary>
    public async Task<Guid?> GetEnrollmentStatusAsync(string code, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
            new Uri($"/api/enrollment/status/{code}", UriKind.Relative), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EnrollmentStatusResult>(cancellationToken))!.RegistrationId;
    }

    public async Task<string> GetChallengeNonceAsync(Guid registrationId, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(
            "/api/daemon-auth/challenge", new { registrationId }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChallengeResult>(cancellationToken))!.Nonce;
    }

    public async Task<string> GetConnectionTokenAsync(Guid registrationId, string signature, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(
            "/api/daemon-auth/token", new { registrationId, signature }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResult>(cancellationToken))!.AccessToken;
    }
}

internal sealed record PairingCodeResult(string Code, DateTimeOffset ExpiresAt);

internal sealed record EnrollmentStatusResult(Guid RegistrationId);

internal sealed record ChallengeResult(string Nonce);

internal sealed record TokenResult(string AccessToken);
