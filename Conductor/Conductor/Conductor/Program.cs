using Conductor.Db;
using Conductor.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Threading.RateLimiting;
using System.Text.Json.Serialization;
using System.Net.Sockets;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    // QUIC/HTTP3 causes RemoteIpAddress to throw SocketException in some environments.
    // Restrict protocols to HTTP/1.1 and HTTP/2 for local development.
    options.ConfigureEndpointDefaults(lo =>
    {
        lo.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
    });
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.Converters.Add(new Conductor.Json.IPAddressJsonConverter());
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });
builder.Services.AddOpenApi();
builder.Services.AddMemoryCache();

// Db (SQLite)
builder.Services.AddDbContext<AppDb>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("AppDb")));

// Auth
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = "some-scheme";
    options.DefaultChallengeScheme = "some-scheme";
})
.AddJwtBearer("some-scheme", jwtOptions =>
{
    var cfg = builder.Configuration;
    var hmac = cfg["Api:HmacSecret"];

    if (!string.IsNullOrWhiteSpace(hmac))
    {
        jwtOptions.Authority = null;
        jwtOptions.MetadataAddress = null;
        jwtOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidAudiences = cfg.GetSection("Api:ValidAudiences").Get<string[]>() ?? Array.Empty<string>(),
            ValidIssuers = cfg.GetSection("Api:ValidIssuers").Get<string[]>() ?? Array.Empty<string>(),
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(hmac)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }
    else
    {
        jwtOptions.MetadataAddress = cfg["Api:MetadataAddress"];
        jwtOptions.Authority = cfg["Api:Authority"];
        jwtOptions.Audience = cfg["Api:Audience"];
        jwtOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidAudiences = cfg.GetSection("Api:ValidAudiences").Get<string[]>() ?? Array.Empty<string>(),
            ValidIssuers = cfg.GetSection("Api:ValidIssuers").Get<string[]>() ?? Array.Empty<string>(),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }

    jwtOptions.MapInboundClaims = false;
});

builder.Services.AddAuthorization();

builder.Services.AddScoped<Conductor.AuthService>();

// Background service: mark stale sessions inactive and broadcast via SignalR
builder.Services.AddHostedService<Conductor.Background.InactiveSessionMonitor>();

// SignalR with authentication
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    // Add better timeout handling for production
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    options.HandshakeTimeout = TimeSpan.FromSeconds(30);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
});

// Configure SignalR authentication to accept tokens from query string
builder.Services.Configure<JwtBearerOptions>("some-scheme", options =>
{
    var existingOnMessageReceived = options.Events?.OnMessageReceived;

    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = async context =>
        {
            // Call existing handler first if it exists
            if (existingOnMessageReceived != null)
            {
                await existingOnMessageReceived(context);
            }

            // Handle SignalR token from query string
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;

            if (!string.IsNullOrEmpty(accessToken) &&
                (path.StartsWithSegments("/hub") || path.StartsWithSegments("/hub/negotiate")))
            {
                context.Token = accessToken;
            }
        }
    };
});

// Rate limiting
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("auth", httpContext =>
    {
        string ip;
        try
        {
            ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }
        catch (SocketException sockEx)
        {
            // Extremely rare – the connection may not be backed by a normal
            // socket (e.g. HTTP/3, in-memory test server).  We fall back to a
            // fixed key so the request still proceeds and add a warning log.
            var logger = httpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogWarning(sockEx, "Failed to read RemoteIpAddress – falling back to generic rate-limit key");
            ip = "unknown";
        }
        var key = $"{ip}:{httpContext.Request.Path}";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
});


// Session/heartbeat configuration (config-driven)
var heartbeatIntervalSeconds = builder.Configuration.GetValue<int?>("Sessions:HeartbeatSeconds") ?? 30;
var inactiveMultiplier = builder.Configuration.GetValue<int?>("Sessions:InactiveMultiplier") ?? 2;
builder.Services.AddSingleton(new
{
    HeartbeatSeconds = heartbeatIntervalSeconds,
    InactiveThresholdSeconds = heartbeatIntervalSeconds * inactiveMultiplier
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("DevFrontend", policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            // In development, allow everything for easier testing
            policy
                .SetIsOriginAllowed(_ => true) // Allow any origin
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials() // Keep credentials for SignalR
                .WithExposedHeaders("*"); // Required for SignalR
        }
        else
        {
            // Production: Allow dashboard domains + dynamically check registered sites
            policy
                .SetIsOriginAllowed(origin =>
                {
                    // Always allow dashboard/admin domains
                    var allowedDashboardDomains = new[]
                    {
                        "https://conductor.watch",
                        "http://conductor.watch",
                        "https://panelback.blkmetrics.com",
                        "https://blkmetrics.com",
                        "http://panelback.blkmetrics.com",
                        "http://blkmetrics.com"
                    };

                    if (allowedDashboardDomains.Contains(origin))
                    {
                        return true;
                    }

                    // For client sites, check if the origin matches any registered site
                    try
                    {
                        var serviceProvider = builder.Services.BuildServiceProvider();
                        using var scope = serviceProvider.CreateScope();
                        var db = scope.ServiceProvider.GetService<AppDb>();

                        if (db != null)
                        {
                            var sites = db.Sites.Where(s => s.IsActive).ToList();
                            var isRegisteredSite = sites.Any(site =>
                                origin.Equals(site.Origin, StringComparison.OrdinalIgnoreCase) ||
                                origin.Equals($"https://{site.Origin}", StringComparison.OrdinalIgnoreCase) ||
                                origin.Equals($"http://{site.Origin}", StringComparison.OrdinalIgnoreCase));

                            return isRegisteredSite;
                        }
                    }
                    catch (Exception ex)
                    {
                        var logger = builder.Services.BuildServiceProvider().GetService<ILogger<Program>>();
                        logger?.LogWarning(ex, "Failed to check CORS origin against registered sites: {Origin}", origin);
                    }

                    return false;
                })
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials() // Required for Authorization header
                .WithExposedHeaders("*"); // Required for SignalR
        }
    });
});


var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.Migrate();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    if (app.Environment.IsDevelopment())
    {
        if (!db.Users.Any(u => u.User == "admin"))
        {
            var testAdmin = new Conductor.Models.AppUser
            {
                User = "admin",
                Role = "admin",
                PasswordHash = HashPasswordForDevelopment("admin"),
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Users.Add(testAdmin);
            logger.LogWarning("Created test admin user (admin/admin) - FOR DEVELOPMENT ONLY");
        }

        if (!db.Users.Any(u => u.User == "test"))
        {
            var testUser = new Conductor.Models.AppUser
            {
                User = "test",
                Role = "user",
                PasswordHash = HashPasswordForDevelopment("test"),
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Users.Add(testUser);
        }

        db.SaveChanges();
    }
    else
    {
        // Production: ensure a default admin exists (closed system). Password provided by user.
        const string defaultAdminUser = "admin";
        const string defaultAdminPassword = "KbowaGBjtGwnw0y9FVerb6r";
        var admin = db.Users.FirstOrDefault(u => u.User == defaultAdminUser);
        if (admin == null)
        {
            var hash = Conductor.AuthService.HashPassword(defaultAdminPassword);
            var newAdmin = new Conductor.Models.AppUser
            {
                User = defaultAdminUser,
                Role = "admin",
                PasswordHash = hash,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Users.Add(newAdmin);
            db.SaveChanges();
            logger.LogWarning("Default admin account created in production. CHANGE PASSWORD IMMEDIATELY.");
        }
    }
}

// Temporarily disable HTTPS redirection for debugging
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Production logging only: keep logs server-side and reduce noise
if (app.Environment.IsDevelopment())
{
    // Keep existing minimal logging in development only (disabled for prod)
    app.Use(async (context, next) =>
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("{Method} {Path}", context.Request.Method, context.Request.Path);
        await next();
    });
}
app.UseRateLimiter();
app.UseCors("DevFrontend"); // 👈 Must go before UseAuthentication
// Enable session tracking middleware
app.UseSessionTracking(); // Custom session tracking middleware
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<Conductor.RealTime.DashboardHub>("/hub");

// Health check endpoint
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", ts = DateTimeOffset.UtcNow }))
   .WithName("Healthz")
   .WithTags("System");

// SignalR health check endpoint
app.MapGet("/hub/health", () => Results.Ok(new
{
    signalr = "ok",
    endpoint = "/hub",
    transport = "websockets,sse",
    ts = DateTimeOffset.UtcNow
}))
   .WithName("SignalRHealth")
   .WithTags("SignalR");

app.Run();

// Helper method for development admin user
static string HashPasswordForDevelopment(string password)
{
    const int iterations = 120_000;
    const int saltSize = 16;
    const int keySize = 32;

    Span<byte> salt = stackalloc byte[saltSize];
    System.Security.Cryptography.RandomNumberGenerator.Fill(salt);

    var pbkdf2 = new System.Security.Cryptography.Rfc2898DeriveBytes(password, salt.ToArray(), iterations, System.Security.Cryptography.HashAlgorithmName.SHA256);
    var key = pbkdf2.GetBytes(keySize);

    return $"PBKDF2${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
}
