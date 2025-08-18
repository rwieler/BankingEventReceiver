using Microsoft.EntityFrameworkCore;

namespace BankingApi.EventReceiver
{
    public class BankingApiDbContext : DbContext
    {
        public BankingApiDbContext(DbContextOptions<BankingApiDbContext> options)
            : base(options) { }

        public DbSet<BankAccount> BankAccounts { get; set; }
        public DbSet<TransactionMessage> TransactionMessages { get; set; }

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
