using System.Runtime.CompilerServices;

namespace Police
{
    /// <summary>
    /// The caught penalty. TimerManager.AddTimerSeconds(-5) can't be used: in single-player it passes a negative value
    /// to RaceTimer.RemoveCountdownTime, which clamps its argument with max(time, 0), so it removes nothing (and in
    /// multiplayer it goes through NetworkGameManager). The game's own path for taking time off is
    /// RaceTimer.RemoveCountdownTime(positive seconds): countdownTime = max(0, countdownTime - seconds), local only.
    /// TimerManager comes from Singleton.Instance.TimerManager (the game's manager hub).
    /// </summary>
    internal static partial class GameApi
    {
        /// <summary>
        /// Takes up to <paramref name="seconds"/> off a running countdown, never leaving less than
        /// <paramref name="keep"/> seconds (a police catch must not end the race). Returns the seconds actually taken
        /// (0 = none) and why in <paramref name="note"/>. Only call when TimerOk, in single-player.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float TakeTime(float seconds, float keep, out string note)
        {
            note = null;
            var hub = Singleton.Instance;
            if (hub == null) { note = "no game manager"; return 0f; }
            var timer = hub.TimerManager;
            if (timer == null) { note = "no timer manager"; return 0f; }
            if (!timer.CountdownMode) { note = "timer is not a countdown"; return 0f; }
            if (!timer.IsTimerPlaying) { note = "timer not running"; return 0f; }
            float remaining = timer.RemainingTime;
            float take = seconds;
            if (take > remaining - keep) take = remaining - keep;
            if (take <= 0.05f) { note = $"only {remaining:0.0} s left"; return 0f; }
            var race = timer.raceTimer;
            if (race == null) { note = "no race timer"; return 0f; }
            race.RemoveCountdownTime(take);
            return take;
        }
    }
}
