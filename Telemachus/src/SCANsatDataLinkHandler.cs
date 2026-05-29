using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Telemachus
{
    /// <summary>
    /// Surfaces SCANsat data — coverage bitfield, anomalies — alongside
    /// stock-backed surface helpers (elevation, biome) that don't actually
    /// require SCANsat. Pattern mirrors PrincipiaDataLinkHandler: reflection
    /// against the SCANsat assembly so the dependency is soft. When SCANsat
    /// isn't installed, scan.available returns false and the SCANsat-only
    /// keys (coverage, maskBitmap, anomalies) return null; the stock-backed
    /// keys (elevation, biome) still resolve.
    ///
    /// All access goes through SCANsat's public surface — no `internal`
    /// methods, no SCANsat fork required:
    ///   SCANutil.getData(CelestialBody) → SCANdata
    ///   SCANutil.GetCoverage(int, CelestialBody) → double
    ///   SCANdata.Coverage : Int16[360,180]
    ///   SCANdata.Anomalies : SCANanomaly[]
    ///   SCANanomaly: name, latitude, longitude, known, detail
    ///
    /// Bit layout in SCANdata.Coverage (from SCANsat's SCANtype):
    ///   1 = AltimetryLoRes  2 = AltimetryHiRes  8 = Biome
    ///   16 = Anomaly  32 = AnomalyDetail  128 = ResourceLoRes
    ///   256 = ResourceHiRes
    /// </summary>
    public class SCANsatDataLinkHandler : DataLinkHandler
    {
        static bool _searched;

        // SCANsat types
        static Type _utilType;       // SCANsat.SCANutil
        static Type _dataType;       // SCANsat.SCAN_Data.SCANdata
        static Type _anomalyType;    // SCANsat.SCAN_Data.SCANanomaly

        // SCANutil static methods
        static MethodInfo _getDataByBody;        // getData(CelestialBody)
        static MethodInfo _getCoverage;          // GetCoverage(int, CelestialBody)

        // SCANdata instance members
        static PropertyInfo _coverageProp;       // Coverage : Int16[,]
        static PropertyInfo _anomaliesProp;      // Anomalies : SCANanomaly[]

        // SCANanomaly fields/properties
        static FieldInfo _anomalyName;
        static FieldInfo _anomalyLat;
        static FieldInfo _anomalyLon;
        static FieldInfo _anomalyKnown;
        static FieldInfo _anomalyDetail;

        // SCANcontroller singleton (for scanner / known-vessel listings)
        static Type _controllerType;       // SCANsat.SCANcontroller
        static PropertyInfo _controllerSingleton; // static SCANcontroller controller { get; }
        static PropertyInfo _knownVesselsProp;    // public List<SCANvessel> Known_Vessels
        static Type _scanVesselType;       // SCANsat.SCANcontroller.SCANvessel
        static FieldInfo _svVesselField;          // Vessel vessel
        static FieldInfo _svBodyField;            // CelestialBody body
        static FieldInfo _svLatField;             // double latitude
        static FieldInfo _svLonField;             // double longitude
        static FieldInfo _svSensorsField;         // List<SCANsensor> sensors
        static Type _scanSensorType;       // SCANsat.SCANcontroller.SCANsensor
        static FieldInfo _ssTypeField;            // SCANtype sensor
        static FieldInfo _ssFovField;             // double fov
        static FieldInfo _ssMinAltField;          // double min_alt
        static FieldInfo _ssMaxAltField;          // double max_alt
        static FieldInfo _ssBestAltField;         // double best_alt
        static FieldInfo _ssInRangeField;         // bool inRange
        static FieldInfo _ssBestRangeField;       // bool bestRange

        // SCANvessel.trackColor — the combined ground-track tint SCANsat
        // paints, useful so the minimap matches the in-game overlay.
        static FieldInfo _svTrackColorField;      // Color32 trackColor

        // Private SCANcontroller method that returns the max effective FoV
        // (in degrees) across a vessel's sensors at its current altitude.
        // Already accounts for altitude scaling, surfscale, and the 20°
        // cap — i.e. the same width SCANsat uses to paint its own ground
        // tracks via drawGroundTrackTris. We reflect into it (rather than
        // re-implementing the formula client-side) so the wire value is
        // SCANsat's actual canonical number.
        static MethodInfo _getFovMethod;

        // SCANdata.HeightMapValue(int body, int lon, int lat, bool useTemp)
        static MethodInfo _heightMapValue;
        // SCANdata.Body — needed to drive HeightMapValue's first arg.
        static PropertyInfo _dataBodyProp;

        public SCANsatDataLinkHandler(FormatterProvider formatters)
            : base(formatters) { }

        static void Search()
        {
            if (_searched) return;
            _searched = true;

            foreach (var asm in AssemblyLoader.loadedAssemblies)
            {
                try
                {
                    var a = asm.assembly;
                    if (_utilType == null)
                        _utilType = a.GetType("SCANsat.SCANutil", false);
                    if (_dataType == null)
                        _dataType = a.GetType("SCANsat.SCAN_Data.SCANdata", false);
                    if (_anomalyType == null)
                        _anomalyType = a.GetType("SCANsat.SCAN_Data.SCANanomaly", false);
                    if (_controllerType == null)
                        _controllerType = a.GetType("SCANsat.SCANcontroller", false);
                    if (_scanVesselType == null)
                        _scanVesselType = a.GetType("SCANsat.SCANcontroller+SCANvessel", false);
                    if (_scanSensorType == null)
                        _scanSensorType = a.GetType("SCANsat.SCANcontroller+SCANsensor", false);
                }
                catch { }
            }

            if (_utilType == null)
            {
                PluginLogger.debug("SCANsat not found");
                return;
            }

            PluginLogger.debug("SCANsat detected: " + _utilType.Assembly.GetName().Version);

            var pubStatic = BindingFlags.Public | BindingFlags.Static;
            var pubInstance = BindingFlags.Public | BindingFlags.Instance;

            // SCANutil.getData(CelestialBody body) — there's an overload
            // taking string too; we use the CelestialBody one to avoid a
            // name → body lookup on SCANsat's side.
            _getDataByBody = _utilType.GetMethod(
                "getData", pubStatic, null,
                new Type[] { typeof(CelestialBody) }, null);

            _getCoverage = _utilType.GetMethod(
                "GetCoverage", pubStatic, null,
                new Type[] { typeof(int), typeof(CelestialBody) }, null);

            if (_dataType != null)
            {
                _coverageProp = _dataType.GetProperty("Coverage", pubInstance);
                _anomaliesProp = _dataType.GetProperty("Anomalies", pubInstance);
                _dataBodyProp = _dataType.GetProperty("Body", pubInstance);
                _heightMapValue = _dataType.GetMethod(
                    "HeightMapValue", pubInstance, null,
                    new Type[] { typeof(int), typeof(int), typeof(int), typeof(bool) },
                    null);
            }

            if (_anomalyType != null)
            {
                _anomalyName = _anomalyType.GetField("name", pubInstance);
                _anomalyLat = _anomalyType.GetField("latitude", pubInstance);
                _anomalyLon = _anomalyType.GetField("longitude", pubInstance);
                _anomalyKnown = _anomalyType.GetField("known", pubInstance);
                _anomalyDetail = _anomalyType.GetField("detail", pubInstance);
            }

            if (_controllerType != null)
            {
                _controllerSingleton = _controllerType.GetProperty(
                    "controller", pubStatic);
                _knownVesselsProp = _controllerType.GetProperty(
                    "Known_Vessels", pubInstance);
            }

            if (_scanVesselType != null)
            {
                _svVesselField = _scanVesselType.GetField("vessel", pubInstance);
                _svBodyField = _scanVesselType.GetField("body", pubInstance);
                _svLatField = _scanVesselType.GetField("latitude", pubInstance);
                _svLonField = _scanVesselType.GetField("longitude", pubInstance);
                _svSensorsField = _scanVesselType.GetField("sensors", pubInstance);
                _svTrackColorField = _scanVesselType.GetField("trackColor", pubInstance);
            }

            if (_controllerType != null && _scanVesselType != null)
            {
                var privInstance = BindingFlags.NonPublic | BindingFlags.Instance;
                _getFovMethod = _controllerType.GetMethod(
                    "getFOV",
                    privInstance,
                    null,
                    new Type[] { _scanVesselType, typeof(CelestialBody) },
                    null);
                if (_getFovMethod == null)
                {
                    PluginLogger.debug(
                        "SCANcontroller.getFOV(SCANvessel, CelestialBody) not " +
                        "found — ground-track width will be omitted.");
                }
            }

            if (_scanSensorType != null)
            {
                _ssTypeField = _scanSensorType.GetField("sensor", pubInstance);
                _ssFovField = _scanSensorType.GetField("fov", pubInstance);
                _ssMinAltField = _scanSensorType.GetField("min_alt", pubInstance);
                _ssMaxAltField = _scanSensorType.GetField("max_alt", pubInstance);
                _ssBestAltField = _scanSensorType.GetField("best_alt", pubInstance);
                _ssInRangeField = _scanSensorType.GetField("inRange", pubInstance);
                _ssBestRangeField = _scanSensorType.GetField("bestRange", pubInstance);
            }
        }

        // Locate a body by case-insensitive bodyName. Keeps the API
        // forgiving to callers who paste display names (Kerbin vs kerbin).
        static CelestialBody BodyByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
            {
                var b = FlightGlobals.Bodies[i];
                if (b == null) continue;
                if (string.Equals(b.bodyName, name, StringComparison.OrdinalIgnoreCase))
                    return b;
            }
            return null;
        }

        // --- Endpoints ---

        [TelemetryAPI("scan.available",
            "Whether SCANsat is installed and reachable via reflection. " +
            "When false, scan.coverage / scan.maskBitmap / scan.anomalies " +
            "return null; the stock-backed scan.elevation / scan.biome " +
            "keys still resolve.",
            AlwaysEvaluable = true,
            Category = "scan",
            ReturnType = "bool")]
        object ScanAvailable(DataSources ds)
        {
            Search();
            return _utilType != null;
        }

        [TelemetryAPI("scan.coverage",
            "Percent (0–100) of the named body that's been scanned at the " +
            "given scan-type bit. Scan-type bit values: 1=AltLoRes, " +
            "2=AltHiRes, 8=Biome, 16=Anomaly, 32=AnomalyDetail, " +
            "128=ResourceLoRes, 256=ResourceHiRes. Returns null when " +
            "SCANsat isn't installed or the body name doesn't match.",
            Category = "scan",
            ReturnType = "double",
            Params = "string bodyName, int scanType")]
        object ScanCoverage(DataSources ds)
        {
            Search();
            if (_getCoverage == null) return null;
            if (ds.args == null || ds.args.Count < 2) return null;
            var body = BodyByName(ds.args[0]);
            if (body == null) return null;
            if (!int.TryParse(ds.args[1], out var scanType)) return null;
            try
            {
                return _getCoverage.Invoke(null, new object[] { scanType, body });
            }
            catch (Exception e)
            {
                PluginLogger.debug("scan.coverage threw: " + e.Message);
                return null;
            }
        }

        [TelemetryAPI("scan.maskBitmap",
            "Bit-packed coverage bitmap for the named body filtered to one " +
            "scan-type bit. Returns { width: 360, height: 180, type, bits: " +
            "<base64> } where bits is a 360×180 bit array packed MSB-first " +
            "into 8100 bytes; bit index (lat+90)*360 + (lon+180) is 1 when " +
            "the 1°×1° tile has been scanned of that type. Designed for a " +
            "one-shot HTTP fetch on body change; do not stream. Returns " +
            "null when SCANsat isn't installed.",
            AlwaysEvaluable = false,
            Plotable = false,
            Category = "scan",
            ReturnType = "object",
            Params = "string bodyName, int scanType")]
        object ScanMaskBitmap(DataSources ds)
        {
            Search();
            if (_getDataByBody == null || _coverageProp == null) return null;
            if (ds.args == null || ds.args.Count < 2) return null;
            var body = BodyByName(ds.args[0]);
            if (body == null) return null;
            if (!int.TryParse(ds.args[1], out var scanType)) return null;

            object data;
            try
            {
                data = _getDataByBody.Invoke(null, new object[] { body });
            }
            catch (Exception e)
            {
                PluginLogger.debug("scan.maskBitmap getData threw: " + e.Message);
                return null;
            }
            if (data == null) return null;

            var coverage = _coverageProp.GetValue(data, null) as Array;
            if (coverage == null) return null;
            // SCANdata.Coverage is Int16[360, 180] — indexed [lon+180, lat+90].
            // Pack into a 8100-byte bit array (360*180 = 64800 bits) in the
            // same row-major order Coverage uses so a client can decode
            // without flipping axes.
            int w = coverage.GetLength(0);
            int h = coverage.GetLength(1);
            int totalBits = w * h;
            var bits = new byte[(totalBits + 7) / 8];
            int idx = 0;
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    short val = (short)coverage.GetValue(x, y);
                    if ((val & scanType) != 0)
                    {
                        bits[idx >> 3] |= (byte)(0x80 >> (idx & 7));
                    }
                    idx++;
                }
            }

            return new Dictionary<string, object>
            {
                ["width"] = w,
                ["height"] = h,
                ["type"] = scanType,
                ["bits"] = Convert.ToBase64String(bits),
            };
        }

        [TelemetryAPI("scan.anomalies",
            "Known anomalies on the named body — array of " +
            "{ name, latitude, longitude, known, detail }. `known` is true " +
            "once the anomaly's position has been discovered (Anomaly scan), " +
            "`detail` is true once the player has the name (AnomalyDetail " +
            "scan). Returns null when SCANsat isn't installed.",
            Category = "scan",
            ReturnType = "object",
            Params = "string bodyName")]
        object ScanAnomalies(DataSources ds)
        {
            Search();
            if (_getDataByBody == null || _anomaliesProp == null) return null;
            if (ds.args == null || ds.args.Count < 1) return null;
            var body = BodyByName(ds.args[0]);
            if (body == null) return null;

            object data;
            try
            {
                data = _getDataByBody.Invoke(null, new object[] { body });
            }
            catch (Exception e)
            {
                PluginLogger.debug("scan.anomalies getData threw: " + e.Message);
                return null;
            }
            if (data == null) return null;

            var anomalies = _anomaliesProp.GetValue(data, null) as IEnumerable;
            if (anomalies == null) return null;

            var list = new List<Dictionary<string, object>>();
            foreach (var a in anomalies)
            {
                if (a == null) continue;
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = _anomalyName != null ? _anomalyName.GetValue(a) : null,
                    ["latitude"] = _anomalyLat != null ? _anomalyLat.GetValue(a) : null,
                    ["longitude"] = _anomalyLon != null ? _anomalyLon.GetValue(a) : null,
                    ["known"] = _anomalyKnown != null ? _anomalyKnown.GetValue(a) : null,
                    ["detail"] = _anomalyDetail != null ? _anomalyDetail.GetValue(a) : null,
                });
            }
            return list;
        }

        [TelemetryAPI("scan.heightGrid",
            "Bulk per-tile elevation grid for the named body. Returns " +
            "{ width: 360, height: 180, minMetres, maxMetres, " +
            "heights: <base64 Int16[] (metres)> } row-major in the same " +
            "(lon+180)*height + (lat+90) order as scan.maskBitmap. Uses " +
            "PQS lookups so SCANsat installation is not required, but " +
            "operators should still gate display behind scan.maskBitmap " +
            "coverage for fog-of-war semantics. Plotable=false — fetch " +
            "once on body change, do not stream. ~130 KB base64 per body.",
            AlwaysEvaluable = false,
            Plotable = false,
            Category = "scan",
            ReturnType = "object",
            Params = "string bodyName")]
        object ScanHeightGrid(DataSources ds)
        {
            if (ds.args == null || ds.args.Count < 1) return null;
            var body = BodyByName(ds.args[0]);
            if (body == null || body.pqsController == null) return null;

            const int W = 360;
            const int H = 180;
            var grid = new short[W * H];
            short minVal = short.MaxValue;
            short maxVal = short.MinValue;
            int idx = 0;
            for (int x = 0; x < W; x++)
            {
                double lon = x - 180 + 0.5;
                double rlon = lon * (Math.PI / 180.0);
                double cosLon = Math.Cos(rlon);
                double sinLon = Math.Sin(rlon);
                for (int y = 0; y < H; y++)
                {
                    double lat = y - 90 + 0.5;
                    double rlat = lat * (Math.PI / 180.0);
                    double cosLat = Math.Cos(rlat);
                    var rad = new Vector3d(cosLat * cosLon, Math.Sin(rlat), cosLat * sinLon);
                    double m = body.pqsController.GetSurfaceHeight(rad) - body.pqsController.radius;
                    short s = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(m)));
                    grid[idx++] = s;
                    if (s < minVal) minVal = s;
                    if (s > maxVal) maxVal = s;
                }
            }

            var bytes = new byte[grid.Length * 2];
            for (int i = 0; i < grid.Length; i++)
            {
                var s = grid[i];
                bytes[i * 2] = (byte)(s & 0xFF);
                bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }

            return new Dictionary<string, object>
            {
                ["width"] = W,
                ["height"] = H,
                ["minMetres"] = (int)minVal,
                ["maxMetres"] = (int)maxVal,
                ["heights"] = Convert.ToBase64String(bytes),
            };
        }

        [TelemetryAPI("scan.biomeGrid",
            "Bulk per-tile biome index grid for the named body. Returns " +
            "{ width: 360, height: 180, biomes: [{ name, displayName, " +
            "colour: 0xRRGGBB }], indices: <base64 byte[]> } where " +
            "indices[i] is the position of that tile's biome in `biomes` " +
            "(0xFF for null / bodies without a BiomeMap). Stock " +
            "CelestialBody.BiomeMap lookup — no SCANsat install required. " +
            "Plotable=false — fetch once on body change. ~70 KB base64.",
            AlwaysEvaluable = false,
            Plotable = false,
            Category = "scan",
            ReturnType = "object",
            Params = "string bodyName")]
        object ScanBiomeGrid(DataSources ds)
        {
            if (ds.args == null || ds.args.Count < 1) return null;
            var body = BodyByName(ds.args[0]);
            if (body == null) return null;
            const int W = 360;
            const int H = 180;
            var indices = new byte[W * H];
            var biomeOrder = new List<CBAttributeMapSO.MapAttribute>();
            var biomeIndex = new Dictionary<CBAttributeMapSO.MapAttribute, byte>();

            int idx = 0;
            for (int x = 0; x < W; x++)
            {
                double lon = x - 180 + 0.5;
                for (int y = 0; y < H; y++)
                {
                    double lat = y - 90 + 0.5;
                    CBAttributeMapSO.MapAttribute attr = null;
                    try { attr = body.BiomeMap?.GetAtt(lat * (Math.PI / 180.0), lon * (Math.PI / 180.0)); }
                    catch { attr = null; }
                    if (attr == null)
                    {
                        indices[idx++] = 0xFF;
                        continue;
                    }
                    if (!biomeIndex.TryGetValue(attr, out var slot))
                    {
                        slot = (byte)Math.Min(254, biomeOrder.Count);
                        biomeIndex[attr] = slot;
                        biomeOrder.Add(attr);
                    }
                    indices[idx++] = slot;
                }
            }

            var biomeList = new List<Dictionary<string, object>>();
            foreach (var attr in biomeOrder)
            {
                int rgb = 0;
                try
                {
                    var c = attr.mapColor;
                    rgb = ((int)(c.r * 255) << 16) | ((int)(c.g * 255) << 8) | (int)(c.b * 255);
                }
                catch { rgb = 0; }
                biomeList.Add(new Dictionary<string, object>
                {
                    ["name"] = attr.name ?? string.Empty,
                    ["displayName"] = attr.displayname ?? attr.name ?? string.Empty,
                    ["colour"] = rgb,
                });
            }

            return new Dictionary<string, object>
            {
                ["width"] = W,
                ["height"] = H,
                ["biomes"] = biomeList,
                ["indices"] = Convert.ToBase64String(indices),
            };
        }

        [TelemetryAPI("scan.scanningVessels",
            "Every vessel SCANsat is tracking (active or unloaded) plus its " +
            "current sub-vessel ground point and the list of scanner parts " +
            "on board with their FoV / altitude gates and live in-range " +
            "state. Cross-vessel by design — SCANsat keeps unloaded " +
            "satellites scanning in the background, so this surface " +
            "deliberately does NOT filter to the active vessel. Each entry " +
            "also carries SCANsat's actual per-tick ground-track values: " +
            "groundTrackWidthDeg (reflected from the private " +
            "SCANcontroller.getFOV — same number used to paint the in-game " +
            "overlay; this is the per-side latitude half-width in degrees), " +
            "groundTrackLonHalfDeg (the per-side longitude half-width = " +
            "groundTrackWidthDeg / cos(|subLat|), capped at 120° per " +
            "SCANsat's own coverage paint loop), and trackColor ({r,g,b,a} " +
            "from SCANvessel.trackColor). Returns null when SCANsat isn't " +
            "installed.",
            Category = "scan",
            ReturnType = "object")]
        object ScanScanningVessels(DataSources ds)
        {
            Search();
            if (_controllerSingleton == null || _knownVesselsProp == null
                || _scanVesselType == null || _scanSensorType == null)
            {
                return null;
            }
            object controller;
            try
            {
                controller = _controllerSingleton.GetValue(null, null);
            }
            catch (Exception e)
            {
                PluginLogger.debug("scan.scanningVessels singleton threw: " + e.Message);
                return null;
            }
            if (controller == null) return null;

            object known;
            try
            {
                known = _knownVesselsProp.GetValue(controller, null);
            }
            catch (Exception e)
            {
                PluginLogger.debug("scan.scanningVessels Known_Vessels threw: " + e.Message);
                return null;
            }

            var list = new List<Dictionary<string, object>>();
            if (known is IEnumerable kn)
            {
                foreach (var sv in kn)
                {
                    if (sv == null) continue;
                    var vessel = _svVesselField?.GetValue(sv) as Vessel;
                    var bodyObj = _svBodyField?.GetValue(sv) as CelestialBody;
                    double subLat = _svLatField != null ? (double)_svLatField.GetValue(sv) : 0;
                    double subLon = _svLonField != null ? (double)_svLonField.GetValue(sv) : 0;
                    var sensorList = _svSensorsField?.GetValue(sv) as IEnumerable;

                    var sensors = new List<Dictionary<string, object>>();
                    if (sensorList != null)
                    {
                        foreach (var ss in sensorList)
                        {
                            if (ss == null) continue;
                            sensors.Add(new Dictionary<string, object>
                            {
                                ["type"] = _ssTypeField != null ? (int)_ssTypeField.GetValue(ss) : 0,
                                ["fov"] = _ssFovField != null ? (double)_ssFovField.GetValue(ss) : 0,
                                ["minAlt"] = _ssMinAltField != null ? (double)_ssMinAltField.GetValue(ss) : 0,
                                ["maxAlt"] = _ssMaxAltField != null ? (double)_ssMaxAltField.GetValue(ss) : 0,
                                ["bestAlt"] = _ssBestAltField != null ? (double)_ssBestAltField.GetValue(ss) : 0,
                                ["inRange"] = _ssInRangeField != null && (bool)_ssInRangeField.GetValue(ss),
                                ["bestRange"] = _ssBestRangeField != null && (bool)_ssBestRangeField.GetValue(ss),
                            });
                        }
                    }

                    // SCANsat's actual ground-track width for this vessel
                    // at its current altitude — same value the in-game
                    // overlay paints. We reflect into the private
                    // SCANcontroller.getFOV so the wire field never drifts
                    // from what SCANsat is drawing.
                    object fovDegBox = null;
                    if (_getFovMethod != null && bodyObj != null)
                    {
                        try
                        {
                            fovDegBox = _getFovMethod.Invoke(
                                controller, new object[] { sv, bodyObj });
                        }
                        catch (Exception e)
                        {
                            PluginLogger.debug(
                                "scan.scanningVessels getFOV threw: " + e.Message);
                        }
                    }

                    // Longitude widening at the current latitude, matching
                    // SCANsat's coverage paint loop in SCANcontroller.cs:
                    //   fovW = fov * (1 / cos(|lat|));
                    //   if (fovW > 120) fovW = 120;
                    // We mirror that here so the wire shape carries
                    // SCANsat's exact lat/lon footprint extent — clients
                    // just draw `(±latHalfDeg, ±lonHalfDeg)`.
                    object lonHalfDegBox = null;
                    if (fovDegBox is double fovDeg && fovDeg > 0)
                    {
                        double absLat = Math.Abs(subLat);
                        if (absLat >= 90)
                        {
                            lonHalfDegBox = 120d;
                        }
                        else
                        {
                            double cosLat = Math.Cos(absLat * Math.PI / 180.0);
                            double fovW = cosLat > 1e-6
                                ? fovDeg / cosLat
                                : 120d;
                            if (fovW > 120) fovW = 120;
                            lonHalfDegBox = fovW;
                        }
                    }

                    // SCANsat's combined per-vessel track colour. Matches
                    // the tint of the in-game ground track overlay so the
                    // minimap stays visually aligned.
                    Dictionary<string, object> trackColor = null;
                    if (_svTrackColorField != null)
                    {
                        try
                        {
                            var c = _svTrackColorField.GetValue(sv);
                            if (c is Color32 c32)
                            {
                                trackColor = new Dictionary<string, object>
                                {
                                    ["r"] = c32.r,
                                    ["g"] = c32.g,
                                    ["b"] = c32.b,
                                    ["a"] = c32.a,
                                };
                            }
                        }
                        catch (Exception e)
                        {
                            PluginLogger.debug(
                                "scan.scanningVessels trackColor threw: " + e.Message);
                        }
                    }

                    list.Add(new Dictionary<string, object>
                    {
                        ["vesselId"] = vessel != null ? vessel.id.ToString() : string.Empty,
                        ["vesselName"] = vessel != null ? vessel.GetName() : string.Empty,
                        ["body"] = bodyObj != null ? bodyObj.bodyName : string.Empty,
                        ["subLatitude"] = subLat,
                        ["subLongitude"] = subLon,
                        ["altitude"] = vessel != null ? vessel.altitude : 0,
                        ["sensors"] = sensors,
                        ["groundTrackWidthDeg"] = fovDegBox,
                        ["groundTrackLonHalfDeg"] = lonHalfDegBox,
                        ["trackColor"] = trackColor,
                    });
                }
            }
            return list;
        }

        [TelemetryAPI("scan.elevation",
            "Surface elevation (metres above the body's reference radius) " +
            "at the given lat/lon. Stock PQS lookup — no SCANsat install " +
            "required. Mirrors SCANutil.getElevation internally; we just " +
            "call PQS directly to avoid a soft-dep on SCANsat for the " +
            "elevation number.",
            Units = APIEntry.UnitType.DISTANCE,
            Category = "scan",
            ReturnType = "double",
            Params = "string bodyName, double latitudeDeg, double longitudeDeg")]
        object ScanElevation(DataSources ds)
        {
            if (ds.args == null || ds.args.Count < 3) return null;
            var body = BodyByName(ds.args[0]);
            if (body == null || body.pqsController == null) return null;
            if (!double.TryParse(ds.args[1], out var lat)) return null;
            if (!double.TryParse(ds.args[2], out var lon)) return null;
            double rlat = lat * (Math.PI / 180.0);
            double rlon = lon * (Math.PI / 180.0);
            var rad = new Vector3d(
                Math.Cos(rlat) * Math.Cos(rlon),
                Math.Sin(rlat),
                Math.Cos(rlat) * Math.Sin(rlon));
            return body.pqsController.GetSurfaceHeight(rad)
                - body.pqsController.radius;
        }

        [TelemetryAPI("scan.biome",
            "Biome name at the given lat/lon on the named body. Stock " +
            "ScienceUtil.GetExperimentBiome — works without SCANsat. " +
            "Bodies without biome maps (e.g. the Sun) return an empty " +
            "string.",
            Units = APIEntry.UnitType.STRING,
            Category = "scan",
            ReturnType = "string",
            Params = "string bodyName, double latitudeDeg, double longitudeDeg")]
        object ScanBiome(DataSources ds)
        {
            if (ds.args == null || ds.args.Count < 3) return null;
            var body = BodyByName(ds.args[0]);
            if (body == null) return null;
            if (!double.TryParse(ds.args[1], out var lat)) return null;
            if (!double.TryParse(ds.args[2], out var lon)) return null;
            try
            {
                return ScienceUtil.GetExperimentBiome(body, lat, lon);
            }
            catch
            {
                return "";
            }
        }
    }
}
