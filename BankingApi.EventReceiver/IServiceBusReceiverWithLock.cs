namespace BankingApi.EventReceiver
{
    public interface IServiceBusReceiverWithLock : IServiceBusReceiver
    {
        TimeSpan DefaultLockDuration { get; }

        Task RenewLock(EventMessage message, CancellationToken ct);
    }
}
