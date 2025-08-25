using Conductor.Db;
using Conductor.Models;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace Conductor.Middleware
{
    public class SessionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IServiceScopeFactory _scopeFactory;

        public SessionMiddleware(RequestDelegate next, IServiceScopeFactory scopeFactory)
        {
            _next = next;
            _scopeFactory = scopeFactory;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                // Skip session tracking for problematic paths
                var path = context.Request.Path.Value?.ToLower() ?? "";
                if (path.Contains("/hub") || 
                    path.Contains("/_framework") ||
                    path.Contains("/css") ||
                    path.Contains("/js") ||
                    context.WebSockets.IsWebSocketRequest) // Skip WebSocket requests
                {
                    await _next(context);
                    return;
                }

                // Handle session for API endpoints that need it
                if (path.Contains("/api/submit") || path.Contains("/api/redirect"))
                {
                    await SetSessionForApiEndpoint(context);
                }
                // Only track sessions for actual page requests
                else if (context.Request.Method == "GET" && 
                    (path == "/" || path.Contains(".html") || string.IsNullOrEmpty(Path.GetExtension(path))))
                {
                    // Simple session tracking for page requests
                    var sessionIdCookie = context.Request.Cookies["ConductorSessionId"];
                    if (!string.IsNullOrEmpty(sessionIdCookie) && int.TryParse(sessionIdCookie, out var sessionId))
                    {
                        context.Items["sessionId"] = sessionId;
                    }
                }
            }
            catch (Exception ex)
            {
                var logger = context.RequestServices.GetRequiredService<ILogger<SessionMiddleware>>();
                logger.LogWarning(ex, "SessionMiddleware error");
                // Continue without session tracking
            }

            await _next(context);
        }

        private async Task SetSessionForApiEndpoint(HttpContext context)
        {
            try
            {
                // For API endpoints, look for session ID in cookie or header
                var sessionIdValue = context.Request.Cookies["ConductorSessionId"] 
                                   ?? context.Request.Headers["X-Session-Id"].FirstOrDefault();

                var logger = context.RequestServices.GetRequiredService<ILogger<SessionMiddleware>>();
                logger.LogDebug("SessionMiddleware API: Path={Path}, SessionIdValue={SessionId}", context.Request.Path, sessionIdValue);

                if (!string.IsNullOrEmpty(sessionIdValue) && int.TryParse(sessionIdValue, out var sessionId))
                {
                    // Validate session exists and is active
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                    
                    var session = await db.Sessions.FindAsync(sessionId);
                    if (session != null && session.IsActive)
                    {
                        context.Items["sessionId"] = sessionId;
                        logger.LogDebug("SessionMiddleware API: Set sessionId={SessionId} for path={Path}", sessionId, context.Request.Path);
                        
                        // Update last seen
                        session.LastSeenAt = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync();
                    }
                    else
                    {
                        logger.LogDebug("SessionMiddleware API: Session {SessionId} not found or inactive", sessionId);
                    }
                }
                else
                {
                    logger.LogDebug("SessionMiddleware API: No valid session ID found");
                }
            }
            catch (Exception ex)
            {
                var logger = context.RequestServices.GetRequiredService<ILogger<SessionMiddleware>>();
                logger.LogWarning(ex, "SessionMiddleware API session error");
                // Continue without session
            }
        }
    }

    public static class SessionMiddlewareExtensions
    {
        public static IApplicationBuilder UseSessionTracking(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<SessionMiddleware>();
        }
    }
}
