using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AngstromCommander.Server.Data;

internal sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<DaemonRegistration> DaemonRegistrations => this.Set<DaemonRegistration>();

    public DbSet<PairingCode> PairingCodes => this.Set<PairingCode>();

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
    }
}
