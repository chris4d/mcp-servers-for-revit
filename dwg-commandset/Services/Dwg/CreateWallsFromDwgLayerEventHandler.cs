using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Dwg;
using RevitMCPCommandSet.Utils.Dwg;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Dwg
{
    /// <summary>
    /// Mutating handler that generates Revit walls from paired face lines on
    /// one DWG layer. DWGs draft walls as pairs of parallel lines (the two
    /// faces). Detection: near-parallel pairs within a thickness range with
    /// sufficient directional overlap; greedy assignment consumes each face
    /// line once. Short pairs are rejected as door jamb linework, and pairs
    /// spanning detected door-swing arcs are optionally rejected. Pair
    /// thickness maps to the nearest Revit wall type by compound width.
    /// </summary>
    public class CreateWallsFromDwgLayerEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public string DwgNameOrId { get; set; }
        public string Layer { get; set; }
        public double HeightFt { get; set; } = 10.0;
        public double MaxWallThicknessFt { get; set; } = 3.0;
        public double MinWallLengthFt { get; set; } = 3.5;
        public string WallTypeName { get; set; }
        public bool ExcludeDoorArcs { get; set; } = true;
        public long LevelId { get; set; } = -1;
        public int MaxWalls { get; set; } = 200;

        private const double MinWallThicknessFt = 2.0 / 12.0; // 2"
        private const double MaxParallelDevDeg = 2.0;
        private const double MinOverlapFrac = 0.7;
        private const double JambSnapFt = 0.35;

        public object Result { get; private set; }

        public bool WaitForCompletion(int timeoutMilliseconds = 180000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        private class WallPair
        {
            public XYZ CenterStart;
            public XYZ CenterEnd;
            public double Thickness;

            public double Length => CenterStart.DistanceTo(CenterEnd);
            public XYZ Mid => (CenterStart + CenterEnd) * 0.5;
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    Result = new Dictionary<string, object> { ["error"] = "No active document" };
                    return;
                }

                if (string.IsNullOrWhiteSpace(DwgNameOrId) || string.IsNullOrWhiteSpace(Layer))
                {
                    Result = new Dictionary<string, object> { ["error"] = "dwgNameOrId and layer are required" };
                    return;
                }

                Element target = DwgCurveSource.ResolveDwg(doc, DwgNameOrId, out string targetName, out string targetKind);
                if (target == null)
                {
                    Result = new Dictionary<string, object> { ["error"] = $"No imported or linked DWG matching '{DwgNameOrId}'" };
                    return;
                }

                var layerCurves = DwgCurveSource.CollectLayerCurves(doc, target, Layer.Trim());

                // Straight lines only; arcs are stubbed (counted, not built).
                var lines = new List<Line>();
                int arcsStubbed = 0, otherSkipped = 0;
                foreach (var cv in layerCurves)
                {
                    if (cv is Line ln) lines.Add(ln);
                    else if (cv is Arc) arcsStubbed++;
                    else otherSkipped++;
                }

                if (lines.Count == 0)
                {
                    Result = new Dictionary<string, object>
                    {
                        ["error"] = $"No straight lines found on layer '{Layer}' of '{targetName}'",
                        ["arcsStubbed"] = arcsStubbed,
                        ["otherSkipped"] = otherSkipped
                    };
                    return;
                }

                // Door-swing arc detection across ALL layers of the DWG.
                List<XYZ> jambPoints = null;
                if (ExcludeDoorArcs)
                {
                    jambPoints = CollectDoorJambPoints(target, doc);
                }

                // ---- Pair detection ----
                var candidates = new List<(int i, int j, double thick, double overlapFrac)>();
                for (int i = 0; i < lines.Count; i++)
                {
                    for (int j = i + 1; j < lines.Count; j++)
                    {
                        if (TryPair(lines[i], lines[j], out var pair, out double overlapFrac))
                            candidates.Add((i, j, pair.Thickness, overlapFrac));
                    }
                }

                // Greedy: most overlap first, then thinnest.
                var consumedSet = new HashSet<int>();
                var accepted = new List<WallPair>();
                foreach (var cand in candidates
                    .OrderByDescending(c => c.overlapFrac)
                    .ThenBy(c => c.thick))
                {
                    if (consumedSet.Contains(cand.i) || consumedSet.Contains(cand.j)) continue;
                    consumedSet.Add(cand.i);
                    consumedSet.Add(cand.j);
                    TryPair(lines[cand.i], lines[cand.j], out var pair, out _);
                    if (pair != null) accepted.Add(pair);
                }

                int unpairedCount = lines.Count - accepted.Count * 2;

                // ---- Level ----
                double z = DwgCurveSource.MedianZ(layerCurves);
                Level level = null;
                if (LevelId > 0) level = doc.GetElement(new ElementId(LevelId)) as Level;
                if (level == null)
                    level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                        .OrderBy(l => Math.Abs(l.Elevation - z)).FirstOrDefault();
                if (level == null)
                {
                    Result = new Dictionary<string, object> { ["error"] = "No level found in document" };
                    return;
                }

                // ---- Wall types ----
                var wallTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType)).WhereElementIsElementType()
                    .Cast<WallType>().ToList();
                if (wallTypes.Count == 0)
                {
                    Result = new Dictionary<string, object> { ["error"] = "No wall types in document" };
                    return;
                }
                WallType forcedType = null;
                if (!string.IsNullOrWhiteSpace(WallTypeName))
                {
                    var q = WallTypeName.Trim();
                    forcedType = wallTypes.FirstOrDefault(wt => string.Equals(wt.Name, q, StringComparison.OrdinalIgnoreCase))
                              ?? wallTypes.FirstOrDefault(wt => wt.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (forcedType == null)
                    {
                        Result = new Dictionary<string, object> { ["error"] = $"No wall type matching '{WallTypeName}'" };
                        return;
                    }
                }

                // ---- Build ----
                var preprocessor = new SilentWarningsPreprocessor();
                var createdIds = new List<long>();
                var typeSummary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int created = 0, rejectedJamb = 0, doorArcRejected = 0, buildFailed = 0;

                using (var trans = new Transaction(doc, "Create Walls from DWG Layer"))
                {
                    var fo = trans.GetFailureHandlingOptions();
                    trans.SetFailureHandlingOptions(fo.SetFailuresPreprocessor(preprocessor));
                    trans.Start();

                    foreach (var pair in accepted)
                    {
                        if (created >= MaxWalls) break;

                        // Jamb rejection: fake walls across door openings are short.
                        if (pair.Length < MinWallLengthFt)
                        {
                            rejectedJamb++;
                            continue;
                        }

                        // Door-arc rejection: short segment spanning between two jamb points.
                        if (ExcludeDoorArcs && jambPoints != null &&
                            pair.Length < MinWallLengthFt * 2.0 &&
                            NearJamb(pair.CenterStart, jambPoints) && NearJamb(pair.CenterEnd, jambPoints))
                        {
                            doorArcRejected++;
                            continue;
                        }

                        // Nearest wall type by thickness (forced type overrides).
                        var wt = forcedType;
                        if (wt == null)
                            wt = wallTypes.OrderBy(w => Math.Abs(w.Width - pair.Thickness)).First();

                        try
                        {
                            var centerLine = Line.CreateBound(pair.CenterStart, pair.CenterEnd);
                            var wall = Wall.Create(doc, centerLine, wt.Id, level.Id, HeightFt, 0, false, false);
                            if (wall != null)
                            {
                                createdIds.Add(DwgCurveSource.IdValue(wall));
                                created++;
                                string tn = wt.Name;
                                if (typeSummary.ContainsKey(tn)) typeSummary[tn]++;
                                else typeSummary[tn] = 1;
                            }
                            else buildFailed++;
                        }
                        catch
                        {
                            buildFailed++;
                        }
                    }

                    trans.Commit();
                }

                Result = new Dictionary<string, object>
                {
                    ["source"] = new Dictionary<string, object>
                    {
                        ["id"] = DwgCurveSource.IdValue(target),
                        ["name"] = targetName,
                        ["kind"] = targetKind
                    },
                    ["layer"] = Layer.Trim(),
                    ["level"] = new Dictionary<string, object> { ["id"] = DwgCurveSource.IdValue(level), ["name"] = level.Name },
                    ["heightFt"] = HeightFt,
                    ["totalLines"] = lines.Count,
                    ["wallsCreated"] = created,
                    ["unpairedLines"] = unpairedCount,
                    ["rejectedJamb"] = rejectedJamb,
                    ["doorArcRejected"] = doorArcRejected,
                    ["buildFailed"] = buildFailed,
                    ["arcsStubbed"] = arcsStubbed,
                    ["otherSkipped"] = otherSkipped,
                    ["maxWallThicknessFt"] = MaxWallThicknessFt,
                    ["minWallLengthFt"] = MinWallLengthFt,
                    ["typeSummary"] = typeSummary,
                    ["createdIds"] = createdIds,
                    ["suppressedMessages"] = preprocessor.Log
                };
            }
            catch (Exception ex)
            {
                Result = new Dictionary<string, object> { ["error"] = ex.Message, ["stack"] = ex.StackTrace };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        /// <summary>
        /// Try to pair two lines as wall faces: near-parallel, perpendicular
        /// distance within thickness range, sufficient directional overlap.
        /// Centerline spans the overlap region as the mean of both lines.
        /// </summary>
        private bool TryPair(Line a, Line b, out WallPair pair, out double overlapFrac)
        {
            pair = null;
            overlapFrac = 0;

            var sa = a.GetEndPoint(0);
            var ea = a.GetEndPoint(1);
            XYZ da = ea - sa;
            double la = da.GetLength();
            if (la < 1e-9) return false;
            XYZ ua = da / la;

            var sb = b.GetEndPoint(0);
            var eb = b.GetEndPoint(1);
            XYZ db = eb - sb;
            double lb = db.GetLength();
            if (lb < 1e-9) return false;
            XYZ ub = db / lb;

            // Parallelism: undirected angle deviation.
            double dot = Math.Abs(ua.DotProduct(ub));
            double angDeg = Math.Acos(Math.Min(1.0, dot)) * 180.0 / Math.PI;
            if (angDeg > MaxParallelDevDeg) return false;

            // Perpendicular thickness from b's midpoint to a's infinite line.
            var mb = (sb + eb) * 0.5;
            XYZ v = mb - sa;
            XYZ perp = v - ua * v.DotProduct(ua);
            double thick = perp.GetLength();
            if (thick < MinWallThicknessFt || thick > MaxWallThicknessFt) return false;

            // Overlap along a's parameter.
            double tb0 = (sb - sa).DotProduct(ua) / la;   // param of b.start on a
            double tb1 = (eb - sa).DotProduct(ua) / la;   // param of b.end on a
            double lo = Math.Max(0.0, Math.Min(tb0, tb1));
            double hi = Math.Min(1.0, Math.Max(tb0, tb1));
            if (hi <= lo) return false;

            double overlapLen = (hi - lo) * la;
            overlapFrac = overlapLen / Math.Min(la, lb);
            if (overlapFrac < MinOverlapFrac) return false;

            // Centerline: mean of point on a and its projection on b, over overlap.
            var pStart = sa + da * lo;
            XYZ c0 = (pStart + ProjectOnLine(pStart, sb, ub)) * 0.5;
            var pEnd = sa + da * hi;
            XYZ c1 = (pEnd + ProjectOnLine(pEnd, sb, ub)) * 0.5;

            pair = new WallPair { CenterStart = c0, CenterEnd = c1, Thickness = thick };
            return true;
        }

        private static XYZ ProjectOnLine(XYZ p, XYZ s, XYZ dirUnit)
        {
            return s + dirUnit * (p - s).DotProduct(dirUnit);
        }

        private static List<XYZ> CollectDoorJambPoints(Element target, Document doc)
        {
            var pts = new List<XYZ>();
            var all = DwgCurveSource.CollectLayerCurves(doc, target, null);
            foreach (var cv in all)
            {
                if (!(cv is Arc a)) continue;
                double spanRad;
                try { spanRad = a.GetEndParameter(1) - a.GetEndParameter(0); } catch { continue; }
                double spanDeg = spanRad * 180.0 / Math.PI;
                if (spanDeg < 75 || spanDeg > 105) continue;
                if (a.Radius < 0.75 || a.Radius > 5.0) continue;
                try
                {
                    pts.Add(a.GetEndPoint(0));
                    pts.Add(a.GetEndPoint(1));
                }
                catch { }
            }
            return pts;
        }

        private static bool NearJamb(XYZ p, List<XYZ> jambPoints)
        {
            foreach (var jp in jambPoints)
            {
                if (jp.DistanceTo(p) <= JambSnapFt) return true;
            }
            return false;
        }

        public string GetName()
        {
            return "Create Walls from DWG Layer";
        }
    }
}
