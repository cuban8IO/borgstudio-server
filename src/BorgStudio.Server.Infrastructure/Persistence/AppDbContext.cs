using BorgStudio.Server.Domain.Settings;
using BorgStudio.Server.Domain.Storage;
using BorgStudio.Server.Domain.Users;
using BorgStudio.Server.Infrastructure.Identity;

using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BorgStudio.Server.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<ServerSettings> ServerSettings => Set<ServerSettings>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<Datastore> Datastores => Set<Datastore>();

    public DbSet<BackupRepository> Repositories => Set<BackupRepository>();

    public DbSet<UserStoragePolicy> StoragePolicies => Set<UserStoragePolicy>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(u => u.PendingTasks).HasConversion<int>();
            user.Property(u => u.ThemeMode).HasConversion<int>();
            user.Property(u => u.ChangelogSeenVersion).HasMaxLength(32);
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

        builder.Entity<Datastore>(datastore =>
        {
            datastore.ToTable("Datastores");
            datastore.HasKey(d => d.Id);
            datastore.Property(d => d.Name).HasMaxLength(Datastore.MaxNameLength).IsRequired();
            datastore.HasIndex(d => d.Name).IsUnique();
            datastore.Property(d => d.Path).HasMaxLength(1024).IsRequired();
            datastore.HasIndex(d => d.Path).IsUnique();
        });

        builder.Entity<BackupRepository>(repository =>
        {
            repository.ToTable("Repositories");
            repository.HasKey(r => r.Id);
            repository.Property(r => r.Name).HasMaxLength(RepositoryNames.MaxLength).IsRequired();
            repository.Property(r => r.FormerOwnerName).HasMaxLength(256);
            repository.Property(r => r.RelativePath).HasMaxLength(512).IsRequired();
            repository.Property(r => r.MoveError).HasMaxLength(1024);
            repository.HasIndex(r => r.OwnerId);
            // Owners are handled before a user is deleted (their repositories go to the trash).
            repository.HasOne<ApplicationUser>().WithMany().HasForeignKey(r => r.OwnerId).OnDelete(DeleteBehavior.SetNull);
            repository.HasOne<Datastore>().WithMany().HasForeignKey(r => r.DatastoreId).OnDelete(DeleteBehavior.Restrict);
            repository.HasMany(r => r.Shares).WithOne().HasForeignKey(s => s.RepositoryId).OnDelete(DeleteBehavior.Cascade);
            repository.Navigation(r => r.Shares).HasField("shares").UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        builder.Entity<RepositoryShare>(share =>
        {
            share.ToTable("RepositoryShares");
            share.HasKey(s => new { s.RepositoryId, s.UserId });
            share.Property(s => s.Access).HasConversion<int>();
            share.HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UserStoragePolicy>(policy =>
        {
            policy.ToTable("UserStoragePolicies");
            policy.HasKey(p => p.UserId);
            policy.HasOne<ApplicationUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
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
                CheckForUpdates = true,
            });
        });
    }
}
