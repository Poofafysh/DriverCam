using System;
using System.Collections.Generic;

namespace EngineAudio
{
    /// <summary>
    /// Measured pitch of the game's engine recordings (0.4.0). Each rev-up / rev-down sweep was pitch-tracked offline
    /// (log-frequency spectra of neighbouring frames matched with sub-bin interpolation, then made monotone): the table
    /// gives the recording's pitch at 33 evenly spaced points over its length, relative to its top (rev-up: its end, rev-down:
    /// its start). They are measurements, not sound. The sweeps are far from linear, and gears 2-4 move only 4-50% in
    /// pitch while the simulated RPM swings from 58% to 100% of the redline, so playing them by a straight position (before
    /// 0.4.0) left the sound nearly still while the RPM climbed. With the table, the RPM's share of the redline picks the
    /// moment in the recording with that pitch, and the remaining difference is played as an exact pitch ratio, so the
    /// note always matches the RPM. A recording's top is taken as the redline. Clips not in the table use the old mapping.
    /// </summary>
    internal static class PitchCurves
    {
        private static readonly Dictionary<string, float[]> Table = new Dictionary<string, float[]>(StringComparer.Ordinal)
        {
            ["Inline 4 engine accel gear 01"] = new[] { 0.360f, 0.382f, 0.433f, 0.500f, 0.576f, 0.618f, 0.659f, 0.703f, 0.724f, 0.754f, 0.782f, 0.799f, 0.825f, 0.842f, 0.854f, 0.875f, 0.886f, 0.895f, 0.911f, 0.918f, 0.927f, 0.937f, 0.942f, 0.948f, 0.954f, 0.958f, 0.963f, 0.967f, 0.969f, 0.973f, 0.975f, 0.976f, 1.000f },
            ["Inline 4 engine accel gear 02"] = new[] { 0.789f, 0.804f, 0.819f, 0.834f, 0.847f, 0.861f, 0.872f, 0.884f, 0.894f, 0.905f, 0.914f, 0.923f, 0.930f, 0.936f, 0.943f, 0.948f, 0.955f, 0.959f, 0.965f, 0.969f, 0.973f, 0.977f, 0.980f, 0.983f, 0.985f, 0.988f, 0.990f, 0.992f, 0.994f, 0.996f, 0.998f, 1.000f, 1.000f },
            ["Inline 4 engine accel gear 03"] = new[] { 0.894f, 0.900f, 0.906f, 0.913f, 0.919f, 0.924f, 0.929f, 0.934f, 0.939f, 0.942f, 0.946f, 0.949f, 0.954f, 0.957f, 0.961f, 0.964f, 0.967f, 0.970f, 0.972f, 0.975f, 0.977f, 0.980f, 0.982f, 0.984f, 0.986f, 0.988f, 0.990f, 0.992f, 0.994f, 0.996f, 0.998f, 0.999f, 1.000f },
            ["Inline 4 engine accel gear 04"] = new[] { 0.514f, 0.540f, 0.565f, 0.590f, 0.618f, 0.642f, 0.667f, 0.690f, 0.713f, 0.735f, 0.755f, 0.777f, 0.796f, 0.816f, 0.832f, 0.847f, 0.861f, 0.874f, 0.886f, 0.895f, 0.904f, 0.911f, 0.919f, 0.927f, 0.937f, 0.945f, 0.954f, 0.963f, 0.971f, 0.980f, 0.987f, 0.995f, 1.000f },
            ["Inline 4 engine decel gear 01"] = new[] { 1.000f, 0.931f, 0.862f, 0.800f, 0.741f, 0.687f, 0.638f, 0.593f, 0.548f, 0.506f, 0.471f, 0.438f, 0.406f, 0.377f, 0.349f, 0.323f, 0.305f, 0.287f, 0.270f, 0.255f, 0.243f, 0.232f, 0.222f, 0.213f, 0.210f, 0.207f, 0.207f, 0.206f, 0.206f, 0.206f, 0.205f, 0.205f, 0.204f },
            ["Inline 4 engine decel gear 02"] = new[] { 1.000f, 0.988f, 0.975f, 0.962f, 0.949f, 0.937f, 0.924f, 0.912f, 0.900f, 0.888f, 0.876f, 0.864f, 0.853f, 0.841f, 0.830f, 0.819f, 0.808f, 0.797f, 0.786f, 0.776f, 0.766f, 0.755f, 0.745f, 0.735f, 0.725f, 0.716f, 0.706f, 0.696f, 0.687f, 0.678f, 0.669f, 0.660f, 0.652f },
            ["Inline 4 engine decel gear 03"] = new[] { 1.000f, 0.994f, 0.986f, 0.979f, 0.972f, 0.964f, 0.957f, 0.950f, 0.943f, 0.935f, 0.928f, 0.921f, 0.914f, 0.907f, 0.900f, 0.894f, 0.887f, 0.881f, 0.874f, 0.867f, 0.861f, 0.855f, 0.848f, 0.842f, 0.836f, 0.830f, 0.823f, 0.818f, 0.812f, 0.806f, 0.800f, 0.794f, 0.789f },
            ["Inline 6 engine accel gear 01"] = new[] { 0.149f, 0.149f, 0.149f, 0.152f, 0.152f, 0.160f, 0.175f, 0.190f, 0.196f, 0.211f, 0.230f, 0.255f, 0.283f, 0.312f, 0.342f, 0.365f, 0.400f, 0.439f, 0.486f, 0.530f, 0.577f, 0.620f, 0.663f, 0.705f, 0.743f, 0.781f, 0.815f, 0.849f, 0.882f, 0.910f, 0.940f, 0.970f, 1.000f },
            ["Inline 6 engine accel gear 02"] = new[] { 0.653f, 0.655f, 0.663f, 0.676f, 0.689f, 0.702f, 0.716f, 0.728f, 0.742f, 0.754f, 0.768f, 0.780f, 0.793f, 0.805f, 0.816f, 0.829f, 0.842f, 0.855f, 0.866f, 0.877f, 0.886f, 0.898f, 0.910f, 0.920f, 0.929f, 0.939f, 0.949f, 0.958f, 0.968f, 0.976f, 0.985f, 0.993f, 1.000f },
            ["Inline 6 engine accel gear 03"] = new[] { 0.791f, 0.792f, 0.796f, 0.807f, 0.817f, 0.829f, 0.841f, 0.851f, 0.861f, 0.870f, 0.881f, 0.888f, 0.897f, 0.904f, 0.913f, 0.921f, 0.928f, 0.934f, 0.940f, 0.946f, 0.951f, 0.958f, 0.964f, 0.969f, 0.971f, 0.975f, 0.980f, 0.985f, 0.987f, 0.991f, 0.995f, 0.998f, 1.000f },
            ["Inline 6 engine accel gear 04"] = new[] { 0.897f, 0.897f, 0.901f, 0.906f, 0.910f, 0.916f, 0.919f, 0.924f, 0.928f, 0.932f, 0.936f, 0.940f, 0.943f, 0.947f, 0.950f, 0.954f, 0.957f, 0.961f, 0.964f, 0.967f, 0.970f, 0.973f, 0.976f, 0.978f, 0.981f, 0.984f, 0.986f, 0.988f, 0.991f, 0.993f, 0.996f, 0.998f, 1.000f },
            ["Inline 6 engine decel gear 01"] = new[] { 1.000f, 0.951f, 0.889f, 0.837f, 0.795f, 0.751f, 0.705f, 0.668f, 0.623f, 0.580f, 0.539f, 0.502f, 0.469f, 0.437f, 0.406f, 0.395f, 0.376f, 0.346f, 0.321f, 0.300f, 0.287f, 0.272f, 0.262f, 0.256f, 0.250f, 0.243f, 0.240f, 0.238f, 0.235f, 0.233f, 0.230f, 0.230f, 0.230f },
            ["Inline 6 engine decel gear 02"] = new[] { 1.000f, 0.999f, 0.999f, 0.994f, 0.989f, 0.980f, 0.971f, 0.959f, 0.952f, 0.950f, 0.948f, 0.944f, 0.936f, 0.929f, 0.921f, 0.913f, 0.905f, 0.897f, 0.896f, 0.894f, 0.892f, 0.888f, 0.887f, 0.882f, 0.874f, 0.865f, 0.858f, 0.852f, 0.849f, 0.847f, 0.840f, 0.839f, 0.835f },
            ["Inline 6 engine decel gear 03"] = new[] { 1.000f, 0.998f, 0.995f, 0.992f, 0.989f, 0.985f, 0.983f, 0.980f, 0.978f, 0.977f, 0.975f, 0.974f, 0.972f, 0.970f, 0.969f, 0.968f, 0.966f, 0.963f, 0.962f, 0.959f, 0.954f, 0.953f, 0.951f, 0.948f, 0.946f, 0.944f, 0.942f, 0.939f, 0.938f, 0.936f, 0.933f, 0.930f, 0.925f },
            ["RX7 engine accel gear 01"] = new[] { 0.656f, 0.662f, 0.665f, 0.673f, 0.691f, 0.710f, 0.728f, 0.746f, 0.761f, 0.779f, 0.797f, 0.811f, 0.828f, 0.842f, 0.853f, 0.865f, 0.878f, 0.891f, 0.905f, 0.917f, 0.929f, 0.934f, 0.944f, 0.954f, 0.963f, 0.971f, 0.976f, 0.983f, 0.986f, 0.988f, 0.992f, 0.998f, 1.000f },
            ["RX7 engine accel gear 02"] = new[] { 0.916f, 0.917f, 0.919f, 0.924f, 0.928f, 0.931f, 0.934f, 0.938f, 0.943f, 0.948f, 0.952f, 0.954f, 0.957f, 0.960f, 0.963f, 0.965f, 0.967f, 0.971f, 0.973f, 0.975f, 0.977f, 0.979f, 0.982f, 0.983f, 0.986f, 0.987f, 0.989f, 0.991f, 0.993f, 0.995f, 0.997f, 0.998f, 1.000f },
            ["RX7 engine accel gear 03"] = new[] { 0.954f, 0.955f, 0.955f, 0.956f, 0.957f, 0.959f, 0.961f, 0.963f, 0.965f, 0.967f, 0.969f, 0.971f, 0.973f, 0.974f, 0.976f, 0.979f, 0.979f, 0.981f, 0.983f, 0.984f, 0.985f, 0.986f, 0.988f, 0.989f, 0.990f, 0.992f, 0.993f, 0.995f, 0.996f, 0.997f, 0.998f, 1.000f, 1.000f },
            ["RX7 engine accel gear 04"] = new[] { 0.963f, 0.963f, 0.965f, 0.967f, 0.968f, 0.970f, 0.971f, 0.972f, 0.974f, 0.975f, 0.976f, 0.978f, 0.979f, 0.980f, 0.981f, 0.983f, 0.984f, 0.985f, 0.986f, 0.987f, 0.988f, 0.989f, 0.990f, 0.992f, 0.993f, 0.994f, 0.995f, 0.996f, 0.997f, 0.998f, 0.999f, 1.000f, 1.000f },
            ["RX7 engine decel gear 01"] = new[] { 1.000f, 1.000f, 0.998f, 0.993f, 0.991f, 0.987f, 0.985f, 0.984f, 0.983f, 0.981f, 0.980f, 0.977f, 0.974f, 0.973f, 0.973f, 0.970f, 0.968f, 0.968f, 0.966f, 0.964f, 0.964f, 0.962f, 0.961f, 0.960f, 0.958f, 0.958f, 0.956f, 0.955f, 0.954f, 0.952f, 0.950f, 0.950f, 0.950f },
            ["RX7 engine decel gear 02"] = new[] { 1.000f, 1.000f, 0.999f, 0.999f, 0.997f, 0.995f, 0.995f, 0.994f, 0.993f, 0.992f, 0.991f, 0.990f, 0.988f, 0.986f, 0.985f, 0.984f, 0.983f, 0.982f, 0.981f, 0.980f, 0.978f, 0.977f, 0.976f, 0.974f, 0.973f, 0.972f, 0.971f, 0.970f, 0.969f, 0.967f, 0.966f, 0.965f, 0.962f },
            ["RX7 engine decel gear 03"] = new[] { 1.000f, 1.000f, 0.999f, 0.999f, 0.999f, 0.999f, 0.999f, 0.998f, 0.998f, 0.998f, 0.998f, 0.997f, 0.997f, 0.996f, 0.996f, 0.996f, 0.996f, 0.996f, 0.995f, 0.995f, 0.995f, 0.995f, 0.994f, 0.993f, 0.993f, 0.993f, 0.992f, 0.992f, 0.992f, 0.992f, 0.991f, 0.991f, 0.991f },
            ["V12 engine accel gear 01"] = new[] { 0.286f, 0.341f, 0.403f, 0.461f, 0.521f, 0.565f, 0.614f, 0.660f, 0.690f, 0.724f, 0.749f, 0.770f, 0.796f, 0.811f, 0.829f, 0.844f, 0.855f, 0.872f, 0.880f, 0.891f, 0.902f, 0.909f, 0.920f, 0.925f, 0.932f, 0.937f, 0.940f, 0.945f, 0.947f, 0.951f, 0.953f, 0.955f, 1.000f },
            ["V12 engine accel gear 02"] = new[] { 0.779f, 0.780f, 0.794f, 0.813f, 0.831f, 0.846f, 0.860f, 0.875f, 0.889f, 0.900f, 0.911f, 0.921f, 0.928f, 0.935f, 0.942f, 0.947f, 0.952f, 0.957f, 0.962f, 0.965f, 0.969f, 0.972f, 0.976f, 0.978f, 0.981f, 0.984f, 0.986f, 0.988f, 0.991f, 0.994f, 0.995f, 0.997f, 1.000f },
            ["V12 engine accel gear 03"] = new[] { 0.892f, 0.893f, 0.901f, 0.910f, 0.919f, 0.927f, 0.932f, 0.938f, 0.943f, 0.948f, 0.952f, 0.955f, 0.959f, 0.962f, 0.965f, 0.968f, 0.971f, 0.973f, 0.976f, 0.978f, 0.980f, 0.983f, 0.984f, 0.987f, 0.988f, 0.990f, 0.992f, 0.993f, 0.995f, 0.996f, 0.997f, 0.999f, 1.000f },
            ["V12 engine accel gear 04"] = new[] { 0.943f, 0.945f, 0.948f, 0.951f, 0.953f, 0.956f, 0.959f, 0.961f, 0.964f, 0.966f, 0.968f, 0.970f, 0.972f, 0.974f, 0.976f, 0.978f, 0.979f, 0.981f, 0.983f, 0.984f, 0.986f, 0.987f, 0.989f, 0.990f, 0.991f, 0.992f, 0.993f, 0.995f, 0.996f, 0.997f, 0.998f, 0.999f, 1.000f },
            ["V12 engine decel gear 01"] = new[] { 1.000f, 0.955f, 0.913f, 0.864f, 0.821f, 0.778f, 0.740f, 0.698f, 0.664f, 0.629f, 0.596f, 0.561f, 0.535f, 0.509f, 0.485f, 0.459f, 0.438f, 0.416f, 0.396f, 0.379f, 0.369f, 0.359f, 0.349f, 0.343f, 0.339f, 0.336f, 0.333f, 0.330f, 0.329f, 0.327f, 0.325f, 0.324f, 0.323f },
            ["V12 engine decel gear 02"] = new[] { 1.000f, 0.989f, 0.976f, 0.963f, 0.950f, 0.937f, 0.924f, 0.912f, 0.899f, 0.887f, 0.875f, 0.862f, 0.851f, 0.839f, 0.828f, 0.817f, 0.805f, 0.793f, 0.783f, 0.772f, 0.761f, 0.750f, 0.739f, 0.729f, 0.719f, 0.709f, 0.699f, 0.688f, 0.679f, 0.670f, 0.660f, 0.651f, 0.643f },
            ["V12 engine decel gear 03"] = new[] { 1.000f, 0.999f, 0.992f, 0.985f, 0.978f, 0.971f, 0.965f, 0.957f, 0.950f, 0.943f, 0.936f, 0.929f, 0.922f, 0.916f, 0.909f, 0.903f, 0.896f, 0.889f, 0.883f, 0.876f, 0.870f, 0.863f, 0.857f, 0.851f, 0.845f, 0.839f, 0.833f, 0.827f, 0.821f, 0.815f, 0.809f, 0.803f, 0.799f },
            ["V8 engine accel gear 01"] = new[] { 0.363f, 0.363f, 0.363f, 0.368f, 0.410f, 0.451f, 0.480f, 0.517f, 0.546f, 0.575f, 0.617f, 0.637f, 0.665f, 0.694f, 0.716f, 0.738f, 0.765f, 0.786f, 0.801f, 0.824f, 0.843f, 0.855f, 0.873f, 0.890f, 0.899f, 0.917f, 0.932f, 0.941f, 0.954f, 0.968f, 0.978f, 0.985f, 1.000f },
            ["V8 engine accel gear 02"] = new[] { 0.690f, 0.704f, 0.720f, 0.735f, 0.750f, 0.764f, 0.778f, 0.791f, 0.804f, 0.816f, 0.827f, 0.839f, 0.850f, 0.860f, 0.870f, 0.880f, 0.889f, 0.897f, 0.906f, 0.914f, 0.922f, 0.930f, 0.937f, 0.945f, 0.952f, 0.959f, 0.966f, 0.972f, 0.978f, 0.984f, 0.990f, 0.995f, 1.000f },
            ["V8 engine accel gear 03"] = new[] { 0.766f, 0.778f, 0.795f, 0.810f, 0.825f, 0.840f, 0.853f, 0.865f, 0.877f, 0.887f, 0.897f, 0.906f, 0.914f, 0.923f, 0.930f, 0.936f, 0.941f, 0.947f, 0.951f, 0.955f, 0.960f, 0.964f, 0.968f, 0.972f, 0.975f, 0.978f, 0.982f, 0.986f, 0.989f, 0.991f, 0.995f, 0.997f, 1.000f },
            ["V8 engine accel gear 04"] = new[] { 0.904f, 0.910f, 0.916f, 0.921f, 0.927f, 0.932f, 0.936f, 0.940f, 0.945f, 0.949f, 0.952f, 0.955f, 0.959f, 0.962f, 0.964f, 0.967f, 0.970f, 0.972f, 0.974f, 0.977f, 0.980f, 0.981f, 0.983f, 0.985f, 0.987f, 0.989f, 0.991f, 0.992f, 0.994f, 0.996f, 0.997f, 0.999f, 1.000f },
            ["V8 engine decel gear 01"] = new[] { 1.000f, 0.962f, 0.924f, 0.897f, 0.872f, 0.858f, 0.835f, 0.830f, 0.829f, 0.827f, 0.815f, 0.814f, 0.812f, 0.809f, 0.793f, 0.783f, 0.765f, 0.761f, 0.758f, 0.756f, 0.756f, 0.751f, 0.751f, 0.749f, 0.735f, 0.734f, 0.727f, 0.727f, 0.725f, 0.725f, 0.723f, 0.719f, 0.717f },
            ["V8 engine decel gear 02"] = new[] { 1.000f, 0.991f, 0.979f, 0.966f, 0.955f, 0.944f, 0.934f, 0.925f, 0.914f, 0.905f, 0.896f, 0.887f, 0.882f, 0.872f, 0.866f, 0.856f, 0.845f, 0.839f, 0.832f, 0.823f, 0.817f, 0.810f, 0.805f, 0.798f, 0.791f, 0.785f, 0.781f, 0.775f, 0.772f, 0.766f, 0.756f, 0.748f, 0.743f },
            ["V8 engine decel gear 03"] = new[] { 1.000f, 0.992f, 0.982f, 0.973f, 0.965f, 0.958f, 0.950f, 0.944f, 0.935f, 0.927f, 0.919f, 0.910f, 0.905f, 0.896f, 0.890f, 0.880f, 0.873f, 0.869f, 0.862f, 0.857f, 0.853f, 0.844f, 0.838f, 0.832f, 0.825f, 0.819f, 0.816f, 0.810f, 0.807f, 0.801f, 0.795f, 0.790f, 0.785f },
            ["muscle engine accel gear 01"] = new[] { 0.388f, 0.388f, 0.398f, 0.426f, 0.459f, 0.497f, 0.546f, 0.588f, 0.628f, 0.662f, 0.712f, 0.741f, 0.766f, 0.787f, 0.825f, 0.842f, 0.857f, 0.873f, 0.899f, 0.911f, 0.921f, 0.936f, 0.950f, 0.957f, 0.964f, 0.975f, 0.981f, 0.986f, 0.989f, 0.995f, 0.996f, 0.998f, 1.000f },
            ["muscle engine accel gear 02"] = new[] { 0.849f, 0.849f, 0.852f, 0.866f, 0.882f, 0.892f, 0.906f, 0.914f, 0.925f, 0.931f, 0.942f, 0.947f, 0.955f, 0.959f, 0.965f, 0.967f, 0.972f, 0.973f, 0.976f, 0.979f, 0.981f, 0.983f, 0.985f, 0.987f, 0.988f, 0.990f, 0.991f, 0.993f, 0.994f, 0.996f, 0.997f, 0.999f, 1.000f },
            ["muscle engine accel gear 03"] = new[] { 0.926f, 0.926f, 0.926f, 0.928f, 0.934f, 0.939f, 0.943f, 0.946f, 0.950f, 0.954f, 0.957f, 0.960f, 0.964f, 0.968f, 0.970f, 0.972f, 0.974f, 0.976f, 0.978f, 0.980f, 0.982f, 0.984f, 0.986f, 0.988f, 0.990f, 0.991f, 0.993f, 0.994f, 0.995f, 0.997f, 0.998f, 0.999f, 1.000f },
            ["muscle engine accel gear 04"] = new[] { 0.962f, 0.962f, 0.962f, 0.962f, 0.962f, 0.962f, 0.962f, 0.962f, 0.962f, 0.963f, 0.965f, 0.968f, 0.970f, 0.972f, 0.974f, 0.976f, 0.978f, 0.979f, 0.981f, 0.982f, 0.983f, 0.985f, 0.986f, 0.987f, 0.988f, 0.989f, 0.990f, 0.992f, 0.993f, 0.994f, 0.995f, 0.996f, 1.000f },
            ["muscle engine decel gear 01"] = new[] { 1.000f, 0.967f, 0.920f, 0.876f, 0.834f, 0.793f, 0.757f, 0.719f, 0.685f, 0.651f, 0.619f, 0.589f, 0.559f, 0.530f, 0.505f, 0.479f, 0.456f, 0.433f, 0.412f, 0.392f, 0.376f, 0.366f, 0.357f, 0.351f, 0.346f, 0.341f, 0.339f, 0.336f, 0.333f, 0.330f, 0.329f, 0.328f, 0.325f },
            ["muscle engine decel gear 02"] = new[] { 1.000f, 1.000f, 1.000f, 1.000f, 1.000f, 1.000f, 1.000f, 0.996f, 0.986f, 0.979f, 0.969f, 0.961f, 0.953f, 0.944f, 0.935f, 0.926f, 0.917f, 0.908f, 0.899f, 0.891f, 0.882f, 0.871f, 0.860f, 0.851f, 0.844f, 0.836f, 0.828f, 0.820f, 0.812f, 0.805f, 0.798f, 0.793f, 0.787f },
            ["muscle engine decel gear 03"] = new[] { 1.000f, 1.000f, 1.000f, 1.000f, 0.996f, 0.992f, 0.984f, 0.979f, 0.975f, 0.972f, 0.965f, 0.960f, 0.956f, 0.953f, 0.944f, 0.940f, 0.936f, 0.931f, 0.923f, 0.919f, 0.916f, 0.912f, 0.907f, 0.904f, 0.900f, 0.896f, 0.892f, 0.890f, 0.887f, 0.883f, 0.880f, 0.878f, 0.876f },
        };

        /// <summary>
        /// For a recording and the RPM's share of the redline (rho): where to play (seconds) and the pitch ratio that brings
        /// the recording's pitch there to rho. False when the clip isn't measured. rising = a rev-up sweep.
        /// </summary>
        internal static bool Find(string clip, float length, float rho, bool rising, out float position, out float ratio)
        {
            position = 0f; ratio = 1f;
            if (clip == null || !Table.TryGetValue(StripCopy(clip), out var q) || length <= 0f) return false;
            int n = q.Length;
            float lo = rising ? q[0] : q[n - 1], hi = rising ? q[n - 1] : q[0];
            float want = rho < lo ? lo : rho > hi ? hi : rho;
            // a monotone table: find the segment holding want
            float idx = 0f;
            for (int i = 0; i < n - 1; i++)
            {
                float a = q[i], b = q[i + 1];
                bool inside = rising ? (want >= a && want <= b) : (want <= a && want >= b);
                if (!inside) continue;
                idx = Math.Abs(b - a) < 1e-6f ? i : i + (want - a) / (b - a);
                break;
            }
            // keep clear of the very ends (a grain needs room either side)
            float u = idx / (n - 1);
            u = 0.02f + 0.94f * u;
            position = u * length;
            float at = Sample(q, u);
            ratio = at > 0.01f ? rho / at : 1f;
            return true;
        }

        private static float Sample(float[] q, float u)
        {
            float x = Math.Max(0f, Math.Min(1f, u)) * (q.Length - 1);
            int i = Math.Min(q.Length - 2, (int)x);
            float f = x - i;
            return q[i] + (q[i + 1] - q[i]) * f;
        }

        private static string StripCopy(string name) => name.EndsWith("_0", StringComparison.Ordinal) ? name.Substring(0, name.Length - 2) : name;

        internal static int Count => Table.Count;

        /// <summary>"n of m": how many of the car's sweeps are in the table (for the setup log line).</summary>
        internal static string Measured(UnityEngine.AudioClip[] accel, UnityEngine.AudioClip[] decel)
        {
            int n = 0, m = 0;
            foreach (var arr in new[] { accel, decel })
            {
                if (arr == null) continue;
                foreach (var c in arr) { if (c == null) continue; m++; if (Table.ContainsKey(StripCopy(c.name))) n++; }
            }
            return $"{n} of {m}";
        }

        /// <summary>The clip's name, read again only when the clip changes (an Il2Cpp string read allocates).</summary>
        internal static string NameOf(UnityEngine.AudioClip c, ref IntPtr ptr, ref string name)
        {
            if (c.Pointer != ptr || name == null) { ptr = c.Pointer; name = c.name; }
            return name;
        }
    }
}
