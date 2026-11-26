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

        var email = cfg["Seed:AdminEmail"];
        var password = cfg["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(password)) return;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Configure both Seed:AdminEmail and Seed:AdminPassword.");
        if (await users.FindByEmailAsync(email) is null)
        {
            var admin = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            var res = await users.CreateAsync(admin, password);
            if (!res.Succeeded)
                throw new InvalidOperationException("Admin creation failed. Check the Identity password policy.");
            var assigned = await users.AddToRoleAsync(admin, "Admin");
            if (!assigned.Succeeded)
            {
                await users.DeleteAsync(admin);
                throw new InvalidOperationException("Could not assign the seeded Admin role.");
            }
        }
    }
}