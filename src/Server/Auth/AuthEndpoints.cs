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
            });

        app.MapPost(
            "/api/auth/login",
            static async Task<Results<Ok<LoginResponse>, UnauthorizedHttpResult>> (
                LoginRequest request,
                UserManager<AppUser> users,
                TokenService tokens) =>
            {
                var user = await users.FindByEmailAsync(request.Email);
                if (user is null || !await users.CheckPasswordAsync(user, request.Password))
                {
                    return TypedResults.Unauthorized();
                }

                return TypedResults.Ok(new LoginResponse(tokens.CreateUserToken(user)));
            });

        return app;
    }
}

internal sealed record RegisterRequest(string Email, string Password);

internal sealed record RegisterResponse(Guid UserId);

internal sealed record LoginRequest(string Email, string Password);

internal sealed record LoginResponse(string AccessToken);
