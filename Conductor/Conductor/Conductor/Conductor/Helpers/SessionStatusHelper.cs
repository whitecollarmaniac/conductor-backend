namespace Conductor.Helpers
{
    /// <summary>
    /// Helper class for determining session status consistently across the application
    /// </summary>
    public static class SessionStatusHelper
    {
        /// <summary>
        /// Determine canonical session status.
        /// States: complete, inactive, waiting, live.
        /// </summary>
        /// <param name="hasPendingTicket">Session has pending NextStep</param>
        /// <param name="hasDecidedTicket">Session has decided NextStep</param>
        /// <param name="hasProcessedTicket">Session has processed NextStep</param>
        /// <param name="hasCompletedTicket">Session has completed NextStep</param>
        /// <param name="isActive">Session isActive flag</param>
        /// <param name="lastSeenAt">Session last seen timestamp</param>
        /// <param name="inactiveThreshold">Threshold for considering session inactive</param>
        /// <returns>Session status string</returns>
        public static string DetermineSessionStatus(bool hasPendingTicket, bool hasDecidedTicket, bool hasProcessedTicket, bool hasCompletedTicket, bool isActive, DateTimeOffset lastSeenAt, TimeSpan inactiveThreshold)
        {
            var isRecentlyActive = (DateTimeOffset.UtcNow - lastSeenAt) < inactiveThreshold;
            
            // 1) Completed sessions take precedence
            if (hasCompletedTicket)
            {
                return "complete";
            }
            
            // 2) Inactive if not recently active or explicitly inactive
            if (!isRecentlyActive || !isActive)
            {
                return "inactive";
            }

            // 3) Active client with a pending ticket is waiting
            if (hasPendingTicket)
            {
                return "waiting";
            }
            
            // 4) Otherwise live
            return "live";
        }
    }
}
