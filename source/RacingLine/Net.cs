namespace RacingLine
{
    internal enum NetRole { Offline, Host, Client }

    /// <summary>
    /// Who owns the game state (design doc, Safety rule 11). As a client, the host owns traffic, scores and race state
    /// and syncs them to everyone, so the plugin may only read and draw them (each player's own score categories are local: the game sends its score totals itself). If the role can't be read, assume Client.
    /// </summary>
    internal static class Net
    {
        internal static NetRole Role()
        {
            try
            {
                if (Mirror.NetworkServer.active) return NetRole.Host;
                if (Mirror.NetworkClient.active) return NetRole.Client;
                return NetRole.Offline;
            }
            catch { return NetRole.Client; }
        }

        /// <summary>True only where we own the simulation. Nothing in phase 1 writes game state; tier 2 will need this.</summary>
        internal static bool MayWriteGameState() => Role() != NetRole.Client;
    }
}
