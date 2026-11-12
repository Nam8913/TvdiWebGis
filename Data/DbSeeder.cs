using Microsoft.AspNetCore.Identity;

namespace TvdiWebGis.Data;

public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider sp, IConfiguration cfg)
    {
        var roles = sp.GetRequiredService<RoleManager<IdentityRole>>();
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();

        foreach (var r in new[] { "Admin", "User" })
            if (!await roles.RoleExistsAsync(r)) await roles.CreateAsync(new IdentityRole(r));

        var email = cfg["Seed:AdminEmail"]!;
        if (await users.FindByEmailAsync(email) is null)
        {
            var admin = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            var res = await users.CreateAsync(admin, cfg["Seed:AdminPassword"]!);
            if (res.Succeeded) await users.AddToRoleAsync(admin, "Admin");
        }
    }
}