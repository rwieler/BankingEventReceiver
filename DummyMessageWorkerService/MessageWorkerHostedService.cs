using System;
using System.Threading;
using System.Threading.Tasks;
using BankingApi.EventReceiver;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

public sealed class MessageWorkerHostedService : BackgroundService
{
    private readonly IServiceBusReceiver _receiver;
    private readonly IDbContextFactory<BankingApiDbContext> _dbFactory;
    private readonly MessageWorker _worker;

    public MessageWorkerHostedService(
        IServiceBusReceiver receiver,
        IDbContextFactory<BankingApiDbContext> dbFactory)
    {
        _receiver = receiver;
        _dbFactory = dbFactory;
        _worker = new MessageWorker(receiver, dbFactory);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ensure DB is up-to-date and seed a demo account
        await using (var db = await _dbFactory.CreateDbContextAsync(stoppingToken))
        {
            await db.Database.MigrateAsync(stoppingToken);

            var acctId = Guid.Parse("7d445724-24ec-4d52-aa7a-ff2bac9f191d");
            if (!await db.BankAccounts.AnyAsync(a => a.Id == acctId, stoppingToken))
            {
                db.BankAccounts.Add(new BankAccount { Id = acctId, Balance = 100m });
                await db.SaveChangesAsync(stoppingToken);
            }
        }

        // If running with FakeServiceBusReceiver, enqueue sample messages
        if (_receiver is FakeServiceBusReceiver fake)
        {
            fake.Enqueue(
                new EventMessage
                {
                    Id = Guid.NewGuid(),
                    MessageBody = """
                    {"id":"11111111-1111-1111-1111-111111111111","messageType":"Credit","bankAccountId":"7d445724-24ec-4d52-aa7a-ff2bac9f191d","amount":50.00}
                    """,
                    ProcessingCount = 0
                },
                new EventMessage
                {
                    Id = Guid.NewGuid(),
                    MessageBody = """
                    {"id":"22222222-2222-2222-2222-222222222222","messageType":"Debit","bankAccountId":"7d445724-24ec-4d52-aa7a-ff2bac9f191d","amount":20.00}
                    """,
                    ProcessingCount = 0
                },
                // Unknown type -> deadletter
                new EventMessage
                {
                    Id = Guid.NewGuid(),
                    MessageBody = """
                    {"id":"33333333-3333-3333-3333-333333333333","messageType":"Foo","bankAccountId":"7d445724-24ec-4d52-aa7a-ff2bac9f191d","amount":10.00}
                    """,
                    ProcessingCount = 0
                },
                // Nonexistent account -> deadletter
                new EventMessage
                {
                    Id = Guid.NewGuid(),
                    MessageBody = """
                    {"id":"44444444-4444-4444-4444-444444444444","messageType":"Credit","bankAccountId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","amount":15.00}
                    """,
                    ProcessingCount = 0
                }
            );
        }

        // Hand off to your worker loop
        await _worker.Start(stoppingToken);
    }
}