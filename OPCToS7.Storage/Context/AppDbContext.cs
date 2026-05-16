using Microsoft.EntityFrameworkCore;
using OPCToS7.Storage.Entities;

namespace OPCToS7.Storage.Context;

public class AppDbContext : DbContext
{
    public DbSet<PlcConfig> PlcConfigs { get; set; }
    public DbSet<OpcConfig> OpcConfigs { get; set; }
    public DbSet<TagMapEntity> TagMapEntities { get; set; }

    public AppDbContext()
    {
        // EnsureCreated() 会检查数据库是否存在，若不存在则直接创建文件和所有表
        this.Database.EnsureCreated();
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // 数据库文件将存放在程序运行根目录下的 config.db 中
        optionsBuilder.UseSqlite("Data Source=config.db");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 建立复合索引优化运行时加载性能
        modelBuilder.Entity<TagMapEntity>()
            .HasIndex(t => new { t.DbNumber, t.ByteOffset });
    }
}