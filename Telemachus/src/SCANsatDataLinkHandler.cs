using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

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
            }

            if (_anomalyType != null)
            {
                _anomalyName = _anomalyType.GetField("name", pubInstance);
                _anomalyLat = _anomalyType.GetField("latitude", pubInstance);
                _anomalyLon = _anomalyType.GetField("longitude", pubInstance);
                _anomalyKnown = _anomalyType.GetField("known", pubInstance);
                _anomalyDetail = _anomalyType.GetField("detail", pubInstance);
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
