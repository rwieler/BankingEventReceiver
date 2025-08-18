using System.Text.Json;
using BankingApi.EventReceiver.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BankingApi.EventReceiver
{
    public class MessageWorker
    {
        private readonly IServiceBusReceiver _receiver;

        // exponential delays in seconds for transient failures
        private static readonly int[] RetryBackoffsSeconds = new[] { 5, 25, 125 };

        public MessageWorker(IServiceBusReceiver serviceBusReceiver)
        {
            _receiver = serviceBusReceiver;
        }

        public async Task Start()
        {
            // In a real service you’d pass a CancellationToken; the test harness can keep this simple loop.
            while (true)
            {
                EventMessage? msg = null;

                try
                {
                    msg = await _receiver.Peek();

                    if (msg is null)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10));
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(msg.MessageBody))
                    {
                        // Malformed: no body -> non-transient
                        await _receiver.MoveToDeadLetter(msg);
                        continue;
                    }

                    TransactionEvent? payload;
                    try
                    {
                        payload = JsonSerializer.Deserialize<TransactionEvent>(msg.MessageBody,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch
                    {
                        // Bad JSON -> non-transient
                        await _receiver.MoveToDeadLetter(msg);
                        continue;
                    }

                    if (payload is null || payload.id == Guid.Empty || payload.bankAccountId == Guid.Empty)
                    {
                        await _receiver.MoveToDeadLetter(msg);
                        continue;
                    }

                    // Only Credit/Debit are supported
                    var kind = payload.messageType?.Trim();
                    if (!string.Equals(kind, "Credit", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(kind, "Debit", StringComparison.OrdinalIgnoreCase))
                    {
                        await _receiver.MoveToDeadLetter(msg);
                        continue;
                    }

                    // Process inside a transaction with idempotency insert-first pattern
                    using var db = new BankingApi.EventReceiver.BankingApiDbContext();
                    using var tx = await db.Database.BeginTransactionAsync();

                    try
                    {
                        // 1) Insert into Inbox as "claim" for idempotency. If duplicate -> already processed.
                        db.TransactionMessages.Add(new TransactionMessage
                        {
                            Id = payload.id,
                            BankAccountId = payload.bankAccountId,
                            Amount = payload.amount,
                            MessageType = kind!,
                            ProcessedAtUtc = DateTime.UtcNow
                        });

                        try
                        {
                            await db.SaveChangesAsync();
                        }
                        catch (DbUpdateException)
                        {
                            // Unique PK violation => already processed by some other worker
                            await _receiver.Complete(msg);
                            await tx.RollbackAsync();
                            continue;
                        }

                        // 2) Apply atomic balance update
                        var delta = string.Equals(kind, "Credit", StringComparison.OrdinalIgnoreCase)
                            ? payload.amount
                            : -payload.amount;

                        // Single-row atomic update. RowsAffected==0 -> missing account -> non-transient
                        var affected = await db.Database.ExecuteSqlInterpolatedAsync(
                            $"UPDATE BankAccounts SET Balance = Balance + {delta} WHERE Id = {payload.bankAccountId}");

                        if (affected == 0)
                        {
                            // No such account: business/data error -> non-transient
                            await tx.RollbackAsync();
                            await _receiver.MoveToDeadLetter(msg);
                            continue;
                        }

                        await tx.CommitAsync();

                        // Done
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
                        // Non-transient processing failure
                        await _receiver.MoveToDeadLetter(msg);
                    }
                }
                catch
                {
                    // Defensive outer catch: if we fail before we touched the message, wait briefly to avoid tight loop.
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }
        }
    }
}
