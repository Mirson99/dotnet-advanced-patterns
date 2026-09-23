using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Concurenncy.LostUpdate;

/// <summary>
/// The lab's EF Core context. Reads its connection string from appsettings.json
/// so that both the app and the dotnet-ef tooling resolve the same database.
/// </summary>
public sealed class LabDbContext : DbContext
{
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();

    public static string ConnectionString
    {
        get
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .Build();

            return configuration.GetConnectionString("Database")
                ?? throw new InvalidOperationException(
                    "ConnectionStrings:Database is missing from appsettings.json.");
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlServer(ConnectionString);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BankAccount>(account =>
        {
            account.HasKey(a => a.Id);
            account.Property(a => a.Amount).IsRequired();
            account.Property(a => a.RowVersion)
                .IsRowVersion();

            // The single account the lost-update scenarios contend over.
            account.HasData(new BankAccount { Id = 1, Amount = 100 });
        });
    }
}
