using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Conductor.Db;
using Microsoft.EntityFrameworkCore;
using System.Net.Sockets;

namespace Conductor.RealTime
{
    // Note: Authentication is handled in the connection pipeline, not here
    // Adding [Authorize] here can interfere with SignalR negotiation
    public class DashboardHub : Hub
    {
        private readonly AppDb _db;
        private readonly ILogger<DashboardHub> _logger;

        public DashboardHub(AppDb db, ILogger<DashboardHub> logger)
        {
            _db = db;
            _logger = logger;
        }
        public override async Task OnConnectedAsync()
        {
            var q = Context.GetHttpContext()?.Request.Query;
            var role = q?["role"].ToString();
            
            // SECURITY CHECKPOINT 1: Verify user is authenticated (except for site connections)
            var user = Context.User;
            if (role != "site" && user?.Identity?.IsAuthenticated != true)
            {
                string ipSafe;
                try
                {
                    ipSafe = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                }
                catch (SocketException)
                {
                    ipSafe = "unknown";
                }
                _logger.LogWarning("SECURITY: Unauthenticated connection attempt blocked from {Ip}", ipSafe);
                Context.Abort();
                return;
            }

            // SECURITY CHECKPOINT 2: Validate JWT token is not expired (except for site connections)
            if (role != "site" && user?.Identity?.IsAuthenticated == true)
            {
                var tokenExp = user.FindFirst("exp")?.Value;
                if (tokenExp != null && long.TryParse(tokenExp, out var expTimestamp))
                {
                    var expDate = DateTimeOffset.FromUnixTimeSeconds(expTimestamp);
                    if (expDate <= DateTimeOffset.UtcNow)
                    {
                        _logger.LogWarning("SECURITY: Expired token blocked for user {User}", user.FindFirst("unique_name")?.Value);
                        Context.Abort();
                        return;
                    }
                }
            }

            var siteId = q?["siteId"].ToString();
            var sessionId = q?["sessionId"].ToString();

            // Log connection for security monitoring
            if (role == "site")
            {
                _logger.LogInformation("SignalR: Site connection connected to hub for session {SessionId}", sessionId);
            }
            else
            {
                var userName = user.FindFirst("unique_name")?.Value ?? "unknown";
                var userRole = user.FindFirst("http://schemas.microsoft.com/ws/2008/06/identity/claims/role")?.Value ?? "unknown";
                _logger.LogInformation("SignalR: User {User} (Role: {Role}) connected to hub", userName, userRole);
            }

            // SECURITY CHECKPOINT 3: Site access control with permissions
            if (role == "dashboard")
            {
                var userName = user.FindFirst("unique_name")?.Value ?? "unknown";
                var userRole = user.FindFirst("http://schemas.microsoft.com/ws/2008/06/identity/claims/role")?.Value ?? "unknown";
                
                // Add to general dashboard group for session notifications
                await Groups.AddToGroupAsync(Context.ConnectionId, "dashboard");
                Console.WriteLine($"SECURITY: User '{userName}' added to dashboard group");
                
                if (!string.IsNullOrWhiteSpace(siteId))
                {
                    var userId = user.FindFirst("sub")?.Value;
                    var isAdmin = userRole == "admin";
                    
                    if (isAdmin || await UserHasAccessToSite(userId, siteId))
                    {
                        await Groups.AddToGroupAsync(Context.ConnectionId, $"site-{siteId}");
                        _logger.LogInformation("SECURITY: User {User} authorized for site-{Site}", userName, siteId);
                    }
                    else
                    {
                        _logger.LogWarning("SECURITY: Access denied - User {User} attempted unauthorized access to site-{Site}", userName, siteId);
                        Context.Abort(); // Disconnect unauthorized access attempts
                        return;
                    }
                }
            }

            if (role == "site" && !string.IsNullOrWhiteSpace(sessionId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"sess-{sessionId}");
            }
            
            // Track site connections for dashboard monitoring
            if (role == "site" && !string.IsNullOrWhiteSpace(siteId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"site-monitor-{siteId}");
                
                // Notify dashboards that this site is now connected
                await Clients.Group($"site-{siteId}").SendAsync("SiteConnected", new { siteId, timestamp = DateTimeOffset.UtcNow });
                _logger.LogInformation("SITE CONNECTION: Site {SiteId} connected to hub", siteId);
            }

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var userName = Context.User?.FindFirst("unique_name")?.Value ?? "unknown";
            _logger.LogInformation("SignalR disconnection: User {User} disconnected from hub", userName);
            
            // Handle site disconnections
            var q = Context.GetHttpContext()?.Request.Query;
            var role = q?["role"].ToString();
            var siteId = q?["siteId"].ToString();
            var sessionId = q?["sessionId"].ToString();
            
            if (role == "site" && !string.IsNullOrWhiteSpace(siteId))
            {
                // Notify dashboards that this site is now disconnected
                await Clients.Group($"site-{siteId}").SendAsync("SiteDisconnected", new { siteId, timestamp = DateTimeOffset.UtcNow });
                _logger.LogInformation("SITE DISCONNECTION: Site {SiteId} disconnected from hub", siteId);
            }

            // If this was a site client tied to a specific session, proactively mark it inactive immediately
            if (role == "site" && !string.IsNullOrWhiteSpace(sessionId))
            {
                if (int.TryParse(sessionId, out var sid))
                {
                    try
                    {
                        var session = await _db.Sessions.FindAsync(sid);
                        if (session != null)
                        {
                            session.IsActive = false;
                            session.LastSeenAt = DateTimeOffset.UtcNow;
                            await _db.SaveChangesAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to mark session {SessionId} inactive on site disconnect", sid);
                    }

                    // Broadcast to dashboards so UI doesn't stay in 'waiting'
                    await Clients.Group("dashboard").SendAsync("sessionUpdated", new
                    {
                        id = sid,
                        sessionId = sid,
                        isActive = false,
                        lastSeenAt = DateTimeOffset.UtcNow,
                        status = "inactive"
                    });
                }
            }
            
            await base.OnDisconnectedAsync(exception);
        }

        private async Task<bool> UserHasAccessToSite(string? userId, string siteId)
        {
            if (string.IsNullOrWhiteSpace(userId) || !int.TryParse(userId, out var userIdInt) || !int.TryParse(siteId, out var siteIdInt))
            {
                _logger.LogWarning("SECURITY: Invalid user/site parameters - UserId: {UserId}, SiteId: {SiteId}", userId, siteId);
                return false;
            }

            var hasAccess = await _db.UserSiteAssignments
                .AnyAsync(usa => usa.UserId == userIdInt && usa.SiteId == siteIdInt);
                
            _logger.LogDebug("SECURITY: Database access check - User {UserId} -> Site {SiteId}: {Has}", userIdInt, siteIdInt, hasAccess);
            return hasAccess;
        }

        // SECURITY: Add method-level protection for any hub methods
        private bool EnsureAuthenticated()
        {
            var q = Context.GetHttpContext()?.Request.Query;
            var role = q?["role"].ToString();
            
            // Site connections don't need authentication
            if (role == "site") return true;
            
            if (Context.User?.Identity?.IsAuthenticated != true)
            {
                _logger.LogWarning("SECURITY: Unauthorized hub method call blocked from {ConnectionId}", Context.ConnectionId);
                Context.Abort();
                return false;
            }
            return true;
        }

        // Allows a site client to explicitly join its own session group after connection
        public async Task JoinSessionGroup(string sessionId)
        {
            if (!EnsureAuthenticated()) return;

            if (string.IsNullOrWhiteSpace(sessionId)) return;

            await Groups.AddToGroupAsync(Context.ConnectionId, $"sess-{sessionId}");
        }
    }
}
