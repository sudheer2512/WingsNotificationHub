using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Wings.NotificationHub.Infrastructure.Data;

/// <summary>
/// Used by `dotnet ef` to create the SQL Server migrations (Data/Migrations) and scripts in database/sqlserver.
/// SQLite development databases are created from the model directly (EnsureCreated) and never use these migrations.
/// </summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<NotificationHubDbContext>
{
    public NotificationHubDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NotificationHubDbContext>()
            .UseSqlServer("Server=localhost;Database=WingsNotificationHub;Trusted_Connection=True;TrustServerCertificate=True");
        return new NotificationHubDbContext(options.Options);
    }
}
