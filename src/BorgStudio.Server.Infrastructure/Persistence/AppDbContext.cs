using BorgStudio.Server.Domain.Access;
using BorgStudio.Server.Domain.Settings;
using BorgStudio.Server.Domain.Statistics;
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

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<PairingRequest> PairingRequests => Set<PairingRequest>();

    public DbSet<SshKey> SshKeys => Set<SshKey>();

    public DbSet<RepositorySession> RepositorySessions => Set<RepositorySession>();

    public DbSet<ConnectionRecord> ConnectionRecords => Set<ConnectionRecord>();

    public DbSet<AppReport> AppReports => Set<AppReport>();

    public DbSet<StorageSample> StorageSamples => Set<StorageSample>();

    public DbSet<SystemSample> SystemSamples => Set<SystemSample>();

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
            datastore.Property(d => d.Kind).HasConversion<int>();
            datastore.Property(d => d.Host).HasMaxLength(255);
            datastore.Property(d => d.UserName).HasMaxLength(64);
            datastore.Property(d => d.RemoteBorg).HasMaxLength(128);
            datastore.Property(d => d.HostKey).HasMaxLength(4096);
            datastore.Property(d => d.LastError).HasMaxLength(1000);
        });

        builder.Entity<BackupRepository>(repository =>
        {
            repository.ToTable("Repositories");
            repository.HasKey(r => r.Id);
            repository.Property(r => r.Name).HasMaxLength(RepositoryNames.MaxLength).IsRequired();
            repository.Property(r => r.FormerOwnerName).HasMaxLength(256);
            repository.Property(r => r.RelativePath).HasMaxLength(512).IsRequired();
            repository.Property(r => r.MoveError).HasMaxLength(1024);
            repository.Property(r => r.LastAccessUserId).HasMaxLength(450);
            repository.Property(r => r.LastAccessBy).HasMaxLength(100);
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

        builder.Entity<Device>(device =>
        {
            device.ToTable("Devices");
            device.HasKey(d => d.Id);
            device.Property(d => d.Kind).HasConversion<int>();
            device.Property(d => d.Name).HasMaxLength(Device.MaxNameLength).IsRequired();
            device.Property(d => d.Platform).HasMaxLength(100);
            device.Property(d => d.AppVersion).HasMaxLength(50);
            device.Property(d => d.TokenHash).HasMaxLength(64).IsRequired();
            device.HasIndex(d => d.TokenHash).IsUnique();
            device.HasIndex(d => d.UserId);
            device.HasOne<ApplicationUser>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PairingRequest>(pairing =>
        {
            pairing.ToTable("PairingRequests");
            pairing.HasKey(p => p.Id);
            pairing.Property(p => p.PairingCodeHash).HasMaxLength(64).IsRequired();
            pairing.HasIndex(p => p.PairingCodeHash).IsUnique();
            pairing.Property(p => p.UserCode).HasMaxLength(9).IsRequired();
            pairing.HasIndex(p => p.UserCode).IsUnique();
            pairing.Property(p => p.DeviceName).HasMaxLength(Device.MaxNameLength).IsRequired();
            pairing.Property(p => p.Platform).HasMaxLength(100);
            pairing.Property(p => p.AppVersion).HasMaxLength(50);
            pairing.HasOne<ApplicationUser>().WithMany().HasForeignKey(p => p.ApprovedUserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SshKey>(key =>
        {
            key.ToTable("SshKeys");
            key.HasKey(k => k.Id);
            key.Property(k => k.Name).HasMaxLength(SshKey.MaxNameLength).IsRequired();
            key.Property(k => k.Algorithm).HasMaxLength(64).IsRequired();
            key.Property(k => k.Blob).HasMaxLength(4096).IsRequired();
            key.Property(k => k.Fingerprint).HasMaxLength(64).IsRequired();
            // One key opens one repository: the server tells repositories apart by the key.
            key.HasIndex(k => k.Fingerprint).IsUnique();
            key.HasIndex(k => k.RepositoryId);
            key.HasOne<BackupRepository>().WithMany().HasForeignKey(k => k.RepositoryId).OnDelete(DeleteBehavior.Cascade);
            key.HasOne<ApplicationUser>().WithMany().HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);
            key.HasOne<Device>().WithMany().HasForeignKey(k => k.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RepositorySession>(session =>
        {
            session.ToTable("RepositorySessions");
            session.HasKey(s => s.Id);
            session.HasIndex(s => s.RepositoryId);
            session.Property(s => s.UserId).HasMaxLength(450);
            session.Property(s => s.By).HasMaxLength(100);
            session.HasOne<BackupRepository>().WithMany().HasForeignKey(s => s.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        });

        // Statistics: times as Unix milliseconds, so SQLite can compare and sort them in queries.
        var unixMilliseconds = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
            v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));

        builder.Entity<ConnectionRecord>(record =>
        {
            record.ToTable("ConnectionRecords");
            record.HasKey(r => r.Id);
            record.Property(r => r.UserId).HasMaxLength(450).IsRequired();
            record.Property(r => r.By).HasMaxLength(100).IsRequired();
            record.Property(r => r.StartedAt).HasConversion(unixMilliseconds);
            record.Property(r => r.EndedAt).HasConversion(unixMilliseconds);
            record.HasIndex(r => new { r.RepositoryId, r.StartedAt });
            record.HasIndex(r => r.StartedAt);
        });

        builder.Entity<AppReport>(report =>
        {
            report.ToTable("AppReports");
            report.HasKey(r => r.Id);
            report.Property(r => r.UserId).HasMaxLength(450).IsRequired();
            report.Property(r => r.By).HasMaxLength(100);
            report.Property(r => r.Kind).HasConversion<int>();
            report.Property(r => r.Outcome).HasConversion<int>();
            report.Property(r => r.ArchiveName).HasMaxLength(200);
            report.Property(r => r.Message).HasMaxLength(2000);
            report.Property(r => r.StartedAt).HasConversion(unixMilliseconds);
            report.Property(r => r.ReceivedAt).HasConversion(unixMilliseconds);
            report.HasIndex(r => new { r.RepositoryId, r.StartedAt });
        });

        builder.Entity<StorageSample>(sample =>
        {
            sample.ToTable("StorageSamples");
            sample.HasKey(s => s.Id);
            sample.Property(s => s.Subject).HasConversion<int>();
            sample.HasIndex(s => new { s.Subject, s.SubjectId, s.Day }).IsUnique();
        });

        builder.Entity<SystemSample>(sample =>
        {
            sample.ToTable("SystemSamples");
            sample.HasKey(s => s.Id);
            sample.Property(s => s.Resolution).HasConversion<int>();
            sample.Property(s => s.At).HasConversion(unixMilliseconds);
            sample.HasIndex(s => new { s.Resolution, s.At });
        });

        builder.Entity<ServerSettings>(settings =>
        {
            settings.ToTable("ServerSettings");
            settings.HasKey(s => s.Id);
            settings.Property(s => s.Id).ValueGeneratedNever();
            settings.Property(s => s.ServerName).HasMaxLength(Domain.Settings.ServerSettings.MaxServerNameLength).IsRequired();
            settings.Property(s => s.PublicUrl).HasMaxLength(Domain.Settings.ServerSettings.MaxPublicUrlLength);
            settings.Property(s => s.SshPublicAddress).HasMaxLength(Domain.Settings.ServerSettings.MaxSshAddressLength);
            settings.HasData(new
            {
                Id = Domain.Settings.ServerSettings.SingletonId,
                ServerName = Domain.Settings.ServerSettings.DefaultServerName,
                CheckForUpdates = true,
                ShowParticipantsInApps = true,
            });
        });
    }
}
