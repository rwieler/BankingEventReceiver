using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BankingApi.EventReceiver.Contracts
{
    public sealed class TransactionEvent
    { 
        public Guid id { get; set; }
        public string? messageType { get; set; }
        public Guid bankAccountId { get; set; }
        public decimal amount { get; set; }
    }
}
