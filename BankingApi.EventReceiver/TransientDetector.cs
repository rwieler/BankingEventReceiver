using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace BankingApi.EventReceiver
{
    internal static class TransientDetector
    {
        public static bool IsTransient(Exception ex)
        {
            // Conservative heuristic: timeouts, connection drops, SQL transient error codes
            if (ex is TimeoutException) return true;
            if (ex is DbException) return true; // covers SqlException under Microsoft.Data.SqlClient

            if (ex is SqlException sql)
            {
                // Common transient codes (not exhaustive)
                // 4060 Login failed DB unavailable, 40197/40501/40613/10928/10929 throttling/service busy
                int[] transient = { 4060, 40197, 40501, 40613, 10928, 10929, 49918, 49919, 49920, -2 /*timeout*/ };
                foreach (var n in transient) if (sql.Number == n) return true;
            }
            return false;
        }
    }
}