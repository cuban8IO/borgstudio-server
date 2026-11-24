using BorgStudio.Server.Domain.Settings;
using BorgStudio.Server.Domain.Users;
using BorgStudio.Server.Infrastructure.Identity;

using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BorgStudio.Server.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<ServerSettings> ServerSettings => Set<ServerSettings>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(u => u.PendingTasks).HasConversion<int>();
            user.Property(u => u.ThemeMode).HasConversion<int>();
        });

        builder.Entity<Invitation>(invitation =>
        {
            invitation.ToTable("Invitations");
            invitation.HasKey(i => i.Id);
            invitation.Property(i => i.UserId).IsRequired();
            invitation.Property(i => i.TokenHash).HasMaxLength(64).IsRequired();
            invitation.HasIndex(i => i.TokenHash).IsUnique();
            // One open invitation per user; it goes away with the user.
            invitation.HasIndex(i => i.UserId).IsUnique();
            invitation.HasOne<ApplicationUser>().WithMany().HasForeignKey(i => i.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ServerSettings>(settings =>
        {
            settings.ToTable("ServerSettings");
            settings.HasKey(s => s.Id);
            settings.Property(s => s.Id).ValueGeneratedNever();
            settings.Property(s => s.ServerName).HasMaxLength(Domain.Settings.ServerSettings.MaxServerNameLength).IsRequired();
            settings.Property(s => s.PublicUrl).HasMaxLength(Domain.Settings.ServerSettings.MaxPublicUrlLength);
            settings.HasData(new
            {
                Id = Domain.Settings.ServerSettings.SingletonId,
                ServerName = Domain.Settings.ServerSettings.DefaultServerName,
            });
        });
    }
}
