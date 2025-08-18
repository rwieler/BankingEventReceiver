using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BankingApi.EventReceiver
{
    public class TransactionMessage
    {
        public Guid Id { get; set; }

        public Guid BankAccountId { get; set; }

        public decimal Amount { get; set; }

        public string MessageType { get; set; } = default!;

        public DateTime ProcessedAtUtc { get; set; }

        // Navigation property (FK to BankAccount)
        public BankAccount BankAccount { get; set; } = default!;
    }
}
