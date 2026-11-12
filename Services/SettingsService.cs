using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TvdiWebGis.Data;
using TvdiWebGis.Data.Entities;

namespace TvdiWebGis.Services;

public class SettingsService(IDbContextFactory<ApplicationDbContext> factory)
{
    private const string DefaultModelKey = "DefaultModel";

    public async Task<string> GetDefaultModelAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return (await db.AppSettings.FindAsync(DefaultModelKey))?.Value ?? "linear";
    }

    public async Task SetDefaultModelAsync(string key)
    {
        await using var db = await factory.CreateDbContextAsync();
        var s = await db.AppSettings.FindAsync(DefaultModelKey);
        if (s is null) db.AppSettings.Add(new AppSetting { Key = DefaultModelKey, Value = key });
        else s.Value = key;
        await db.SaveChangesAsync();
    }
}