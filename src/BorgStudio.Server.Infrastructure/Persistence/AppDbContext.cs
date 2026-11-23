using BorgStudio.Server.Domain.Settings;
using BorgStudio.Server.Infrastructure.Identity;

using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BorgStudio.Server.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<ServerSettings> ServerSettings => Set<ServerSettings>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(u => u.PendingTasks).HasConversion<int>();
            user.Property(u => u.ThemeMode).HasConversion<int>();
        });

        builder.Entity<ServerSettings>(settings =>
        {
            settings.ToTable("ServerSettings");
            settings.HasKey(s => s.Id);
            settings.Property(s => s.Id).ValueGeneratedNever();
            settings.Property(s => s.ServerName).HasMaxLength(Domain.Settings.ServerSettings.MaxServerNameLength).IsRequired();
            settings.HasData(new
            {
                Id = Domain.Settings.ServerSettings.SingletonId,
                ServerName = Domain.Settings.ServerSettings.DefaultServerName,
            });
        });
    }
}
