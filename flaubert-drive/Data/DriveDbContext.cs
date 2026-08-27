using Microsoft.EntityFrameworkCore;
using Flaubert.Drive.Models;
using Flaubert.Drive.Services;

namespace Flaubert.Drive.Data
{
    public class DriveDbContext : DbContext
    {
        private readonly ITenantProvider _tenantProvider;

        public DriveDbContext(DbContextOptions<DriveDbContext> options, ITenantProvider tenantProvider) 
            : base(options)
        {
            _tenantProvider = tenantProvider;
        }

        public DbSet<FileMetadata> Files { get; set; }
        public DbSet<Folder> Folders { get; set; }
        public DbSet<AuditLog> AuditLogs { get; set; }
        public DbSet<TenantSetting> TenantSettings { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // リクエストヘッダーからテナントIDを取得し、スキーマ名 (app_{tenantId}) を動的指定
            var tenantId = _tenantProvider.GetTenantId();
            var schemaName = $"app_{tenantId}";

            modelBuilder.HasDefaultSchema(schemaName);

            // テナント専用テーブルマッピング
            modelBuilder.Entity<FileMetadata>().ToTable("Files");
            modelBuilder.Entity<Folder>().ToTable("Folders");
            modelBuilder.Entity<AuditLog>().ToTable("AuditLogs");

            // テナント全体共有設定テーブル (public スキーマ)
            modelBuilder.Entity<TenantSetting>(entity =>
            {
                entity.ToTable("TenantSettings", "public");
                entity.HasKey(e => e.TenantId);
            });
        }
    }
}
