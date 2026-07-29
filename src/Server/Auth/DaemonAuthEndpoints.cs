using System.Security.Cryptography;
using AngstromCommander.Server.Data;
using AngstromCommander.Server.Enrollment;
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
            static async (ChallengeRequest request, AppDbContext db, IMemoryCache cache, CancellationToken cancellationToken) =>
            {
                var registration = await db.DaemonRegistrations.AsNoTracking().FirstOrDefaultAsync(
                    r => r.Id == request.RegistrationId && r.RevokedAt == null, cancellationToken);
                if (registration is null)
                {
                    return Results.NotFound();
                }

                var nonce = RandomNumberGenerator.GetBytes(32);
                cache.Set(NonceCacheKey(request.RegistrationId), nonce, NonceLifetime);
                return Results.Ok(new { nonce = Convert.ToBase64String(nonce) });
            });

        app.MapPost(
            "/api/daemon-auth/token",
            static async (TokenRequest request, AppDbContext db, IMemoryCache cache, TokenService tokens, CancellationToken cancellationToken) =>
            {
                if (!cache.TryGetValue<byte[]>(NonceCacheKey(request.RegistrationId), out var nonce) || nonce is null)
                {
                    return Results.Unauthorized();
                }

                cache.Remove(NonceCacheKey(request.RegistrationId));

                var registration = await db.DaemonRegistrations.AsNoTracking().FirstOrDefaultAsync(
                    r => r.Id == request.RegistrationId && r.RevokedAt == null, cancellationToken);
                if (registration is null || !PublicKeys.TryImportSpki(registration.PublicKeySpki, out var key))
                {
                    return Results.Unauthorized();
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
                        return Results.Unauthorized();
                    }

                    if (!key.VerifyData(nonce, signature, HashAlgorithmName.SHA256))
                    {
                        return Results.Unauthorized();
                    }
                }

                return Results.Ok(new { accessToken = tokens.CreateDaemonToken(request.RegistrationId) });
            });

        return app;
    }

    private static string NonceCacheKey(Guid registrationId)
    {
        return $"daemon-nonce:{registrationId}";
    }
}

internal sealed record ChallengeRequest(Guid RegistrationId);

internal sealed record TokenRequest(Guid RegistrationId, string Signature);
