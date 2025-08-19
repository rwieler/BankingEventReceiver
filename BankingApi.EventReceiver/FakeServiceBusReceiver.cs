using System.Collections.Concurrent;

namespace BankingApi.EventReceiver
{
    public class FakeServiceBusReceiver : IServiceBusReceiverWithLock
    {
        private readonly ConcurrentQueue<EventMessage> _queue = new();
        private readonly List<(EventMessage msg, DateTime notBefore)> _scheduled = new();

        public TimeSpan DefaultLockDuration => TimeSpan.FromSeconds(30);
        public Task RenewLock(EventMessage message, CancellationToken ct) => Task.CompletedTask;

        public void Enqueue(params EventMessage[] messages)
        {
            foreach (var m in messages) _queue.Enqueue(m);
        }

        public Task<EventMessage?> Peek()
        {
            // Move any scheduled items that are ready back to the queue
            var now = DateTime.UtcNow;
            var ready = _scheduled.Where(s => s.notBefore <= now).ToList();
            foreach (var r in ready)
            {
                _queue.Enqueue(r.msg);
                _scheduled.Remove(r);
            }

            if (_queue.TryDequeue(out var msg))
            {
                return Task.FromResult<EventMessage?>(msg);
            }
            return Task.FromResult<EventMessage?>(null);
        }

        public Task Abandon(EventMessage message)
        {
            message.ProcessingCount++;
            _queue.Enqueue(message);
            return Task.CompletedTask;
        }

        public Task Complete(EventMessage message) => Task.CompletedTask;

        public Task ReSchedule(EventMessage message, DateTime nextAvailableTime)
        {
            message.ProcessingCount++;
            _scheduled.Add((message, nextAvailableTime));
            return Task.CompletedTask;
        }

        public Task MoveToDeadLetter(EventMessage message) => Task.CompletedTask;
    }
}