using Microsoft.EntityFrameworkCore;
using Flaubert.Drive.Models;
using Flaubert.Drive.Services;

namespace Flaubert.Drive.Data
{
    public class DriveDbContext : DbContext
    {
        private readonly ITenantProvider _tenantProvider;
        public DriveDbContext(DbContextOptions<DriveDbContext> options, ITenantProvider tenantProvider) : base(options) => _tenantProvider = tenantProvider;
        public DbSet<FileMetadata> Files { get; set; } = null!;
        public DbSet<Folder> Folders { get; set; } = null!;
        public DbSet<AuditLog> AuditLogs { get; set; } = null!;
        public DbSet<TenantSetting> TenantSettings { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            var tenantId = _tenantProvider.GetTenantId();
            modelBuilder.HasDefaultSchema($"app_{tenantId}");
            modelBuilder.Entity<FileMetadata>(e =>
            {
                e.ToTable("Files"); e.HasKey(x => x.Id); e.Property(x => x.StorageKey).HasColumnName("StorageKey");
                e.HasIndex(x => new { x.TenantId, x.FolderId }); e.HasIndex(x => new { x.TenantId, x.StorageKey }).IsUnique();
                e.HasOne<Folder>().WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.SetNull);
            });
            modelBuilder.Entity<Folder>(e =>
            {
                e.ToTable("Folders"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.TenantId, x.ParentId });
            });
            modelBuilder.Entity<AuditLog>().ToTable("AuditLogs");
            modelBuilder.Entity<TenantSetting>(e => { e.ToTable("TenantSettings", "public"); e.HasKey(x => x.TenantId); });
        }
    }
}
