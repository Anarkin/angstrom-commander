using AngstromCommander.Server.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;

namespace AngstromCommander.Server.Auth;

internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/api/auth/register",
            static async Task<Results<Ok<RegisterResponse>, ValidationProblem>> (
                RegisterRequest request,
                UserManager<AppUser> users) =>
            {
                var user = new AppUser { UserName = request.Email, Email = request.Email };
                var result = await users.CreateAsync(user, request.Password);
                if (!result.Succeeded)
                {
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["registration"] = result.Errors.Select(static e => e.Description).ToArray(),
                    });
                }

                return TypedResults.Ok(new RegisterResponse(user.Id));
            })
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
                    return WrongCredentials();
                }

                // Through SignInManager rather than UserManager.CheckPasswordAsync, because only
                // this path counts failures and locks the account — without it, password guessing
                // against this endpoint is limited by nothing but bandwidth.
                var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
                if (result.IsLockedOut)
                {
                    return TypedResults.Problem(
                        detail: "Too many failed sign-in attempts. Try again in a few minutes.",
                        statusCode: StatusCodes.Status401Unauthorized);
                }

                if (!result.Succeeded)
                {
                    return WrongCredentials();
                }

                return TypedResults.Ok(new LoginResponse(tokens.CreateUserToken(user)));
            })
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireRateLimiting(RateLimitPolicies.Authentication);

        return app;
    }

    /// <summary>
    /// One answer for "no such account" and "wrong password" alike, so the endpoint does not
    /// double as a way to find out which email addresses have accounts.
    /// </summary>
    private static ProblemHttpResult WrongCredentials()
    {
        return TypedResults.Problem(
            detail: "Incorrect email or password.", statusCode: StatusCodes.Status401Unauthorized);
    }
}

internal sealed record RegisterRequest(string Email, string Password);

internal sealed record RegisterResponse(Guid UserId);

internal sealed record LoginRequest(string Email, string Password);

internal sealed record LoginResponse(string AccessToken);
