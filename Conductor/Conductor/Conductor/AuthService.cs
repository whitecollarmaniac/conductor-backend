using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Conductor.Db;
using Conductor.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Conductor
{
    public class AuthService
    {
        readonly AppDb _db;
        readonly IConfiguration _cfg;
        readonly IMemoryCache _cache;

        const int HashIterations = 120_000;
        const int SaltSize = 16;
        const int KeySize = 32;
        static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(10);
        static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);
        const int LockoutThreshold = 10;

        public AuthService(AppDb db, IConfiguration cfg, IMemoryCache cache)
        {
            _db = db;
            _cfg = cfg;
            _cache = cache;
        }

        public async Task<(bool ok, string? err, AppUser? user, string? jwt, DateTimeOffset? exp)> Register(string username, string password, string regKey)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return (false, "Username and password are required", null, null, null);

            var expected = _cfg["Auth:RegistrationKey"];
            if (string.IsNullOrWhiteSpace(expected) || regKey != expected)
                return (false, "Invalid registration key", null, null, null);

            var exists = await _db.Users.AnyAsync(u => u.User == username);
            if (exists)
                return (false, "Username already exists", null, null, null);

            var hash = HashPassword(password);
            
            // Check if this is the first user - make them admin
            var isFirstUser = !await _db.Users.AnyAsync();
            
            var user = new AppUser
            {
                User = username,
                Role = isFirstUser ? "admin" : "user",
                PasswordHash = hash,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            var (jwt, exp, err) = IssueJwt(user);
            if (jwt == null) return (false, err ?? "JWT issue failed", null, null, null);
            return (true, null, user, jwt, exp);
        }

        public async Task<(bool ok, string? err, AppUser? user, string? jwt, DateTimeOffset? exp)> Login(string username, string password, string? ip)
        {
            if (IsLocked(username, ip)) return (false, "Locked due to too many failed attempts. Try later.", null, null, null);

            var user = await _db.Users.FirstOrDefaultAsync(u => u.User == username);
            if (user == null || !VerifyPassword(password, user.PasswordHash))
            {
                RecordFailure(username, ip);
                return (false, "Invalid credentials", null, null, null);
            }

            ClearFailures(username, ip);

            var (jwt, exp, err) = IssueJwt(user);
            if (jwt == null) return (false, err ?? "JWT issue failed", null, null, null);
            return (true, null, user, jwt, exp);
        }

        public static string HashPassword(string password)
        {
            Span<byte> salt = stackalloc byte[SaltSize];
            RandomNumberGenerator.Fill(salt);

            var pbkdf2 = new Rfc2898DeriveBytes(password, salt.ToArray(), HashIterations, HashAlgorithmName.SHA256);
            var key = pbkdf2.GetBytes(KeySize);

            return $"PBKDF2${HashIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
        }

        static bool VerifyPassword(string password, string stored)
        {
            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "PBKDF2") return false;

            var iterations = int.Parse(parts[1]);
            var salt = Convert.FromBase64String(parts[2]);
            var key = Convert.FromBase64String(parts[3]);

            var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
            var computed = pbkdf2.GetBytes(key.Length);
            return CryptographicOperations.FixedTimeEquals(computed, key);
        }

        (string? jwt, DateTimeOffset? exp, string? err) IssueJwt(AppUser user)
        {
            var hmac = _cfg["Api:HmacSecret"];
            if (string.IsNullOrWhiteSpace(hmac)) return (null, null, "Missing Api:HmacSecret");

            var issuers = _cfg.GetSection("Api:ValidIssuers").Get<string[]>() ?? Array.Empty<string>();
            var audiences = _cfg.GetSection("Api:ValidAudiences").Get<string[]>() ?? Array.Empty<string>();
            var issuer = issuers.FirstOrDefault() ?? _cfg["Api:Issuer"];
            var audience = audiences.FirstOrDefault() ?? _cfg["Api:Audience"];

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(hmac));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var now = DateTimeOffset.UtcNow;
            var exp = now.AddHours(1);

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.UniqueName, user.User),
                new Claim(ClaimTypes.Name, user.User),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                notBefore: now.UtcDateTime,
                expires: exp.UtcDateTime,
                signingCredentials: creds
            );

            var encoded = new JwtSecurityTokenHandler().WriteToken(token);
            return (encoded, exp, null);
        }

        void RecordFailure(string username, string? ip)
        {
            var uKey = $"fail:u:{username}";
            var iKey = ip is null ? null : $"fail:i:{ip}";
            Increment(uKey);
            if (iKey != null) Increment(iKey);
            if (Get(uKey) >= LockoutThreshold || (iKey != null && Get(iKey) >= LockoutThreshold))
                _cache.Set($"lock:{username}:{ip}", true, LockoutDuration);
        }

        void ClearFailures(string username, string? ip)
        {
            _cache.Remove($"fail:u:{username}");
            if (ip != null) _cache.Remove($"fail:i:{ip}");
            _cache.Remove($"lock:{username}:{ip}");
        }

        bool IsLocked(string username, string? ip) => _cache.TryGetValue($"lock:{username}:{ip}", out _);

        void Increment(string key)
        {
            if (!_cache.TryGetValue<int>(key, out var count)) count = 0;
            count++;
            _cache.Set(key, count, new MemoryCacheEntryOptions { SlidingExpiration = LockoutWindow });
        }

        int Get(string key) => _cache.TryGetValue<int>(key, out var v) ? v : 0;
    }
}
