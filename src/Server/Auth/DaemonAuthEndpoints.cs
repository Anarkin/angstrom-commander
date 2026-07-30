using System.Security.Cryptography;
using AngstromCommander.Server.Data;
using AngstromCommander.Server.Enrollment;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AngstromCommander.Server.Auth;

internal static class DaemonAuthEndpoints
{
    private static readonly TimeSpan NonceLifetime = TimeSpan.FromMinutes(2);

    public static IEndpointRouteBuilder MapDaemonAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/api/daemon-auth/challenge",
            static async Task<Results<Ok<ChallengeResponse>, NotFound>> (
                ChallengeRequest request,
                AppDbContext db,
                IMemoryCache cache,
                CancellationToken cancellationToken) =>
            {
                var registration = await db.DaemonRegistrations.AsNoTracking().FirstOrDefaultAsync(
                    r => r.Id == request.RegistrationId && r.RevokedAt == null, cancellationToken);
                if (registration is null)
                {
                    return TypedResults.NotFound();
                }

                var nonce = RandomNumberGenerator.GetBytes(32);
                cache.Set(NonceCacheKey(request.RegistrationId), nonce, NonceLifetime);
                return TypedResults.Ok(new ChallengeResponse(Convert.ToBase64String(nonce)));
            });

        app.MapPost(
            "/api/daemon-auth/token",
            static async Task<Results<Ok<DaemonTokenResponse>, UnauthorizedHttpResult>> (
                TokenRequest request,
                AppDbContext db,
                IMemoryCache cache,
                TokenService tokens,
                CancellationToken cancellationToken) =>
            {
                if (!cache.TryGetValue<byte[]>(NonceCacheKey(request.RegistrationId), out var nonce) || nonce is null)
                {
                    return TypedResults.Unauthorized();
                }

                cache.Remove(NonceCacheKey(request.RegistrationId));

                var registration = await db.DaemonRegistrations.AsNoTracking().FirstOrDefaultAsync(
                    r => r.Id == request.RegistrationId && r.RevokedAt == null, cancellationToken);
                if (registration is null || !PublicKeys.TryImportSpki(registration.PublicKeySpki, out var key))
                {
                    return TypedResults.Unauthorized();
                }

                using (key)
                {
                    byte[] signature;
                    try
                    {
                        signature = Convert.FromBase64String(request.Signature);
                    }
                    catch (FormatException)
                    {
                        return TypedResults.Unauthorized();
                    }

                    if (!key.VerifyData(nonce, signature, HashAlgorithmName.SHA256))
                    {
                        return TypedResults.Unauthorized();
                    }
                }

                return TypedResults.Ok(new DaemonTokenResponse(tokens.CreateDaemonToken(request.RegistrationId)));
            });

        return app;
    }

    private static string NonceCacheKey(Guid registrationId)
    {
        return $"daemon-nonce:{registrationId}";
    }
}

internal sealed record ChallengeRequest(Guid RegistrationId);

internal sealed record ChallengeResponse(string Nonce);

internal sealed record TokenRequest(Guid RegistrationId, string Signature);

internal sealed record DaemonTokenResponse(string AccessToken);
