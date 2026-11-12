using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TvdiWebGis.Data.Entities;

namespace TvdiWebGis.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<RasterFile> RasterFiles => Set<RasterFile>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<ForecastResult> ForecastResults => Set<ForecastResult>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<RasterFile>(e =>
        {
            e.Property(x => x.Data).HasColumnType("varbinary(max)");
            e.HasIndex(x => x.Period);
        });
        b.Entity<AppSetting>().HasKey(x => x.Key);
        b.Entity<ForecastResult>(e =>
        {
            e.Property(x => x.Data).HasColumnType("varbinary(max)");
            e.HasIndex(x => new { x.ModelKey, x.Signature, x.Step }).IsUnique();
        });
    }
}