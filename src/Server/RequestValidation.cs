using System.ComponentModel.DataAnnotations;

namespace AngstromCommander.Server;

/// <summary>
/// Checks a request body against the DataAnnotations on its record before the handler runs, so an
/// oversized or missing field is a 400 naming the field rather than a 500 out of the database
/// driver when the value reaches its column.
/// </summary>
/// <remarks>
/// Deliberately explicit per endpoint rather than the framework's <c>AddValidation()</c>: that one
/// depends on a source generator whose absence compiles and runs perfectly while validating
/// nothing, and a silent no-op is the worst possible shape for a guard.
/// </remarks>
internal static class RequestValidation
{
    public static RouteHandlerBuilder ValidatesRequest<TRequest>(this RouteHandlerBuilder builder)
        where TRequest : notnull
    {
        return builder
            .AddEndpointFilter(static async (context, next) =>
            {
                var request = context.Arguments.OfType<TRequest>().FirstOrDefault();
                if (request is null)
                {
                    return await next(context);
                }

                var failures = new List<ValidationResult>();
                if (Validator.TryValidateObject(
                    request, new ValidationContext(request), failures, validateAllProperties: true))
                {
                    return await next(context);
                }

                return TypedResults.ValidationProblem(Describe(failures));
            })
            .ProducesValidationProblem();
    }

    private static Dictionary<string, string[]> Describe(List<ValidationResult> failures)
    {
        return failures
            .SelectMany(
                static failure => failure.MemberNames.DefaultIfEmpty("request"),
                static (failure, member) => (Member: member, Message: failure.ErrorMessage ?? "Invalid value."))
            .GroupBy(static entry => entry.Member, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static entry => entry.Message).ToArray(),
                StringComparer.Ordinal);
    }
}
