using Conductor.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Linq.Expressions;

namespace Conductor.Db
{
    public class AppDb : DbContext
    {
        public AppDb(DbContextOptions<AppDb> options) : base(options) { }

        public DbSet<AppUser> Users => Set<AppUser>();
        public DbSet<Site> Sites => Set<Site>();
        public DbSet<Session> Sessions => Set<Session>();
        public DbSet<Submission> Submissions => Set<Submission>();
        public DbSet<NextStep> NextSteps => Set<NextStep>();
        public DbSet<Event> Events => Set<Event>();
        public DbSet<UserSiteAssignment> UserSiteAssignments => Set<UserSiteAssignment>();

        protected override void OnModelCreating(ModelBuilder b)
        {
            // Converters using static helper methods (no optional args, no null-prop)
            Expression<Func<JsonDocument, string>> jdToString = v => EfHelpers.JsonDocToString(v);
            Expression<Func<string, JsonDocument>> stringToJd = v => EfHelpers.StringToJsonDoc(v);
            var jsonDocConverter = new ValueConverter<JsonDocument, string>(jdToString, stringToJd);
            var jsonDocComparer = new ValueComparer<JsonDocument>(
                (a, c) => EfHelpers.JsonEquals(a, c),
                v => EfHelpers.JsonHash(v),
                v => EfHelpers.JsonSnapshot(v));

            Expression<Func<List<string>, string>> listToJsonExp = v => EfHelpers.ListToJson(v);
            Expression<Func<string, List<string>>> jsonToListExp = v => EfHelpers.JsonToList(v);
            var listToJson = new ValueConverter<List<string>, string>(listToJsonExp, jsonToListExp);
            var listComparer = new ValueComparer<List<string>>(
                (a, c) => EfHelpers.ListEquals(a, c),
                v => EfHelpers.ListHash(v),
                v => EfHelpers.ListSnapshot(v));

            b.Entity<AppUser>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                e.Property(x => x.User).IsRequired();
                e.Property(x => x.Role).IsRequired();
                e.Property(x => x.PasswordHash).IsRequired();
                e.HasIndex(x => x.User).IsUnique();
            });

            b.Entity<Site>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                e.Property(x => x.Origin).IsRequired();
                e.Property(x => x.ManualRoutingEnabled).HasDefaultValue(false);
                e.Property(x => x.IsActive).HasDefaultValue(true);
                // Normalise site name casing and enforce uniqueness
                e.Property(x => x.Name)
                    .HasConversion(v => v.ToLower(), v => v) // store lower-case
                    .IsRequired();
                e.HasIndex(x => x.Name).IsUnique();
                e.Property(x => x.Pages).HasConversion(listToJson).Metadata.SetValueComparer(listComparer);
                e.Property(s => s.Type)
                    .HasConversion<string>()
                    .HasMaxLength(32)
                    .HasDefaultValue(SiteType.Unknown);
                e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            });

            b.Entity<UserSiteAssignment>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.AssignedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                e.HasIndex(x => new { x.UserId, x.SiteId }).IsUnique();
                
                e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne(x => x.AssignedBy).WithMany().HasForeignKey(x => x.AssignedByUserId).OnDelete(DeleteBehavior.Restrict);
            });

            b.Entity<Session>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.StartedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                e.Property(x => x.LastSeenAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                e.Property(x => x.IsActive).HasDefaultValue(true);
                e.Property(x => x.Ip).HasConversion(
                    v => v == null ? "" : v.ToString(),
                    v => string.IsNullOrWhiteSpace(v) ? IPAddress.None : IPAddress.Parse(v));
                e.HasIndex(x => x.UserId);
                e.HasIndex(x => x.Ip).IsUnique().HasFilter("IsActive = 1");
            });

            b.Entity<Submission>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                e.Property(x => x.UseDefaultFlow).HasDefaultValue(false);
                e.Property(x => x.Payload).HasConversion(jsonDocConverter).Metadata.SetValueComparer(jsonDocComparer);
                e.HasIndex(x => new { x.SiteId, x.CreatedAt });
                e.HasIndex(x => new { x.SessionId, x.CreatedAt });
            });

            b.Entity<NextStep>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                e.Property(x => x.Status).HasDefaultValue("pending");
                e.HasIndex(x => new { x.SiteId, x.Status, x.CreatedAt });
                e.HasIndex(x => x.SubmissionId).IsUnique();
                e.HasIndex(x => new { x.SessionId, x.PageId, x.Status }).HasFilter("Status = 'pending'");
                e.HasOne<Submission>().WithMany().HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            });

            b.Entity<Event>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.Meta).HasConversion(jsonDocConverter).Metadata.SetValueComparer(jsonDocComparer);
                e.Property(x => x.Ts).HasDefaultValueSql("CURRENT_TIMESTAMP");
            });
        }
    }

    internal static class EfHelpers
    {
        public static string JsonDocToString(JsonDocument v)
            => v == null ? "{}" : v.RootElement.GetRawText();

        public static JsonDocument StringToJsonDoc(string v)
            => string.IsNullOrWhiteSpace(v)
                ? JsonDocument.Parse("{}", new JsonDocumentOptions())
                : JsonDocument.Parse(v, new JsonDocumentOptions());

        public static bool JsonEquals(JsonDocument a, JsonDocument c)
        {
            if (a == null && c == null) return true;
            if (a == null || c == null) return false;
            return a.RootElement.GetRawText() == c.RootElement.GetRawText();
        }

        public static int JsonHash(JsonDocument v)
            => v == null ? 0 : v.RootElement.GetRawText().GetHashCode();

        public static JsonDocument JsonSnapshot(JsonDocument v)
            => v == null ? null : JsonDocument.Parse(v.RootElement.GetRawText(), new JsonDocumentOptions());

        public static string ListToJson(List<string> v)
            => JsonSerializer.Serialize(v ?? new List<string>(), new JsonSerializerOptions());

        public static List<string> JsonToList(string v)
            => string.IsNullOrWhiteSpace(v)
                ? new List<string>()
                : (JsonSerializer.Deserialize<List<string>>(v, new JsonSerializerOptions()) ?? new List<string>());

        public static bool ListEquals(List<string> a, List<string> c)
        {
            if (ReferenceEquals(a, c)) return true;
            if (a == null || c == null) return false;
            return a.SequenceEqual(c);
        }

        public static int ListHash(List<string> v)
        {
            if (v == null) return 0;
            var h = 0;
            for (int i = 0; i < v.Count; i++)
            {
                var s = v[i];
                h = System.HashCode.Combine(h, s == null ? 0 : s.GetHashCode());
            }
            return h;
        }

        public static List<string> ListSnapshot(List<string> v)
            => v == null ? new List<string>() : new List<string>(v);
    }
}
