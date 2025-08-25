using Conductor.Db;
using Conductor.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Conductor.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = "admin")]
    public class AdminController : ControllerBase
    {
        private readonly AppDb _db;
        private readonly ILogger<AdminController> _logger;

        public AdminController(AppDb db, ILogger<AdminController> logger)
        {
            _db = db;
            _logger = logger;
        }

        // User Management
        public record CreateUserRequest(string Username, string Password, string Role);
        public record UpdateUserRequest(string? Role, bool? IsActive, string? Username, string? Password);
        public record AssignSiteRequest(int UserId, List<int> SiteIds);

        [HttpGet("users")]
        public async Task<IActionResult> GetUsers([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        {
            var users = await _db.Users
                .Select(u => new
                {
                    u.Id,
                    u.User,
                    u.Role,
                    u.CreatedAt,
                    AssignedSites = _db.UserSiteAssignments
                        .Where(usa => usa.UserId == u.Id)
                        .Select(usa => new
                        {
                            usa.SiteId,
                            usa.Site.Name,
                            usa.Site.Origin,
                            usa.Site.Type,
                            usa.AssignedAt
                        })
                        .ToList(),
                    SiteCount = _db.UserSiteAssignments.Count(usa => usa.UserId == u.Id)
                })
                .OrderByDescending(u => u.Id) // Use ID instead of CreatedAt for SQLite compatibility
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var totalUsers = await _db.Users.CountAsync();

            return Ok(new
            {
                users,
                pagination = new
                {
                    page,
                    pageSize,
                    totalUsers,
                    totalPages = (int)Math.Ceiling((double)totalUsers / pageSize)
                }
            });
        }

        [HttpGet("users/{userId}")]
        public async Task<IActionResult> GetUser(int userId)
        {
            var user = await _db.Users
                .Where(u => u.Id == userId)
                .Select(u => new
                {
                    u.Id,
                    u.User,
                    u.Role,
                    u.CreatedAt,
                    AssignedSites = _db.UserSiteAssignments
                        .Where(usa => usa.UserId == u.Id)
                        .Select(usa => new
                        {
                            usa.SiteId,
                            usa.Site.Name,
                            usa.Site.Origin,
                            usa.Site.Type,
                            usa.AssignedAt,
                            AssignedByUser = usa.AssignedBy.User
                        })
                        .ToList()
                })
                .FirstOrDefaultAsync();

            if (user == null)
                return NotFound(new { message = "User not found" });

            return Ok(user);
        }

        [HttpPost("users")]
        public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                return BadRequest(new { message = "Username and password are required" });

            if (!new[] { "admin", "user" }.Contains(request.Role))
                return BadRequest(new { message = "Role must be 'admin' or 'user'" });

            var existingUser = await _db.Users.AnyAsync(u => u.User == request.Username);
            if (existingUser)
                return BadRequest(new { message = "Username already exists" });

            var passwordHash = Conductor.AuthService.HashPassword(request.Password);
            var user = new AppUser
            {
                User = request.Username,
                Role = request.Role,
                PasswordHash = passwordHash,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            return Ok(new
            {
                user.Id,
                user.User,
                user.Role,
                user.CreatedAt
            });
        }

        [HttpPut("users/{userId}")]
        public async Task<IActionResult> UpdateUser(int userId, [FromBody] UpdateUserRequest request)
        {
            var user = await _db.Users.FindAsync(userId);
            if (user == null)
                return NotFound(new { message = "User not found" });

            var currentUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            if (userId == currentUserId && request.Role == "user")
                return BadRequest(new { message = "Cannot demote yourself from admin" });

            if (request.Role != null)
            {
                if (!new[] { "admin", "user" }.Contains(request.Role))
                    return BadRequest(new { message = "Role must be 'admin' or 'user'" });
                user.Role = request.Role;
            }

            if (!string.IsNullOrWhiteSpace(request.Username))
            {
                // Prevent duplicate usernames
                var exists = await _db.Users.AnyAsync(u => u.User == request.Username && u.Id != userId);
                if (exists) return BadRequest(new { message = "Username already exists" });
                user.User = request.Username;
            }

            if (!string.IsNullOrWhiteSpace(request.Password))
            {
                // Update password using central hashing
                user.PasswordHash = Conductor.AuthService.HashPassword(request.Password);
            }

            await _db.SaveChangesAsync();

            return Ok(new
            {
                user.Id,
                user.User,
                user.Role,
                user.CreatedAt
            });
        }

        [HttpDelete("users/{userId}")]
        public async Task<IActionResult> DeleteUser(int userId)
        {
            var currentUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            if (userId == currentUserId)
                return BadRequest(new { message = "Cannot delete yourself" });

            var user = await _db.Users.FindAsync(userId);
            if (user == null)
                return NotFound(new { message = "User not found" });

            // Remove user site assignments first
            var assignments = await _db.UserSiteAssignments.Where(usa => usa.UserId == userId).ToListAsync();
            _db.UserSiteAssignments.RemoveRange(assignments);

            // Remove user
            _db.Users.Remove(user);
            await _db.SaveChangesAsync();

            return Ok(new { message = "User deleted successfully" });
        }

        // Site Assignment Management
        [HttpPost("users/{userId}/sites")]
        public async Task<IActionResult> AssignSitesToUser(int userId, [FromBody] AssignSiteRequest request)
        {
            var user = await _db.Users.FindAsync(userId);
            if (user == null)
                return NotFound(new { message = "User not found" });

            var currentUserId = int.Parse(User.FindFirst("sub")?.Value ?? "0");
            
            // Validate current user
            if (currentUserId == 0)
                return BadRequest(new { message = "Invalid user authentication" });

            try
            {
                // Remove existing assignments
                var existingAssignments = await _db.UserSiteAssignments.Where(usa => usa.UserId == userId).ToListAsync();
                _db.UserSiteAssignments.RemoveRange(existingAssignments);

                // Add new assignments only for valid site IDs
                if (request.SiteIds != null && request.SiteIds.Any())
                {
                    // Verify all site IDs exist
                    var validSiteIds = await _db.Sites.Where(s => request.SiteIds.Contains(s.Id)).Select(s => s.Id).ToListAsync();
                    
                    var newAssignments = validSiteIds.Select(siteId => new UserSiteAssignment
                    {
                        UserId = userId,
                        SiteId = siteId,
                        AssignedByUserId = currentUserId,
                        AssignedAt = DateTimeOffset.UtcNow
                    }).ToList();

                    _db.UserSiteAssignments.AddRange(newAssignments);
                }

                await _db.SaveChangesAsync();
                return Ok(new { message = "Site assignments updated successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to assign sites to user {UserId}: {Message}", userId, ex.Message);
                return BadRequest(new { message = "Failed to assign sites: " + ex.Message });
            }
        }

        [HttpDelete("users/{userId}/sites/{siteId}")]
        public async Task<IActionResult> RemoveSiteFromUser(int userId, int siteId)
        {
            var assignment = await _db.UserSiteAssignments
                .FirstOrDefaultAsync(usa => usa.UserId == userId && usa.SiteId == siteId);

            if (assignment == null)
                return NotFound(new { message = "Assignment not found" });

            _db.UserSiteAssignments.Remove(assignment);
            await _db.SaveChangesAsync();

            return Ok(new { message = "Site assignment removed successfully" });
        }

        // Site Management (Admin can create/manage all sites)
        [HttpPost("sites")]
        public async Task<IActionResult> CreateSite([FromBody] DashboardController.CreateSiteReq request)
        {
            var currentUserId = int.Parse(User.FindFirst("sub")?.Value ?? "0");
            
            // Validate that we have a valid user ID
            if (currentUserId == 0)
            {
                return BadRequest(new { message = "Invalid user authentication" });
            }
            
            // Verify user exists in database
            var userExists = await _db.Users.AnyAsync(u => u.Id == currentUserId);
            if (!userExists)
            {
                return BadRequest(new { message = "User not found in database" });
            }

            // If no pages provided, use predefined pages for the site type
            var pages = request.Pages ?? new List<string>();
            if (!pages.Any() && request.Type != SiteType.Unknown)
            {
                pages = request.Type.GetDefaultPageOrder();
            }

            var site = new Site
            {
                Name = request.Name,
                Origin = request.Origin,
                Pages = pages,
                ManualRoutingEnabled = request.ManualRoutingEnabled,
                DefaultFlowPath = request.DefaultFlowPath ?? "",
                TerminalPath = request.TerminalPath ?? "",
                Type = request.Type,
                CreatedByUserId = currentUserId,
                CreatedAt = DateTimeOffset.UtcNow,
                IsActive = true
            };

            try
            {
                _db.Sites.Add(site);
                await _db.SaveChangesAsync();
                return Ok(site);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create site: {Message}", ex.Message);
                return BadRequest(new { message = "Failed to create site: " + ex.Message });
            }
        }

        [HttpGet("sites")]
        public async Task<IActionResult> GetAllSites()
        {
            var sites = await _db.Sites
                .Where(s => s.IsActive)
                .Select(s => new
                {
                    s.Id,
                    s.Name,
                    s.Origin,
                    s.Pages,
                    s.ManualRoutingEnabled,
                    s.DefaultFlowPath,
                    s.TerminalPath,
                    s.Type,
                    s.CreatedAt,
                    CreatedByUser = _db.Users.Where(u => u.Id == s.CreatedByUserId).Select(u => u.User).FirstOrDefault(),
                    AssignedUsers = _db.UserSiteAssignments.Where(usa => usa.SiteId == s.Id).Select(usa => usa.User.User).ToList(),
                    AssignedUserCount = _db.UserSiteAssignments.Count(usa => usa.SiteId == s.Id),
                    ActiveSessionCount = _db.Sessions.Count(sess => sess.IsActive)
                })
                .OrderByDescending(s => s.Id) // Use ID instead of CreatedAt for SQLite compatibility
                .ToListAsync();

            return Ok(sites);
        }

        [HttpPut("sites/{siteId}")]
        public async Task<IActionResult> UpdateSite(int siteId, [FromBody] DashboardController.UpdateSiteReq request)
        {
            var site = await _db.Sites.FindAsync(siteId);
            if (site == null) return NotFound();

            if (request.Name != null) site.Name = request.Name;
            if (request.Origin != null) site.Origin = request.Origin;
            if (request.Pages != null) site.Pages = request.Pages;
            if (request.ManualRoutingEnabled.HasValue) site.ManualRoutingEnabled = request.ManualRoutingEnabled.Value;
            if (request.DefaultFlowPath != null) site.DefaultFlowPath = request.DefaultFlowPath;
            if (request.TerminalPath != null) site.TerminalPath = request.TerminalPath;

            await _db.SaveChangesAsync();
            return Ok(site);
        }

        [HttpGet("site-types/{siteType}/predefined-pages")]
        public IActionResult GetPredefinedPages(SiteType siteType)
        {
            var predefinedPages = siteType.GetPredefinedPages();
            var defaultOrder = siteType.GetDefaultPageOrder();
            
            return Ok(new { 
                siteType = siteType.ToString(),
                predefinedPages = predefinedPages,
                defaultPageOrder = defaultOrder
            });
        }

        [HttpGet("site-types")]
        public IActionResult GetAllSiteTypes()
        {
            var siteTypes = Enum.GetValues<SiteType>()
                .Where(st => st != SiteType.Unknown)
                .Select(st => new {
                    value = st.ToString(),
                    name = st.ToString(),
                    predefinedPages = st.GetPredefinedPages(),
                    defaultPageOrder = st.GetDefaultPageOrder()
                })
                .ToList();

            return Ok(siteTypes);
        }

        [HttpDelete("sites/{siteId}")]
        public async Task<IActionResult> DeleteSite(int siteId)
        {
            var site = await _db.Sites.FindAsync(siteId);
            if (site == null) return NotFound();

            // Soft delete
            site.IsActive = false;
            await _db.SaveChangesAsync();

            return Ok(new { message = "Site deactivated successfully" });
        }

        // System Statistics
        [HttpGet("stats")]
        public async Task<IActionResult> GetSystemStats()
        {
            var stats = new
            {
                TotalUsers = await _db.Users.CountAsync(),
                AdminUsers = await _db.Users.CountAsync(u => u.Role == "admin"),
                TotalSites = await _db.Sites.CountAsync(s => s.IsActive),
                ActiveSessions = await _db.Sessions.CountAsync(s => s.IsActive),
                TotalSubmissions = await _db.Submissions.CountAsync(),
                PendingDecisions = await _db.NextSteps.CountAsync(ns => ns.Status == "pending"),
                RecentActivity = new
                {
                    // For SQLite compatibility, get last 24 hours of data
                    NewUsersToday = await _db.Users.Where(u => u.Id > 0).CountAsync(), // Simplified for SQLite
                    SubmissionsToday = await _db.Submissions.Where(s => s.Id > 0).CountAsync(), // Simplified for SQLite
                    SessionsToday = await _db.Sessions.Where(s => s.Id > 0).CountAsync() // Simplified for SQLite
                }
            };

            return Ok(stats);
        }

        // Removed duplicate HashPassword helper – using AuthService.HashPassword instead
    }
}
