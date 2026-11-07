using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// Writes fields inside RawAccel's nested value-struct settings (AccelArgs,
    /// SpeedArgs, etc.). Reflection is used to keep the calibration model
    /// independent of the C++/CLI wrapper assembly.
    /// </summary>
    public static class RawAccelSettingsBridge
    {
        public static double LookupActiveDirectionalBaseFactorFromProfile(
            object activeProfile,
            double baseOutputDpi,
            string direction)
        {
            if (activeProfile == null ||
                double.IsNaN(baseOutputDpi) || double.IsInfinity(baseOutputDpi) ||
                baseOutputDpi <= 0.0 || string.IsNullOrWhiteSpace(direction))
                return 1.0;

            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public;
                Type type = activeProfile.GetType();
                FieldInfo outputField = type.GetField("outputDPI", flags);
                if (outputField == null) return 1.0;
                double outputDpi = Convert.ToDouble(outputField.GetValue(activeProfile), CultureInfo.InvariantCulture);
                if (double.IsNaN(outputDpi) || double.IsInfinity(outputDpi) || outputDpi <= 0.0)
                    return 1.0;

                double factor = outputDpi / baseOutputDpi;
                string ratioFieldName;
                switch (direction)
                {
                    case "Right":
                        break;
                    case "Left":
                        ratioFieldName = "lrOutputDPIRatio";
                        factor *= ReadPositiveRatio(type, activeProfile, ratioFieldName, flags);
                        break;
                    case "Down":
                        ratioFieldName = "yxOutputDPIRatio";
                        factor *= ReadPositiveRatio(type, activeProfile, ratioFieldName, flags);
                        break;
                    case "Up":
                        ratioFieldName = "yxOutputDPIRatio";
                        factor *= ReadPositiveRatio(type, activeProfile, ratioFieldName, flags);
                        factor *= ReadPositiveRatio(type, activeProfile, "udOutputDPIRatio", flags);
                        break;
                    default:
                        return 1.0;
                }

                return !double.IsNaN(factor) && !double.IsInfinity(factor) && factor > 0.0
                    ? factor : 1.0;
            }
            catch
            {
                return 1.0;
            }
        }

        private static double ReadPositiveRatio(
            Type profileType,
            object activeProfile,
            string fieldName,
            BindingFlags flags)
        {
            FieldInfo field = profileType.GetField(fieldName, flags);
            if (field == null) return 1.0;
            double value = Convert.ToDouble(field.GetValue(activeProfile), CultureInfo.InvariantCulture);
            return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0.0
                ? value : 1.0;
        }

        public static bool TrySetNestedFields(
            object target,
            string parentFieldName,
            IDictionary<string, object> values,
            out string error)
        {
            error = null;
            if (target == null)
            {
                error = "Settings target is null.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(parentFieldName) || values == null || values.Count == 0)
            {
                error = "Nested field name and at least one value are required.";
                return false;
            }

            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public;
                var parentField = target.GetType().GetField(parentFieldName, flags);
                if (parentField == null || parentField.IsInitOnly)
                {
                    error = "RawAccel settings field '" + parentFieldName + "' is missing or read-only.";
                    return false;
                }

                object nestedValue = parentField.GetValue(target);
                if (nestedValue == null)
                {
                    error = "RawAccel settings field '" + parentFieldName + "' is null.";
                    return false;
                }

                var pending = new List<KeyValuePair<FieldInfo, object>>();
                foreach (var entry in values)
                {
                    var nestedField = parentField.FieldType.GetField(entry.Key, flags);
                    if (nestedField == null || nestedField.IsInitOnly)
                    {
                        error = "RawAccel nested field '" + parentFieldName + "." + entry.Key + "' is missing or read-only.";
                        return false;
                    }

                    object converted;
                    if (!TryConvert(entry.Value, nestedField.FieldType, out converted))
                    {
                        error = "Could not convert value for RawAccel field '" + parentFieldName + "." + entry.Key + "'.";
                        return false;
                    }
                    pending.Add(new KeyValuePair<FieldInfo, object>(nestedField, converted));
                }

                // The nested value is a value type in the RawAccel C++/CLI
                // wrapper. Mutate the boxed copy, then write it back to Profile.
                foreach (var item in pending)
                    item.Key.SetValue(nestedValue, item.Value);

                parentField.SetValue(target, nestedValue);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Builds the native RawAccel argument fields for a fitted directional curve.
        /// Validates persisted parameters before they can enter native settings.
        /// </summary>
        public static bool TryBuildDirectionalCurveValues(
            AccelCurveMode mode,
            double[] parameters,
            out Dictionary<string, object> values,
            out string error)
        {
            values = new Dictionary<string, object>();
            error = null;
            int expectedLength;
            switch (mode)
            {
                case AccelCurveMode.Linear: expectedLength = 2; break;
                case AccelCurveMode.Natural: expectedLength = 3; break;
                case AccelCurveMode.Power:
                case AccelCurveMode.Synchronous: expectedLength = 4; break;
                default:
                    error = "The selected acceleration mode has no writable fitted curve.";
                    return false;
            }

            // Profile storage uses a fixed MaxCurveParameters stride (4)
            // for every mode. Linear and Natural therefore arrive padded with
            // trailing zeros; require at least the mode's real parameter count
            // and consume only the parameters used by that mode.
            if (parameters == null || parameters.Length < expectedLength)
            {
                error = "The fitted acceleration curve has an invalid parameter count.";
                return false;
            }
            for (int i = 0; i < parameters.Length; i++)
            {
                if (double.IsNaN(parameters[i]) || double.IsInfinity(parameters[i]))
                {
                    error = "The fitted acceleration curve contains a non-finite parameter.";
                    return false;
                }
            }

            switch (mode)
            {
                case AccelCurveMode.Linear:
                    if (!InRange(parameters[0], 0.0, DesktopSensConstants.InputOffsetMax) ||
                        !InRange(parameters[1], DesktopSensConstants.LinearAccelMin, DesktopSensConstants.LinearAccelMax))
                        return InvalidCurve(out values, out error);
                    values["mode"] = "classic";
                    values["acceleration"] = parameters[1];
                    values["exponentClassic"] = 1.0;
                    values["inputOffset"] = parameters[0];
                    values["gain"] = true;
                    break;

                case AccelCurveMode.Natural:
                    if (!InRange(parameters[0], 0.0, DesktopSensConstants.InputOffsetMax) ||
                        !InRange(parameters[1], DesktopSensConstants.NaturalDecayMin, DesktopSensConstants.NaturalDecayMax) ||
                        !InRange(parameters[2], DesktopSensConstants.NaturalLimitMin, DesktopSensConstants.NaturalLimitMax))
                        return InvalidCurve(out values, out error);
                    values["mode"] = "natural";
                    values["inputOffset"] = parameters[0];
                    values["decayRate"] = parameters[1];
                    values["limit"] = parameters[2];
                    values["gain"] = true;
                    break;

                case AccelCurveMode.Power:
                    if (!InRange(parameters[0], 0.0, DesktopSensConstants.InputOffsetMax) ||
                        !InRange(parameters[1], DesktopSensConstants.PowerScaleMin, DesktopSensConstants.PowerScaleMax) ||
                        !InRange(parameters[2], DesktopSensConstants.PowerExponentMin, DesktopSensConstants.PowerExponentMax) ||
                        !InRange(parameters[3], -0.5, 0.5))
                        return InvalidCurve(out values, out error);
                    values["mode"] = "power";
                    values["outputOffset"] = parameters[3];
                    values["scale"] = parameters[1];
                    values["exponentPower"] = parameters[2];
                    values["inputOffset"] = parameters[0];
                    values["gain"] = true;
                    break;

                case AccelCurveMode.Synchronous:
                    if (!InRange(parameters[0], 0.1, DesktopSensConstants.SyncSpeedMax) ||
                        !InRange(parameters[1], DesktopSensConstants.MotivityMin, DesktopSensConstants.MotivityMax) ||
                        !InRange(parameters[2], DesktopSensConstants.GammaMin, DesktopSensConstants.GammaMax) ||
                        !InRange(parameters[3], 0.0, 1.0))
                        return InvalidCurve(out values, out error);
                    values["mode"] = "synchronous";
                    values["syncSpeed"] = parameters[0];
                    values["motivity"] = parameters[1];
                    values["gamma"] = parameters[2];
                    values["smooth"] = parameters[3];
                    values["gain"] = false;
                    break;
            }
            return true;
        }

        private static bool InRange(double value, double min, double max)
        {
            return value >= min && value <= max;
        }

        private static bool InvalidCurve(out Dictionary<string, object> values, out string error)
        {
            values = new Dictionary<string, object>();
            error = "The fitted acceleration curve contains a parameter outside RawAccel's supported range.";
            return false;
        }

        // ─────────────────────────────────────────────────────────────────────
        // LUT support.
        //
        // RawAccel's lut accel mode stores a piecewise-linear curve as a
        // flat array of (x, y) pairs in `AccelArgs.data` (a float[] of
        // fixed capacity LUT_RAW_DATA_CAPACITY = 514, i.e. up to 257
        // points), with `AccelArgs.length` set to the number of ENTRIES
        // (= 2 * point_count). The driver's lookup function
        // (rawaccel/common/accel-lookup.hpp) does a binary search on x
        // (so x MUST be strictly ascending — the validate.hpp check
        // only enforces point count and positivity, not ordering, but a
        // non-ascending x would silently produce wrong output).
        //
        // The x value is the input speed seen by the lookup function.
        // From rawaccel/common/rawaccel.hpp:
        //     double ips_factor = dpi_factor / time;
        //     abs_weighted_vel.x = |in.x * ips_factor * args.domain_weights.x|
        // and dpi_factor = NORMALIZED_DPI / cfg.dpi (driver.cpp:376).
        // With domain_weights.x = 1 (the default for the X axis), the
        // speed seen by the lookup is:
        //     x_lut = counts_in_ms * (NORMALIZED_DPI / cfg.dpi)
        //
        // Our calibration stores speeds in cm/s (TryBuildCurveSample
        // produces InputSpeedCmPerSec). To convert cm/s to the LUT x
        // unit:
        //     counts_in_ms = (cm/s) * (DPI / 2.54) / 1000   [2.54 cm/in]
        //     x_lut = counts_in_ms * (NORMALIZED_DPI / DPI)
        //          = (cm/s) * (DPI / 2.54) / 1000 * (1000 / DPI)
        //          = (cm/s) / 2.54
        //
        // The DPI cancels out. The LUT x value is simply cm/s divided
        // by 2.54 (centimeters per inch), independent of the mouse's
        // DPI. This is the conversion used below.
        //
        // The y value is the sensitivity multiplier when gain=false
        // (LutApplyType.Sensitivity). With gain=true the lookup
        // returns velocity, with gain=false it returns sensitivity
        // (see accel-lookup.hpp: `if (velocity) y /= x;`). The
        // CellAccelerationFactor is a sensitivity ratio, so we use
        // gain=false.
        //
        // Per-point isolation: the caller (BuildDirection.BuildLutPoints)
        // already emits one point per green cell, in ascending speed
        // order. To preserve per-cell isolation at the LUT level, we
        // emit TWO points at each cell boundary: (x_boundary,
        // previous_factor) and (x_boundary + epsilon, new_factor). The
        // boundary is the geometric mean of the speeds of two
        // neighbouring green cells (geometric mean on log scale because
        // RawAccel's LUT x values span several orders of magnitude).
        // This zero-order-hold staircase means RawAccel's linear
        // interpolation between points produces the previous factor up
        // to x_boundary and the new factor from x_boundary + epsilon
        // onward. Adding a new cell can only APPEND a new
        // (boundary, epsilon, factor) pair; it cannot change any
        // earlier point.
        //
        // Below the lowest green cell and above the highest green
        // cell, the LUT must return factor = 1.0 (no correction). We
        // achieve this by prepending (x_min, 1.0) and appending
        // (x_max, last_factor or 1.0). x_min and x_max are chosen far
        // outside the realistic mouse speed range (1e-6 and 1e6 in
        // LUT units = (cm/s)/2.54) so the LUT returns 1.0 for any
        // realistic input outside the green range. The validate.hpp
        // check requires at least 2 points (length >= 4); we always
        // emit at least the (x_min, 1.0) and (x_max, 1.0) bookends
        // when there are no green cells, but in that case the caller
        // treats the apply as a no-op and never writes a LUT — see
        // DesktopSensConfigForm.TryApplyTrainDirectionalFactors.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Driver-side DPI normalization constant. From
        /// rawaccel/common/rawaccel-base.hpp: NORMALIZED_DPI = 1000.
        /// </summary>
        public const double DriverNormalizedDPI = 1000.0;

        /// <summary>
        /// Centimeters per inch. Same value as
        /// DesktopSensConstants.InchesToCm.
        /// </summary>
        public const double CmPerInch = 2.54;

        /// <summary>
        /// RawAccel's LUT_RAW_DATA_CAPACITY = 514 (rawaccel-base.hpp).
        /// The AccelArgs.data array has this fixed size; the LUT
        /// length (in entries) must be at most this.
        /// </summary>
        public const int LutRawDataCapacity = 514;

        /// <summary>
        /// RawAccel's LUT_POINTS_CAPACITY = LUT_RAW_DATA_CAPACITY / 2 = 257
        /// (rawaccel-base.hpp). The maximum number of (x, y) points
        /// that fit in the LUT.
        /// </summary>
        public const int LutPointsCapacity = 257;

        /// <summary>
        /// Minimum LUT entries (4 = 2 points) required by validate.hpp:
        ///     if (args.length < 4) error("lookup mode requires at least 2 points");
        /// </summary>
        public const int LutMinEntries = 4;

        /// <summary>
        /// CLAUDE FIX (round 2, directive #3) — Look up the live LUT
        /// factor at the given mouse speed (cm/s) by reading the
        /// ACTIVELY-APPLIED RawAccel Profile (typically
        /// _accelGUI.Settings.ActiveProfile), NOT the merged profile
        /// cache. This guarantees the lookup matches exactly what the
        /// driver is currently applying.
        ///
        /// Behaviour mirrors accel-lookup.hpp's lookup::operator() with
        /// gain=false (sensitivity mode):
        ///   - If argsX/argsY.mode != "lut", return 1.0 (no LUT active).
        ///   - If the speed's LUT x value is &lt;= 0, return 0 (matches
        ///     the driver's `if (x &lt;= 0) return 0;` early-out — but
        ///     the caller treats 0 as "no LUT active", so we return
        ///     1.0 here to keep the chain stable; a 0 from the driver
        ///     would mean "no mouse motion", not "no LUT").
        ///   - Binary search the LUT points for the segment containing
        ///     x (using the same lo/hi loop as accel-lookup.hpp).
        ///   - If an exact match is found, return that point's y.
        ///   - If x is below the first point, return the first point's
        ///     y (matches the driver's `if (lo == 0) return points[0].y;`
        ///     fallback).
        ///   - Otherwise linearly interpolate between points[lo-1] and
        ///     points[lo] using t = (x - a.x) / (b.x - a.x).
        ///   - gain is always false here (sensitivity mode), so we do
        ///     NOT divide by x.
        ///
        /// The function is defensive: any missing field, wrong type, or
        /// non-finite value returns 1.0 (the safe "no correction"
        /// default — the caller treats this as "no LUT was active at
        /// this trial's speed").
        /// </summary>
        /// <param name="activeProfile">The active RawAccel Profile
        /// (typically _accelGUI.Settings.ActiveProfile). May be null
        /// (returns 1.0).</param>
        /// <param name="speedCmPerSec">The trial's average speed in
        /// cm/s. Must be positive and finite.</param>
        /// <param name="axis">"X" for Right/Left (argsX), "Y" for Up/Down
        /// (argsY). Anything else returns 1.0.</param>
        public static double LookupActiveLutFactorFromProfile(
            object activeProfile,
            double speedCmPerSec,
            string axis)
        {
            if (activeProfile == null) return 1.0;
            if (double.IsNaN(speedCmPerSec) || double.IsInfinity(speedCmPerSec) ||
                speedCmPerSec <= 0.0) return 1.0;

            try
            {
                var profileType = activeProfile.GetType();
                var flags = BindingFlags.Instance | BindingFlags.Public;
                string fieldName = axis == "Y" ? "argsY" : "argsX";
                var argsField = profileType.GetField(fieldName, flags);
                if (argsField == null) return 1.0;
                object args = argsField.GetValue(activeProfile);
                if (args == null) return 1.0;

                // Read mode (must be "lut").
                var modeField = args.GetType().GetField("mode", flags);
                if (modeField == null) return 1.0;
                object modeVal = modeField.GetValue(args);
                if (modeVal == null || modeVal.ToString() != "lut") return 1.0;

                // Read gain (we use sensitivity mode = gain=false; if
                // gain is true the LUT returns velocity and the caller
                // would need to divide by x, but the applier always
                // writes gain=false — see TryBuildLutValues. We treat
                // any other gain value as "not a sensitivity LUT" and
                // return 1.0 defensively.)
                var gainField = args.GetType().GetField("gain", flags);
                if (gainField != null)
                {
                    object gainVal = gainField.GetValue(args);
                    if (gainVal is bool b && b) return 1.0;
                }

                // Read length and data.
                var lengthField = args.GetType().GetField("length", flags);
                if (lengthField == null) return 1.0;
                object lengthVal = lengthField.GetValue(args);
                if (!(lengthVal is int length) || length < LutMinEntries) return 1.0;
                if (length > LutRawDataCapacity) length = LutRawDataCapacity; // defensive

                var dataField = args.GetType().GetField("data", flags);
                if (dataField == null) return 1.0;
                object dataVal = dataField.GetValue(args);
                if (!(dataVal is float[] data) || data == null) return 1.0;
                if (data.Length < length) return 1.0; // data must be at least length entries

                int pointCount = length / 2;
                if (pointCount < 2) return 1.0;

                // Convert cm/s to LUT x units (cm/s / 2.54 — DPI cancels,
                // see ConvertCmPerSecToLutX).
                double x = speedCmPerSec / CmPerInch;
                if (!(x > 0)) return 1.0;

                // Binary search identical to accel-lookup.hpp:
                //   int lo = 0; int hi = size - 2;
                //   while (lo <= hi) { ... }
                // The lookup.hpp uses points[i].x and points[i].y
                // where points is a vec2<float>* cast of args.data.
                // We access the same data via data[2*i] (x) and
                // data[2*i+1] (y).
                int lo = 0;
                int hi = pointCount - 2;

                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    float px = data[2 * mid];
                    float py = data[2 * mid + 1];

                    if (x < px)
                    {
                        hi = mid - 1;
                    }
                    else if (x > px)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        // Exact match.
                        if (!(py > 0.0)) return 1.0;
                        return py;
                    }
                }

                // accel-lookup.hpp fallback:
                //   if (lo > 0) { linear interpolation between
                //     points[lo-1] and points[lo] }
                //   else { return points[0].y }
                if (lo > 0)
                {
                    float ax = data[2 * (lo - 1)];
                    float ay = data[2 * (lo - 1) + 1];
                    float bx = data[2 * lo];
                    float by = data[2 * lo + 1];
                    if (!(ax > 0) || !(ay > 0) || !(bx > 0) || !(by > 0)) return 1.0;
                    double dx = bx - ax;
                    if (Math.Abs(dx) < 1e-12) return ay; // degenerate
                    double t = (x - ax) / dx;
                    // accel-lookup.hpp uses `lerp(a.y, b.y, t)` which is
                    // `a.y + t * (b.y - a.y)` with a clamping tweak.
                    // For sensitivity mode we do NOT divide by x (gain
                    // is false).
                    double y = ay + t * (by - ay);
                    if (!(y > 0.0)) return 1.0;
                    return y;
                }

                // lo == 0: x is below the first point. accel-lookup.hpp
                // returns points[0].y here. We return 1.0 because the
                // LUT's first point is the xMin bookend with y=1.0
                // (see TryBuildLutValues), so points[0].y IS 1.0 in a
                // correctly-built LUT. But to be safe, return the
                // actual y value if it is positive.
                float y0 = data[1];
                if (y0 > 0.0) return y0;
                return 1.0;
            }
            catch
            {
                return 1.0;
            }
        }

        /// <summary>
        /// Convert a physical mouse speed in cm/s to RawAccel's LUT x
        /// unit (normalized counts/ms with domain_weight = 1).
        ///
        /// Proof that DPI cancels out:
        ///   counts_per_ms = (cm/s) * (DPI / 2.54) / 1000
        ///   x_lut = counts_per_ms * (NORMALIZED_DPI / DPI)
        ///        = (cm/s) * (DPI / 2.54) / 1000 * (1000 / DPI)
        ///        = (cm/s) / 2.54
        ///
        /// So x_lut = (cm/s) / 2.54, independent of the mouse's DPI.
        /// </summary>
        public static double ConvertCmPerSecToLutX(double speedCmPerSec)
        {
            return speedCmPerSec / CmPerInch;
        }

        /// <summary>
        /// Build the AccelArgs nested-field values for a LUT-mode
        /// acceleration curve from a set of (speed, factor) LUT
        /// points collected from green calibration cells.
        ///
        /// The returned dictionary is suitable for
        /// RawAccelSettingsBridge.TrySetNestedFields(settings,
        /// "argsX" or "argsY", values, out error).
        ///
        /// Returns false (with `values` cleared and `error` set) when
        /// the input points violate RawAccel's LUT constraints:
        ///   - fewer than 1 green point (the caller handles this as a
        ///     no-op separately; this function still fails so the
        ///     caller does NOT accidentally write an empty LUT)
        ///   - too many green points (> 257/2 = 128 cells, because each
        ///     cell emits 2 boundary points + the 2 bookends; if the
        ///     green count exceeds the LUT point capacity, we refuse)
        ///   - non-finite speeds or factors
        ///   - non-positive factors (y must be > 0 per validate.hpp)
        ///   - x values that are not strictly ascending after the
        ///     boundary/epsilon expansion (this would silently
        ///     produce wrong lookup output — the validate.hpp does not
        ///     check this, so we check it here)
        ///
        /// When this function returns true, the resulting LUT is a
        /// zero-order-hold staircase: at each cell boundary the factor
        /// jumps from the previous cell's factor to the new cell's
        /// factor, with a tiny epsilon gap so RawAccel's linear
        /// interpolation between the two boundary points produces the
        /// jump at exactly x_boundary (rather than a smooth ramp).
        /// </summary>
        /// <param name="points">LUT points (speed in cm/s, sensitivity
        /// factor) from green cells, in ascending distance-zone
        /// order.</param>
        /// <param name="values">Output dictionary with keys: "mode"
        /// (= "lut"), "gain" (= false), "length" (int, = 2 * point count),
        /// "data" (float[] of size LutRawDataCapacity, padded with
        /// zeros after the active entries).</param>
        /// <param name="error">Error message on failure.</param>
        public static bool TryBuildLutValues(
            IList<LutPoint> points,
            out Dictionary<string, object> values,
            out string error,
            LutUpperBookendMode lutUpperBookend = LutUpperBookendMode.NeutralOne)
        {
            values = new Dictionary<string, object>();
            error = null;

            if (points == null || points.Count == 0)
            {
                error = "No green calibration cells — LUT not written.";
                return false;
            }

            // CLAUDE FIX (round 2, directive #5) — TryBuildLutValues now
            // accepts the (boundary, factor) points emitted by
            // CombineLutPointsForAxis DIRECTLY, without re-interpreting
            // the boundary as a cell center. The combine step already
            // produced the staircase output (one point per boundary at
            // the boundary speed with the combined factor for that
            // interval). We just:
            //   1. Sort by speed (defensive — combine already sorts).
            //   2. Validate (finite, positive, strictly ascending).
            //   3. Prepend an xMin bookend with y=1.0 so the LUT
            //      returns 1.0 below the first combined point.
            //   4. Append an xMax bookend with the last point's factor
            //      so the LUT extends the last factor to +infinity.
            //   5. Write to the data array.
            //
            // The combine step emits hold anchors immediately before
            // each next boundary. RawAccel interpolates linearly, so the
            // factor is constant over nearly the whole interval and changes
            // only across a narrow transition at the boundary.
            var sorted = new List<LutPoint>(points);
            sorted.Sort((a, b) => a.InputSpeedCmPerSec.CompareTo(b.InputSpeedCmPerSec));

            // Validate input points: finite, positive factor, positive
            // speed. Reject (do NOT fall back) any point that fails —
            // Claude directive #2 removed the "stick to previous x"
            // fallback because it produced silently wrong LUTs.
            for (int i = 0; i < sorted.Count; i++)
            {
                var p = sorted[i];
                if (double.IsNaN(p.InputSpeedCmPerSec) || double.IsInfinity(p.InputSpeedCmPerSec) ||
                    p.InputSpeedCmPerSec <= 0.0)
                {
                    error = "LUT point " + i + " has non-finite or non-positive speed (" +
                            p.InputSpeedCmPerSec.ToString("0.######") + "). LUT not written.";
                    return false;
                }
                if (double.IsNaN(p.Factor) || double.IsInfinity(p.Factor) || p.Factor <= 0.0)
                {
                    error = "LUT point " + i + " has non-finite or non-positive factor (" +
                            p.Factor.ToString("0.######") + "). LUT not written.";
                    return false;
                }
                if (i > 0 && sorted[i].InputSpeedCmPerSec <= sorted[i - 1].InputSpeedCmPerSec)
                {
                    error = "LUT points " + (i - 1) + " and " + i +
                            " have non-ascending speeds (" +
                            sorted[i - 1].InputSpeedCmPerSec.ToString("0.######") +
                            " vs " + sorted[i].InputSpeedCmPerSec.ToString("0.######") +
                            "). CombineLutPointsForAxis should have merged these. LUT not written.";
                    return false;
                }
            }

            // Build the LUT point list as (x, y) pairs in LUT units.
            // We add 2 bookends (xMin, 1.0) and (xMax, lastFactor).
            int totalPoints = sorted.Count + 2;
            if (totalPoints > LutPointsCapacity)
            {
                error = "Too many LUT points (" + totalPoints + " > " + LutPointsCapacity +
                        ") — combine emitted too many boundaries.";
                return false;
            }
            var lutPoints = new List<(double x, double y)>(totalPoints);

            // xMin bookend: factor = 1.0 (no correction below the
            // first combined boundary). xMin is far below any realistic
            // mouse speed.
            const double xMin = 1e-6;
            lutPoints.Add((xMin, 1.0));

            // The combine step's points, converted to LUT x units.
            foreach (var p in sorted)
            {
                double x = ConvertCmPerSecToLutX(p.InputSpeedCmPerSec);
                lutPoints.Add((x, p.Factor));
            }

            // xMax bookend: place it immediately after the final calibrated
            // speed. A distant sentinel (for example 1e6) would create a long
            // linear ramp from the last calibrated factor toward 1.0, applying
            // untested corrections over a huge speed range instead of using the
            // configured upper-bookend policy.
            double lastX = ConvertCmPerSecToLutX(sorted[sorted.Count - 1].InputSpeedCmPerSec);
            double xMax = lastX + Math.Max(1e-6, Math.Abs(lastX) * 1e-4);
            double lastFactor = lutUpperBookend == LutUpperBookendMode.ExtendLastFactor
                ? sorted[sorted.Count - 1].Factor
                : 1.0;
            lutPoints.Add((xMax, lastFactor));

            // Sanity: strictly ascending x. (The validate.hpp does
            // NOT check this — it would silently produce wrong lookup
            // output via binary search. We check it here so we never
            // write a malformed LUT.)
            for (int i = 1; i < lutPoints.Count; i++)
            {
                if (!(lutPoints[i].x > lutPoints[i - 1].x))
                {
                    error = "LUT x values are not strictly ascending at index " + i +
                            " (x[" + (i - 1) + "]=" + lutPoints[i - 1].x.ToString("0.######") +
                            ", x[" + i + "]=" + lutPoints[i].x.ToString("0.######") + ").";
                    return false;
                }
                if (!(lutPoints[i].y > 0.0))
                {
                    error = "LUT y value at index " + i + " is not positive.";
                    return false;
                }
            }

            // Pack into the data array. The AccelArgs.data field is a
            // float[] of size LUT_RAW_DATA_CAPACITY = 514. We fill the
            // first 2 * point_count entries with (x, y) pairs and pad
            // the rest with zeros. `length` is the number of ENTRIES
            // (i.e. 2 * point_count), NOT the number of points.
            int pointCount = lutPoints.Count;
            int entryCount = pointCount * 2;
            if (entryCount > LutRawDataCapacity)
            {
                error = "LUT entry count " + entryCount + " exceeds capacity " + LutRawDataCapacity + ".";
                return false;
            }
            if (entryCount < LutMinEntries)
            {
                error = "LUT entry count " + entryCount + " is below minimum " + LutMinEntries + ".";
                return false;
            }

            var data = new float[LutRawDataCapacity];
            for (int i = 0; i < pointCount; i++)
            {
                data[2 * i] = (float)lutPoints[i].x;
                data[2 * i + 1] = (float)lutPoints[i].y;
            }
            // Remaining entries default to 0 (float array initialization).

            values["mode"] = "lut";
            values["gain"] = false; // y = sensitivity factor (NOT velocity)
            values["length"] = entryCount;
            values["data"] = data;
            return true;
        }

        /// <summary>
        /// Combine two LUT point lists for the same axis (e.g. Right
        /// + Left for argsX, Up + Down for argsY) into a single LUT
        /// point list of (boundary, factor) pairs.
        ///
        /// CLAUDE FIX (round 2, directive #5) — the combine step now
        /// emits (boundary, factor) pairs directly. Each pair represents
        /// "at this boundary speed, the combined factor becomes the
        /// given value". The caller (TryBuildLutValues) writes these
        /// directly into the LUT data array — there is NO
        /// re-interpretation of the boundary as a cell center.
        ///
        /// Algorithm:
        ///   1. If only one direction has green cells, build that direction's
        ///      LUT and explicitly warn that the shared axis LUT affects BOTH signs.
        ///   2. If both directions have cells, walk the union of their interval
        ///      boundaries. When both factors differ by at most 5%, use their
        ///      geometric mean. When they differ by more than 5%, use the
        ///      direction with the higher mean confidence and report the speed,
        ///      both factors, and selected direction. If only one direction has
        ///      an active range at an interval, use that available factor.
        ///   3. If neither direction has green cells, return an empty point list
        ///      with an explicit no-op note.
        ///
        /// RawAccel has one shared LUT per axis. The returned curve therefore
        /// affects both signs; the note is surfaced in the configuration status
        /// and log so the user is not led to believe the directions are isolated.
        /// </summary>
        public static List<LutPoint> CombineLutPointsForAxis(
            IList<LutPoint> positiveDirection,
            IList<LutPoint> negativeDirection,
            out string combinationNote,
            double positiveConfidence = 0.0,
            double negativeConfidence = 0.0,
            string positiveDirectionName = "positive",
            string negativeDirectionName = "negative")
        {
            combinationNote = "";

            bool hasPos = positiveDirection != null && positiveDirection.Count > 0;
            bool hasNeg = negativeDirection != null && negativeDirection.Count > 0;

            if (!hasPos && !hasNeg)
            {
                combinationNote = "no green cells on either direction — nothing to apply";
                return new List<LutPoint>();
            }

            positiveConfidence = NormalizeConfidence(positiveConfidence);
            negativeConfidence = NormalizeConfidence(negativeConfidence);

            if (hasPos != hasNeg)
            {
                string calibratedName = hasPos ? positiveDirectionName : negativeDirectionName;
                IList<LutPoint> calibratedPoints = hasPos ? positiveDirection : negativeDirection;
                var oneSided = BuildSingleDirectionLutPoints(calibratedPoints);
                combinationNote = "Only " + calibratedName +
                    " calibrated. The shared axis LUT will affect BOTH directions until the opposite direction is calibrated too.";
                return oneSided;
            }

            var posBoundaries = BuildBoundariesForDirection(positiveDirection);
            var negBoundaries = BuildBoundariesForDirection(negativeDirection);
            var allBoundaries = new SortedSet<double>();
            foreach (var b in posBoundaries) allBoundaries.Add(b);
            foreach (var b in negBoundaries) allBoundaries.Add(b);

            var sortedBoundaries = new List<double>(allBoundaries);
            var combined = new List<LutPoint>();
            if (sortedBoundaries.Count > 0)
            {
                double firstBoundary = sortedBoundaries[0];
                double neutralAnchor = firstBoundary * (1.0 - 1e-4);
                if (neutralAnchor <= 1e-6)
                {
                    combinationNote = "The first calibrated LUT boundary is too close to the minimum LUT input.";
                    return new List<LutPoint>();
                }
                combined.Add(new LutPoint(neutralAnchor, 1.0));
            }

            int sharedIntervals = 0;
            int conflictIntervals = 0;
            int oneSidedIntervals = 0;
            bool usedTieBlend = false;
            var conflictDetails = new List<string>();

            for (int i = 0; i < sortedBoundaries.Count; i++)
            {
                double boundary = sortedBoundaries[i];
                if (boundary <= 0) continue;

                double evalSpeed = i + 1 < sortedBoundaries.Count
                    ? boundary + (sortedBoundaries[i + 1] - boundary) * 0.5
                    : boundary * 1.0001;

                double posFactor = LookupDirectionFactor(positiveDirection, posBoundaries, evalSpeed);
                double negFactor = LookupDirectionFactor(negativeDirection, negBoundaries, evalSpeed);
                double posIntervalConfidence = LookupDirectionConfidence(
                    positiveDirection, evalSpeed, positiveConfidence);
                double negIntervalConfidence = LookupDirectionConfidence(
                    negativeDirection, evalSpeed, negativeConfidence);
                bool posActive = posFactor > 0.0 && !double.IsNaN(posFactor) && !double.IsInfinity(posFactor);
                bool negActive = negFactor > 0.0 && !double.IsNaN(negFactor) && !double.IsInfinity(negFactor);

                if (!posActive && !negActive)
                    continue;

                double combinedFactor;
                double combinedConfidence;
                if (posActive && !negActive)
                {
                    combinedFactor = posFactor;
                    combinedConfidence = posIntervalConfidence;
                    oneSidedIntervals++;
                }
                else if (!posActive && negActive)
                {
                    combinedFactor = negFactor;
                    combinedConfidence = negIntervalConfidence;
                    oneSidedIntervals++;
                }
                else
                {
                    double relativeDifference = Math.Abs(posFactor - negFactor) /
                        Math.Max(Math.Abs(posFactor), Math.Abs(negFactor));

                    if (relativeDifference <= 0.05)
                    {
                        combinedFactor = Math.Sqrt(posFactor * negFactor);
                        combinedConfidence = 0.5 * (posIntervalConfidence + negIntervalConfidence);
                        sharedIntervals++;
                    }
                    else
                    {
                        conflictIntervals++;
                        if (posIntervalConfidence > negIntervalConfidence)
                        {
                            combinedFactor = posFactor;
                            combinedConfidence = posIntervalConfidence;
                            conflictDetails.Add(positiveDirectionName + " and " + negativeDirectionName +
                                " factors conflict at " + boundary.ToString("0.###") +
                                " cm/s (" + positiveDirectionName + "=" + posFactor.ToString("0.###") +
                                ", " + negativeDirectionName + "=" + negFactor.ToString("0.###") +
                                "). Applied " + positiveDirectionName + " (higher confidence " +
                                posIntervalConfidence.ToString("0.###") + ").");
                        }
                        else if (negIntervalConfidence > posIntervalConfidence)
                        {
                            combinedFactor = negFactor;
                            combinedConfidence = negIntervalConfidence;
                            conflictDetails.Add(positiveDirectionName + " and " + negativeDirectionName +
                                " factors conflict at " + boundary.ToString("0.###") +
                                " cm/s (" + positiveDirectionName + "=" + posFactor.ToString("0.###") +
                                ", " + negativeDirectionName + "=" + negFactor.ToString("0.###") +
                                "). Applied " + negativeDirectionName + " (higher confidence " +
                                negIntervalConfidence.ToString("0.###") + ").");
                        }
                        else
                        {
                            // Equal confidence at this speed interval gives no
                            // defensible winner; retain a deterministic neutral blend.
                            combinedFactor = Math.Sqrt(posFactor * negFactor);
                            combinedConfidence = posIntervalConfidence;
                            usedTieBlend = true;
                            conflictDetails.Add(positiveDirectionName + " and " + negativeDirectionName +
                                " factors conflict at " + boundary.ToString("0.###") +
                                " cm/s (" + positiveDirectionName + "=" + posFactor.ToString("0.###") +
                                ", " + negativeDirectionName + "=" + negFactor.ToString("0.###") +
                                "); interval confidences tied at " + posIntervalConfidence.ToString("0.###") +
                                ", so geometric mean was used.");
                        }
                    }
                }

                combined.Add(new LutPoint(boundary, combinedFactor, combinedConfidence));

                if (i + 1 < sortedBoundaries.Count)
                {
                    double nextBoundary = sortedBoundaries[i + 1];
                    double interval = nextBoundary - boundary;
                    if (interval > 0.0)
                    {
                        double holdEnd = nextBoundary - interval * 1e-4;
                        if (holdEnd > boundary)
                            combined.Add(new LutPoint(holdEnd, combinedFactor, combinedConfidence));
                    }
                }
            }

            combined.Sort((a, b) => a.InputSpeedCmPerSec.CompareTo(b.InputSpeedCmPerSec));

            var notes = new List<string>();
            if (conflictIntervals > 0)
            {
                notes.AddRange(conflictDetails);
                if (usedTieBlend)
                    notes.Add("Equal-confidence conflicts use the geometric mean because neither direction has higher confidence.");
            }
            if (oneSidedIntervals > 0)
                notes.Add(oneSidedIntervals + " interval(s) used the only direction with a calibrated range.");
            if (sharedIntervals > 0)
                notes.Add(sharedIntervals + " interval(s) matched within 5%; geometric mean applied.");
            if (notes.Count == 0)
                notes.Add("no overlapping calibrated intervals");

            combinationNote = "RawAccel has one shared LUT per axis; this curve affects BOTH directions. " +
                string.Join(" ", notes);
            return combined;
        }

        /// <summary>
        /// Merge two continuous sample-fit curves on their shared speed domain.
        /// Unlike CombineLutPointsForAxis (the legacy cell staircase), this
        /// preserves linear interpolation between fitted nodes.
        /// </summary>
        public static List<LutPoint> CombineSampleFitLutPointsForAxis(
            IList<LutPoint> positiveDirection,
            IList<LutPoint> negativeDirection,
            out string combinationNote,
            string positiveDirectionName = "positive",
            string negativeDirectionName = "negative")
        {
            combinationNote = "";
            bool hasPos = positiveDirection != null && positiveDirection.Count > 0;
            bool hasNeg = negativeDirection != null && negativeDirection.Count > 0;
            if (!hasPos && !hasNeg)
            {
                combinationNote = "no sample-fit curve on either direction";
                return new List<LutPoint>();
            }
            if (hasPos != hasNeg)
            {
                var source = hasPos ? positiveDirection : negativeDirection;
                combinationNote = "Only " + (hasPos ? positiveDirectionName : negativeDirectionName) +
                    " has a sample-fit curve; the shared axis LUT will affect both directions.";
                return new List<LutPoint>(source);
            }

            var pos = new List<LutPoint>(positiveDirection);
            var neg = new List<LutPoint>(negativeDirection);
            pos.Sort((a, b) => a.InputSpeedCmPerSec.CompareTo(b.InputSpeedCmPerSec));
            neg.Sort((a, b) => a.InputSpeedCmPerSec.CompareTo(b.InputSpeedCmPerSec));
            double min = Math.Max(pos[0].InputSpeedCmPerSec, neg[0].InputSpeedCmPerSec);
            double max = Math.Min(pos[pos.Count - 1].InputSpeedCmPerSec,
                                  neg[neg.Count - 1].InputSpeedCmPerSec);
            if (!IsFinite(min) || !IsFinite(max) || min <= 0.0 || max <= min)
            {
                combinationNote = "Sample-fit curves have no shared speed interval; no combined curve was emitted.";
                return new List<LutPoint>();
            }

            var speeds = new SortedSet<double> { min, max };
            foreach (var p in pos)
                if (p.InputSpeedCmPerSec > min && p.InputSpeedCmPerSec < max)
                    speeds.Add(p.InputSpeedCmPerSec);
            foreach (var p in neg)
                if (p.InputSpeedCmPerSec > min && p.InputSpeedCmPerSec < max)
                    speeds.Add(p.InputSpeedCmPerSec);

            var result = new List<LutPoint>(speeds.Count);
            foreach (double speed in speeds)
            {
                double pf = EvaluateContinuousFactor(pos, speed);
                double nf = EvaluateContinuousFactor(neg, speed);
                if (!IsFinite(pf) || pf <= 0.0 || !IsFinite(nf) || nf <= 0.0)
                    continue;
                result.Add(new LutPoint(speed, Math.Sqrt(pf * nf), 1.0));
            }
            combinationNote = "Continuous sample-fit curves combined geometrically on their shared speed interval.";
            return result;
        }

        private static double EvaluateContinuousFactor(IList<LutPoint> points, double speed)
        {
            if (points == null || points.Count == 0 || !IsFinite(speed) || speed <= 0.0)
                return double.NaN;
            if (speed <= points[0].InputSpeedCmPerSec) return points[0].Factor;
            int last = points.Count - 1;
            if (speed >= points[last].InputSpeedCmPerSec) return points[last].Factor;
            for (int i = 1; i <= last; i++)
            {
                if (speed > points[i].InputSpeedCmPerSec) continue;
                double x0 = points[i - 1].InputSpeedCmPerSec;
                double x1 = points[i].InputSpeedCmPerSec;
                if (x1 <= x0) return double.NaN;
                double t = (speed - x0) / (x1 - x0);
                return points[i - 1].Factor * (1.0 - t) + points[i].Factor * t;
            }
            return points[last].Factor;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double NormalizeConfidence(double confidence)
        {
            if (double.IsNaN(confidence) || double.IsInfinity(confidence) || confidence < 0.0)
                return 0.0;
            return Math.Min(1.0, confidence);
        }

        private static List<LutPoint> BuildSingleDirectionLutPoints(IList<LutPoint> points)
        {
            var boundaries = BuildBoundariesForDirection(points);
            var result = new List<LutPoint>();
            if (boundaries.Count == 0) return result;

            double firstBoundary = boundaries[0];
            double neutralAnchor = firstBoundary * (1.0 - 1e-4);
            if (neutralAnchor <= 1e-6) return result;
            result.Add(new LutPoint(neutralAnchor, 1.0));

            for (int i = 0; i < boundaries.Count; i++)
            {
                double boundary = boundaries[i];
                if (boundary <= 0.0) continue;
                double evalSpeed = i + 1 < boundaries.Count
                    ? boundary + (boundaries[i + 1] - boundary) * 0.5
                    : boundary * 1.0001;
                double factor = LookupDirectionFactor(points, boundaries, evalSpeed);
                if (!(factor > 0.0) || double.IsNaN(factor) || double.IsInfinity(factor))
                    continue;

                result.Add(new LutPoint(boundary, factor, LookupDirectionConfidence(points, evalSpeed, 0.0)));
                if (i + 1 < boundaries.Count)
                {
                    double nextBoundary = boundaries[i + 1];
                    double interval = nextBoundary - boundary;
                    if (interval > 0.0)
                    {
                        double holdEnd = nextBoundary - interval * 1e-4;
                        if (holdEnd > boundary)
                            result.Add(new LutPoint(holdEnd, factor, LookupDirectionConfidence(points, evalSpeed, 0.0)));
                    }
                }
            }

            result.Sort((a, b) => a.InputSpeedCmPerSec.CompareTo(b.InputSpeedCmPerSec));
            return result;
        }

        /// <summary>
        /// Build the list of boundary speeds (in cm/s) for one
        /// direction's LUT points. The boundaries are:
        ///   - 0.5 * lowest_speed (the first cell's lower edge)
        ///   - geometric mean of every consecutive pair of speeds
        ///     (the boundary between cell i and i+1)
        ///   - each cell's own speed (for the anchor point)
        ///
        /// The returned list is sorted ascending and may contain
        /// duplicates (the caller uses a SortedSet to deduplicate).
        /// </summary>
        private static List<double> BuildBoundariesForDirection(IList<LutPoint> points)
        {
            var sorted = new List<LutPoint>(points);
            sorted.Sort((a, b) => a.InputSpeedCmPerSec.CompareTo(b.InputSpeedCmPerSec));

            var boundaries = new List<double>(sorted.Count * 2 + 1);
            if (sorted.Count == 0) return boundaries;

            // First cell's lower edge.
            boundaries.Add(Math.Max(sorted[0].InputSpeedCmPerSec * 0.5, 1e-9));

            for (int i = 0; i < sorted.Count; i++)
            {
                // The cell's own speed.
                if (sorted[i].InputSpeedCmPerSec > 0)
                    boundaries.Add(sorted[i].InputSpeedCmPerSec);

                // Boundary to the next cell (geometric mean).
                if (i < sorted.Count - 1)
                {
                    double s1 = sorted[i].InputSpeedCmPerSec;
                    double s2 = sorted[i + 1].InputSpeedCmPerSec;
                    double boundary = Math.Sqrt(s1 * s2);
                    if (!double.IsNaN(boundary) && !double.IsInfinity(boundary) && boundary > 0)
                        boundaries.Add(boundary);
                }
            }

            boundaries.Sort();
            return boundaries;
        }

        /// <summary>
        /// Look up the factor of one direction's LUT at the given
        /// speed (cm/s). Returns 0.0 when the speed is outside the
        /// direction's calibrated range (below the first cell's
        /// lower edge). Returns the cell's factor when the speed falls
        /// within a green cell's interval.
        ///
        /// CLAUDE FIX (round 2, directive #5) — this lookup uses the
        /// SAME staircase logic as CombineSingleDirection: each cell
        /// owns the interval [lower_i, lower_{i+1}) where lower_i is
        /// the cell's lower boundary (0.5*speed for the first cell,
        /// sqrt(s_prev * s_cur) for subsequent cells).
        /// </summary>
        private static double LookupDirectionFactor(
            IList<LutPoint> points,
            List<double> boundaries,
            double speedCmPerSec)
        {
            if (points == null || points.Count == 0) return 0.0;
            if (speedCmPerSec <= 0) return 0.0;

            var sorted = new List<LutPoint>(points);
            sorted.Sort((a, b) => a.InputSpeedCmPerSec.CompareTo(b.InputSpeedCmPerSec));

            // The first cell's lower edge.
            double firstLower = Math.Max(sorted[0].InputSpeedCmPerSec * 0.5, 1e-9);
            if (speedCmPerSec < firstLower) return 0.0;

            // Walk through cells; each cell i owns the interval
            // [lower_i, lower_{i+1}) where:
            //   lower_i = firstLower for i=0, else sqrt(s_{i-1} * s_i)
            for (int i = 0; i < sorted.Count; i++)
            {
                double sCur = sorted[i].InputSpeedCmPerSec;
                double lower = i == 0
                    ? firstLower
                    : Math.Sqrt(sorted[i - 1].InputSpeedCmPerSec * sCur);
                double upper = i == sorted.Count - 1
                    ? double.PositiveInfinity
                    : Math.Sqrt(sCur * sorted[i + 1].InputSpeedCmPerSec);
                if (speedCmPerSec >= lower && speedCmPerSec < upper)
                    return sorted[i].Factor;
            }
            return 0.0;
        }


        private static double LookupDirectionConfidence(
            IList<LutPoint> points,
            double speedCmPerSec,
            double fallbackConfidence)
        {
            if (points == null || points.Count == 0 || speedCmPerSec <= 0.0)
                return NormalizeConfidence(fallbackConfidence);

            var sorted = new List<LutPoint>(points);
            sorted.Sort((a, b) => a.InputSpeedCmPerSec.CompareTo(b.InputSpeedCmPerSec));
            double firstLower = Math.Max(sorted[0].InputSpeedCmPerSec * 0.5, 1e-9);
            if (speedCmPerSec < firstLower)
                return NormalizeConfidence(fallbackConfidence);

            for (int i = 0; i < sorted.Count; i++)
            {
                double currentSpeed = sorted[i].InputSpeedCmPerSec;
                double lower = i == 0
                    ? firstLower
                    : Math.Sqrt(sorted[i - 1].InputSpeedCmPerSec * currentSpeed);
                double upper = i == sorted.Count - 1
                    ? double.PositiveInfinity
                    : Math.Sqrt(currentSpeed * sorted[i + 1].InputSpeedCmPerSec);
                if (speedCmPerSec >= lower && speedCmPerSec < upper)
                {
                    return sorted[i].HasConfidence
                        ? NormalizeConfidence(sorted[i].Confidence)
                        : NormalizeConfidence(fallbackConfidence);
                }
            }
            return NormalizeConfidence(fallbackConfidence);
        }

        private static bool TryConvert(object value, Type targetType, out object converted)
        {
            converted = null;
            if (value == null)
                return !targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null;

            Type effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            try
            {
                if (effectiveType.IsInstanceOfType(value))
                {
                    converted = value;
                    return true;
                }

                if (effectiveType.IsEnum)
                {
                    converted = value is string
                        ? Enum.Parse(effectiveType, (string)value, true)
                        : Enum.ToObject(effectiveType, value);
                    return true;
                }

                converted = Convert.ChangeType(value, effectiveType, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// NEW (Model v6): build a per-axis LUT using the sample-fit path instead
        /// of the median-speed summary. The axis is "X" (Right/Left) or "Y"
        /// (Up/Down). Trial filtering already happened in the caller (only
        /// accepted trials with raw samples are passed).
        ///
        /// The result is a single combined LutPoint list; when both directions
        /// have samples, the fit runs once on the concatenated trial set so the
        /// shared axis LUT is a single coherent curve.
        /// </summary>
        public static bool TryBuildSampleFitLutValues(
            IList<DesktopSensTrial> trialsForAxis,
            LutUpperBookendMode upperBookend,
            out Dictionary<string, object> values,
            out string error,
            out int pointCount)
        {
            values = new Dictionary<string, object>();
            error = null;
            pointCount = 0;

            if (trialsForAxis == null || trialsForAxis.Count == 0)
            {
                error = "No eligible trials for this axis.";
                return false;
            }

            SampleLutFitter.FitResult fit = SampleLutFitter.Fit(
                trialsForAxis,
                DesktopSensConstants.SampleLutDefaultNodes,
                includeYellowCells: true);

            if (fit == null || !fit.IsValid)
            {
                error = "Sample-fit LUT failed: " + (fit?.FailureReason ?? "unknown");
                return false;
            }

            List<LutPoint> points = SampleLutFitter.ToLutPoints(fit);
            if (points.Count == 0)
            {
                error = "Sample-fit LUT produced no points.";
                return false;
            }

            pointCount = points.Count;
            return TryBuildLutValues(points, out values, out error, upperBookend);
        }
    }
}
