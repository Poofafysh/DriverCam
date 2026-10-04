using System;

namespace RacingLine
{
    /// <summary>
    /// Publishes the finished racing line to other plugins as AppDomain data "rogue.racingline" (the same way HeadLook
    /// shares its head angle with DriverCam): the Police plugin's daredevils race this line. Only BCL types, so no plugin
    /// needs a reference to another:
    ///   object[] { int version, long pathPtr, float step, int n, float limit, float[] e, float[] curvature }
    /// e = the line's offset from the road's centre line per sample (metres, + = right: the same frame as a traffic car's
    /// lane offset, AvoidanceLaneOffset), sample i at i x step metres along the run's regular path (the same distance as a
    /// traffic car's AvoidanceRoadDistance); curvature = the racing line's own signed curvature (1/m, + = turning right);
    /// limit = how far either side of the centre the line may go. The arrays are never changed after publishing (a rebuilt
    /// line is a new object with a new version); readers must not change them either. null while there is no line.
    /// </summary>
    internal static class LineShare
    {
        public const string Key = "rogue.racingline";
        private static Line _published;
        private static int _version;

        /// <summary>Call every frame with the current line (or null): publishes only when it changed.</summary>
        public static void Sync(Line line, float[] curvature)
        {
            if (line != null && (curvature == null || curvature.Length != line.N)) line = null;   // not ready: publish nothing yet
            if (ReferenceEquals(line, _published)) return;
            _published = line;
            AppDomain.CurrentDomain.SetData(Key, line == null ? null
                : new object[] { ++_version, (long)line.PathPtr, line.Step, line.N, line.Limit, line.E, curvature });
        }
    }
}
