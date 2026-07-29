using AngstromCommander.Server.Data;
using Microsoft.AspNetCore.Identity;

namespace AngstromCommander.Server.Auth;

internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/api/auth/register",
            static async (RegisterRequest request, UserManager<AppUser> users) =>
            {
                var user = new AppUser { UserName = request.Email, Email = request.Email };
                var result = await users.CreateAsync(user, request.Password);
                return result.Succeeded
                    ? Results.Ok(new { userId = user.Id })
                    : Results.BadRequest(new { errors = result.Errors.Select(static e => e.Description) });
            });

        app.MapPost(
            "/api/auth/login",
            static async (LoginRequest request, UserManager<AppUser> users, TokenService tokens) =>
            {
                var user = await users.FindByEmailAsync(request.Email);
                if (user is null || !await users.CheckPasswordAsync(user, request.Password))
                {
                    return Results.Unauthorized();
                }

                return Results.Ok(new { accessToken = tokens.CreateUserToken(user) });
            });

        return app;
    }
}

internal sealed record RegisterRequest(string Email, string Password);

internal sealed record LoginRequest(string Email, string Password);
