using BankingApi.EventReceiver;
using Microsoft.EntityFrameworkCore;

Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        var cs = ctx.Configuration.GetConnectionString("BankingDb");

        // Use a factory for DbContext in long-running background workers
        services.AddDbContextFactory<BankingApiDbContext>(opt => opt.UseSqlServer(cs));

        // replace with your real Azure Service Bus impl in prod
        services.AddSingleton<IServiceBusReceiver, FakeServiceBusReceiver>();

        services.AddHostedService<MessageWorkerHostedService>();
    })
    .Build()
    .Run();