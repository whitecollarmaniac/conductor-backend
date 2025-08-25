using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Net.Sockets;

namespace Conductor.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        readonly AuthService _auth;

        public AuthController(AuthService auth) => _auth = auth;

        public record LoginRequest(string Username, string Password);
        public record RegisterRequest(string Username, string Password, string RegKey);

        [AllowAnonymous]
        [EnableRateLimiting("auth")]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest req)
        {
            string ip;
            try
            {
                ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            }
            catch (SocketException)
            {
                ip = "unknown";
            }
            var (ok, err, user, jwt, exp) = await _auth.Login(req.Username, req.Password, ip);
            if (!ok) return Unauthorized(new { message = err });
            return Ok(new { token = jwt, expiresAt = exp, user = user!.User, role = user.Role });
        }

        [AllowAnonymous]
        [EnableRateLimiting("auth")]
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest req)
        {
            // In production, disable public self-registration to keep system closed.
            if (!HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment())
            {
                return StatusCode(403, new { message = "Registration is disabled in production" });
            }

            var (ok, err, user, jwt, exp) = await _auth.Register(req.Username, req.Password, req.RegKey);
            if (!ok) return BadRequest(new { message = err });
            return Ok(new { token = jwt, expiresAt = exp, user = user!.User, role = user.Role });
        }

/*        [Authorize]
        [HttpGet("me")]
        public IActionResult Me()
        {
            return Ok(new
            {
                name = User.Identity?.Name,
                role = User.IsInRole("admin") ? "admin" : "user"
            });
        }*/
    }
}
