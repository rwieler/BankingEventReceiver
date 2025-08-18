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

                    await using var db = await _dbFactory.CreateDbContextAsync(ct);
                    await using var tx = await db.Database.BeginTransactionAsync(ct);

                    try
                    {
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
                        await _receiver.Complete(msg);
                    }
                    catch (Exception ex) when (TransientDetector.IsTransient(ex))
                    {
                        var attempt = Math.Min(msg.ProcessingCount, RetryBackoffsSeconds.Length - 1);
                        var delay = TimeSpan.FromSeconds(RetryBackoffsSeconds[attempt]);
                        await _receiver.ReSchedule(msg, DateTime.UtcNow.Add(delay));
                    }
                    catch
                    {
                        await _receiver.MoveToDeadLetter(msg);
                    }
                }
                catch
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
        }
    }
}
