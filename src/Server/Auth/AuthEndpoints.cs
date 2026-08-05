using System.ComponentModel.DataAnnotations;
using AngstromCommander.Server.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AngstromCommander.Server.Auth;

internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/api/auth/register",
            static async Task<Results<Ok<RegisterResponse>, ValidationProblem, ProblemHttpResult>> (
                RegisterRequest request,
                UserManager<AppUser> users,
                IOptions<RegistrationOptions> registration) =>
            {
                if (!registration.Value.Enabled)
                {
                    return TypedResults.Problem(
                        detail: "Registration is closed on this server.",
                        statusCode: StatusCodes.Status403Forbidden);
                }

                var user = new AppUser { UserName = request.Email, Email = request.Email };
                var result = await users.CreateAsync(user, request.Password);
                if (!result.Succeeded)
                {
                    // Identity's own wording names the address back ("Email 'x' is already
                    // taken"), which turns this endpoint into the account-existence check
                    // /api/auth/login refuses to be. Errors about the submitted password are
                    // safe to repeat — the caller chose it — but a duplicate says something
                    // about somebody else, so it gets a message that fits either case.
                    var duplicate = result.Errors.Any(static e =>
                        e.Code is "DuplicateEmail" or "DuplicateUserName");
                    var messages = duplicate
                        ? [DuplicateOrRefused]
                        : result.Errors.Select(static e => e.Description).ToArray();

                    return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["registration"] = messages,
                    });
                }

                return TypedResults.Ok(new RegisterResponse(user.Id));
            })
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ValidatesRequest<RegisterRequest>()
            .RequireRateLimiting(RateLimitPolicies.Authentication);

        app.MapPost(
            "/api/auth/login",
            static async Task<Results<Ok<LoginResponse>, ProblemHttpResult>> (
                LoginRequest request,
                UserManager<AppUser> users,
                SignInManager<AppUser> signIn,
                TokenService tokens) =>
            {
                var user = await users.FindByEmailAsync(request.Email);
                if (user is null)
                {
                    // Hash anyway. Password hashing is the slow part of this endpoint, so
                    // returning before doing it would make an address with no account answer
                    // measurably faster than a wrong password — enumeration with a stopwatch,
                    // which identical response bodies alone do not prevent.
                    users.PasswordHasher.HashPassword(new AppUser(), request.Password);
                    return WrongCredentials();
                }

                // Through SignInManager rather than UserManager.CheckPasswordAsync, because only
                // this path counts failures and locks the account — without it, password guessing
                // against this endpoint is limited by nothing but bandwidth.
                var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
                if (result.IsLockedOut)
                {
                    // Deliberately the same answer as a wrong password, because a lockout can
                    // only happen to an account that exists: saying so would hand back exactly
                    // the enumeration this endpoint spends the branch above preventing. Five
                    // wrong guesses would otherwise be a membership test for any address.
                    // Hash first — the lockout check short-circuits before Identity verifies
                    // anything, so returning here directly would answer measurably faster than
                    // either other path and re-open the same channel with a stopwatch.
                    users.PasswordHasher.HashPassword(new AppUser(), request.Password);
                    return WrongCredentials();
                }

                if (!result.Succeeded)
                {
                    return WrongCredentials();
                }

                return TypedResults.Ok(new LoginResponse(tokens.CreateUserToken(user)));
            })
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ValidatesRequest<LoginRequest>()
            .RequireRateLimiting(RateLimitPolicies.Authentication);

        return app;
    }

    /// <summary>
    /// The answer to a registration that cannot go ahead for a reason about the address rather
    /// than the password, worded so it does not confirm an account exists.
    /// </summary>
    /// <remarks>
    /// This narrows the leak rather than closing it: registration that succeeds still proves the
    /// address was free. Only a flow that answers identically either way — create-or-email, which
    /// needs the email verification ARCHITECTURE.md § Implementation status already lists as the
    /// prerequisite for opening registration — closes it completely.
    /// </remarks>
    private const string DuplicateOrRefused =
        "That email address cannot be registered. If you already have an account, sign in instead.";

    /// <summary>
    /// One answer for "no such account", "wrong password" and "locked out" alike, so the endpoint
    /// does not double as a way to find out which email addresses have accounts.
    /// </summary>
    private static ProblemHttpResult WrongCredentials()
    {
        return TypedResults.Problem(
            detail: "Incorrect email or password.", statusCode: StatusCodes.Status401Unauthorized);
    }
}

// Lengths match the columns these land in, so an oversized value is a 400 from the endpoint
// rather than a 500 out of the database driver. The password cap is its own point: hashing is
// deliberately slow, and an unbounded password is free work for anyone who asks.
//
// Kept out of the XML summary deliberately: summaries are published verbatim into the OpenAPI
// contract, so a multi-line one embeds whichever line endings the author's editor wrote and the
// generated file stops matching between machines.
/// <summary>Credentials for a new account.</summary>
internal sealed record RegisterRequest(
    [property: Required][property: EmailAddress][property: StringLength(256)] string Email,
    [property: Required][property: StringLength(128, MinimumLength = 1)] string Password);

internal sealed record RegisterResponse(Guid UserId);

internal sealed record LoginRequest(
    [property: Required][property: StringLength(256)] string Email,
    [property: Required][property: StringLength(128)] string Password);

internal sealed record LoginResponse(string AccessToken);
