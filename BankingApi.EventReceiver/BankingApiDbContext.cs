using Microsoft.EntityFrameworkCore;

namespace BankingApi.EventReceiver
{
    public class BankingApiDbContext : DbContext
    {
        public DbSet<BankAccount> BankAccounts { get; set; }
        public DbSet<TransactionMessage> TransactionMessages { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => //options.UseSqlServer("Data Source=.\\SQLEXPRESS;Initial Catalog=BankingApiTest;Integrated Security=True;TrustServerCertificate=True;");
            options.UseSqlServer(
  "Server=(localdb)\\MSSQLLocalDB;Database=BankingApiTest;Trusted_Connection=True;TrustServerCertificate=True;");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TransactionMessage>()
                .HasKey(x => x.Id);

            modelBuilder.Entity<TransactionMessage>()
                .HasOne(x => x.BankAccount)
                .WithMany()
                .HasForeignKey(x => x.BankAccountId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
