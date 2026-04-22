using System.Net;
using System.Text.Json;

namespace Medytao.Api.Middleware;

public static class GlobalExceptionHandler
{
    public static async Task Handle(HttpContext context)
    {
        var ex = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
        if (ex is null) return;

        var (status, message) = ex switch
        {
            KeyNotFoundException  => (HttpStatusCode.NotFound, ex.Message),
            UnauthorizedAccessException => (HttpStatusCode.Forbidden, ex.Message),
            ArgumentException    => (HttpStatusCode.BadRequest, ex.Message),
            _                    => (HttpStatusCode.InternalServerError, "An unexpected error occurred.")
        };

        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = message,
            status = (int)status
        }));
    }
}
