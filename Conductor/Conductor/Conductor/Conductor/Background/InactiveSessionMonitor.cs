using Conductor.Db;
using Conductor.RealTime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conductor.Background
{
    /// <summary>
    /// Periodically scans for active sessions whose LastSeenAt timestamp is older than the
    /// configured threshold and marks them inactive.  Notifies all dashboards in real-time
    /// so the UI updates without a page refresh.
    /// </summary>
    public class InactiveSessionMonitor : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHubContext<DashboardHub> _hub;
        private readonly ILogger<InactiveSessionMonitor> _logger;

        private readonly TimeSpan _scanInterval = TimeSpan.FromSeconds(30); // keep in sync with client heartbeat
        private readonly TimeSpan _inactiveThreshold = TimeSpan.FromSeconds(90); // 3 × heartbeat

        public InactiveSessionMonitor(IServiceScopeFactory scopeFactory,
                                      IHubContext<DashboardHub> hub,
                                      ILogger<InactiveSessionMonitor> logger)
        {
            _scopeFactory = scopeFactory;
            _hub = hub;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("InactiveSessionMonitor started – scanning every {Interval}s", _scanInterval.TotalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDb>();

                    var cutoff = DateTimeOffset.UtcNow - _inactiveThreshold;

                    // SQLite cannot translate DateTimeOffset comparison; pull to memory first.
                    var staleSessions = (await db.Sessions.Where(s => s.IsActive).ToListAsync(stoppingToken))
                                             .Where(s => s.LastSeenAt < cutoff)
                                             .ToList();

                    if (staleSessions.Count > 0)
                    {
                        _logger.LogInformation("Marking {Count} sessions inactive (timeout exceeded)", staleSessions.Count);

                        foreach (var s in staleSessions)
                        {
                            s.IsActive = false;
                        }
                        await db.SaveChangesAsync(stoppingToken);

                        // Broadcast updates to all dashboards so they refresh immediately
                        foreach (var s in staleSessions)
                        {
                            await _hub.Clients.Group("dashboard").SendAsync("sessionUpdated", new
                            {
                                id = s.Id,
                                sessionId = s.Id,
                                isActive = false,
                                lastSeenAt = s.LastSeenAt,
                                status = "inactive"
                            }, cancellationToken: stoppingToken);
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    // Expected on shutdown – ignore
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in InactiveSessionMonitor loop");
                }

                await Task.Delay(_scanInterval, stoppingToken);
            }
        }
    }
}
