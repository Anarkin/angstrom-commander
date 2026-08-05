using System.ComponentModel.DataAnnotations;
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

                var nonce = new DaemonNonce(RandomNumberGenerator.GetBytes(32));
                cache.Set(NonceCacheKey(request.RegistrationId), nonce, NonceLifetime);
                return TypedResults.Ok(new ChallengeResponse(Convert.ToBase64String(nonce.Value)));
            })
            .RequireRateLimiting(RateLimitPolicies.Authentication);

        app.MapPost(
            "/api/daemon-auth/token",
            static async Task<Results<Ok<DaemonTokenResponse>, UnauthorizedHttpResult>> (
                TokenRequest request,
                AppDbContext db,
                IMemoryCache cache,
                TokenService tokens,
                CancellationToken cancellationToken) =>
            {
                // Claimed before anything is verified, and claimed atomically: reading the nonce
                // and then removing it is two steps, so two requests could both pass between
                // them and replay the same signature — which is the one thing single use is for.
                if (!cache.TryGetValue<DaemonNonce>(NonceCacheKey(request.RegistrationId), out var nonce)
                    || nonce is null
                    || !nonce.TryConsume())
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

                    if (!key.VerifyData(nonce.Value, signature, HashAlgorithmName.SHA256))
                    {
                        return TypedResults.Unauthorized();
                    }
                }

                return TypedResults.Ok(new DaemonTokenResponse(tokens.CreateDaemonToken(request.RegistrationId)));
            })
            .ValidatesRequest<TokenRequest>()
            .RequireRateLimiting(RateLimitPolicies.Authentication);

        return app;
    }

    private static string NonceCacheKey(Guid registrationId)
    {
        return $"daemon-nonce:{registrationId}";
    }

    /// <summary>A challenge nonce and the flag that lets exactly one caller spend it.</summary>
    private sealed class DaemonNonce(byte[] value)
    {
        private int _consumed;

        public byte[] Value { get; } = value;

        /// <summary>True for the first caller to reach it, false for every other, always.</summary>
        public bool TryConsume()
        {
            return Interlocked.Exchange(ref this._consumed, 1) == 0;
        }
    }
}

internal sealed record ChallengeRequest(Guid RegistrationId);

internal sealed record ChallengeResponse(string Nonce);

internal sealed record TokenRequest(
    Guid RegistrationId,
    [property: Required][property: StringLength(512)] string Signature);

internal sealed record DaemonTokenResponse(string AccessToken);
