using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace BankingApi.EventReceiver;

public sealed class BankingApiDbContextFactory : IDesignTimeDbContextFactory<BankingApiDbContext>
{
    public BankingApiDbContext CreateDbContext(string[] args)
    {
        // Determine environment (Development, Production, etc.)
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                  ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                  ?? "Production";

        // Build configuration from appsettings in the *current working directory*
        // When you run dotnet-ef with --startup-project, this directory is the startup project.
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{env}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()                // supports ConnectionStrings__BankingDb
            .AddCommandLine(args ?? Array.Empty<string>()) // supports --connection=
            .Build();

        var cs = ResolveConnectionString(config, args);

        var options = new DbContextOptionsBuilder<BankingApiDbContext>()
            .UseSqlServer(cs)
            .Options;

        return new BankingApiDbContext(options);
    }

    private static string ResolveConnectionString(IConfiguration config, string[]? args)
    {
        // 1) Standard connection string key in any loaded appsettings
        var cs = config.GetConnectionString("BankingDb") ?? config["ConnectionStrings:BankingDb"];

        // 2) CLI override:  dotnet ef ... -- --connection="..."
        if (string.IsNullOrWhiteSpace(cs) && args is not null)
        {
            const string key = "--connection=";
            var match = args.FirstOrDefault(a => a.StartsWith(key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
                cs = match.Substring(key.Length).Trim('"');
        }

        // 3) Env-var aliases
        cs ??= Environment.GetEnvironmentVariable("ConnectionStrings__BankingDb");
        cs ??= Environment.GetEnvironmentVariable("BANKINGDB_CONNECTION");

        // 4) Safe local default (LocalDB) if nothing else provided
        cs ??= @"Server=(localdb)\MSSQLLocalDB;Database=BankingApiTest;Trusted_Connection=True;TrustServerCertificate=True;";

        return cs;
    }
}
