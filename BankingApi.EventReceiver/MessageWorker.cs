using System.Text.Json;
using BankingApi.EventReceiver.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BankingApi.EventReceiver
{
    public class MessageWorker
    {
        private readonly IServiceBusReceiver _receiver;
        private readonly IDbContextFactory<BankingApiDbContext> _dbFactory;

        private static readonly int[] RetryBackoffsSeconds = new[] { 5, 25, 125 };

        public MessageWorker(IServiceBusReceiver receiver,
                             IDbContextFactory<BankingApiDbContext> dbFactory)
        {
            _receiver = receiver;
            _dbFactory = dbFactory;
        }

        public async Task Start(CancellationToken ct = default)
        {
            while (!ct.IsCancellationRequested)
            {
                EventMessage? msg = null;

                try
                {
                    msg = await _receiver.Peek();

                    if (msg is null)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), ct);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(msg.MessageBody))
                    {
                        await _receiver.MoveToDeadLetter(msg);
                        continue;
                    }

                    TransactionEvent? payload;
                    try
                    {
                        payload = JsonSerializer.Deserialize<TransactionEvent>(
                            msg.MessageBody,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch
                    {
                        await _receiver.MoveToDeadLetter(msg);
                        continue;
                    }

                    if (payload is null || payload.id == Guid.Empty || payload.bankAccountId == Guid.Empty)
                    {
                        await _receiver.MoveToDeadLetter(msg);
                        continue;
                    }

                    var kind = payload.messageType?.Trim();
                    var isCredit = string.Equals(kind, "Credit", StringComparison.OrdinalIgnoreCase);
                    var isDebit = string.Equals(kind, "Debit", StringComparison.OrdinalIgnoreCase);
                    if (!isCredit && !isDebit)
                    {
                        await _receiver.MoveToDeadLetter(msg);
                        continue;
                    }

                    // start auto lock-renew if the receiver supports it
                    using var renewCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var renewTask = StartLockRenewalIfSupported(_receiver, msg, renewCts.Token);

                    try
                    {
                        await using var db = await _dbFactory.CreateDbContextAsync(ct);
                        await using var tx = await db.Database.BeginTransactionAsync(ct);

                        // Insert-first idempotency (audit)
                        db.Set<TransactionMessage>().Add(new TransactionMessage
                        {
                            Id = payload.id,
                            BankAccountId = payload.bankAccountId,
                            Amount = payload.amount,
                            MessageType = kind!,
                            ProcessedAtUtc = DateTime.UtcNow
                        });

                        try
                        {
                            await db.SaveChangesAsync(ct);
                        }
                        catch (DbUpdateException)
                        {
                            // duplicate MessageId => already processed
                            await _receiver.Complete(msg);
                            await tx.RollbackAsync(ct);
                            continue;
                        }

                        var delta = isCredit ? payload.amount : -payload.amount;

                        var affected = await db.BankAccounts
                            .Where(a => a.Id == payload.bankAccountId)
                            .ExecuteUpdateAsync(setters =>
                                setters.SetProperty(a => a.Balance, a => a.Balance + delta),
                                ct);

                        if (affected == 0)
                        {
                            await tx.RollbackAsync(ct);
                            await _receiver.MoveToDeadLetter(msg);
                            continue;
                        }

                        await tx.CommitAsync(ct);

                        // stop renewing before we complete
                        renewCts.Cancel();
                        if (renewTask is not null) { try { await renewTask; } catch { /* swallow */ } }

                        await _receiver.Complete(msg);
                    }
                    catch (Exception ex) when (TransientDetector.IsTransient(ex))
                    {
                        // stop renewal before rescheduling
                        renewCts.Cancel();
                        if (renewTask is not null) { try { await renewTask; } catch { } }

                        var attempt = Math.Min(msg.ProcessingCount, RetryBackoffsSeconds.Length - 1);
                        var delay = TimeSpan.FromSeconds(RetryBackoffsSeconds[attempt]);
                        await _receiver.ReSchedule(msg, DateTime.UtcNow.Add(delay));
                    }
                    catch
                    {
                        // stop renewal before DLQ
                        renewCts.Cancel();
                        if (renewTask is not null) { try { await renewTask; } catch { } }

                        await _receiver.MoveToDeadLetter(msg);
                    }
                }
                catch
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
        }

        /// <summary>
        /// Starts a background task that periodically renews the message lock until the token is cancelled.
        /// Returns null if the receiver doesn't support lock renewal.
        /// </summary>
        private static Task? StartLockRenewalIfSupported(IServiceBusReceiver receiver, EventMessage msg, CancellationToken ct)
        {
            if (receiver is not IServiceBusReceiverWithLock withLock)
                return null;

            // Renew roughly every third of the lock window, but at least every 5 seconds.
            var period = withLock.DefaultLockDuration;
            if (period <= TimeSpan.Zero) period = TimeSpan.FromSeconds(30);
            var renewEvery = TimeSpan.FromSeconds(Math.Max(5, Math.Floor(period.TotalSeconds / 3)));

            return Task.Run(async () =>
            {
                try
                {
                    // Small initial delay so ultra-fast messages don't renew unnecessarily
                    await Task.Delay(renewEvery, ct);

                    while (!ct.IsCancellationRequested)
                    {
                        await withLock.RenewLock(msg, ct);
                        await Task.Delay(renewEvery, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // normal shutdown
                }
                catch
                {
                    // In production, log renewal failures here. The main flow will still try to complete;
                    // if the lock is already lost, Complete() may fail and the message will be retried per SB policy.
                }
            }, ct);
        }
    }
}
