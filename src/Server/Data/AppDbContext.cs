using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AngstromCommander.Server.Data;

internal sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<DaemonRegistration> DaemonRegistrations => this.Set<DaemonRegistration>();

    public DbSet<PairingCode> PairingCodes => this.Set<PairingCode>();

    public DbSet<UserSession> Sessions => this.Set<UserSession>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<DaemonRegistration>(registration =>
        {
            registration.HasKey(static r => r.Id);
            registration.HasIndex(static r => r.UserId);
            registration.Property(static r => r.DisplayName).HasMaxLength(200);
            registration.Property(static r => r.PublicKeySpki).HasMaxLength(1000);
            registration.Property(static r => r.Platform).HasMaxLength(200);
            registration
                .HasOne<AppUser>()
                .WithMany()
                .HasForeignKey(static r => r.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PairingCode>(code =>
        {
            code.HasKey(static c => c.Code);
            code.Property(static c => c.Code).HasMaxLength(16);
            code.Property(static c => c.PublicKeySpki).HasMaxLength(1000);
            code.Property(static c => c.Platform).HasMaxLength(200);
        });

        builder.Entity<UserSession>(session =>
        {
            session.HasKey(static s => s.Id);
            // Auth looks tokens up by hash on every PAT request; unique doubles as the
            // (astronomically unlikely) collision guard.
            session.HasIndex(static s => s.TokenHash).IsUnique();
            session.HasIndex(static s => s.UserId);
            session.Property(static s => s.Kind).HasMaxLength(20);
            session.Property(static s => s.Name).HasMaxLength(200);
            session.Property(static s => s.TokenHash).HasMaxLength(64);
            session.Property(static s => s.Scopes).HasMaxLength(500);
            session
                .HasOne<AppUser>()
                .WithMany()
                .HasForeignKey(static s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
