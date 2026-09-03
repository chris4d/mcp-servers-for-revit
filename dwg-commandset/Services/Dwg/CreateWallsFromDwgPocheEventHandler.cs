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
    /// Mutating handler that generates Revit walls from the HATCH fills of a
    /// DWG import (poche regions). Lines and polylines are NOT used here —
    /// that is create_walls_from_dwg_layer's job.DWG hatch entities are
    /// exposed by Revit's import as flat (volume-zero) solids with no solid
    /// level GraphicsStyle; the source DWG layer survives one level deeper,
    /// on Face.GraphicsStyleId of each face. This handler therefore:
    /// scans the import for flat solids, keeps only faces whose
    /// Face.GraphicsStyleId resolves to the requested DWG layer (or all faces
    /// when no layer is given), extracts each face's boundary curve loop
    /// (tessellated), merges consecutive collinear edges into straight runs,
    /// and pairs runs of the same face as the two faces of a wall.
    /// Centerline pieces from different loops that are collinear (same
    /// circular-mean angle within tolerance, same perpendicular offset, small
    /// gap) merge independently, and duplicate outlines collapse. Thickness
    /// imposes the 2in..max wall band. Piece thickness maps to the nearest
    /// Revit wall type by compound width. Short centerlines are rejected as
    /// jamb linework; door-swing arcs optionally reject short centerlines
    /// near detected jambs.
    /// </summary>
    public class CreateWallsFromDwgPocheEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public string DwgNameOrId { get; set; }
        public string PocheLayer { get; set; } = "";
        public double HeightFt { get; set; } = 10.0;
        public double MaxWallThicknessFt { get; set; } = 3.0;
        public double MinWallLengthFt { get; set; } = 3.5;
        public string WallTypeName { get; set; }
        public bool ExcludeDoorArcs { get; set; } = true;
        public long LevelId { get; set; } = -1;
        public int MaxWalls { get; set; } = 200;

        // Tunables (internal; revisit per-DWG if needed).
        private const double MinWallThicknessFt = 2.0 / 12.0; // 2"
        private const double RunAngleTolDeg = 2.0;             // consecutive-edge collinearity in a loop
        private const double RailTolFt = 0.05;                 // collinear offset tolerance
        private const double PairAngleTolDeg = 2.0;            // face pairing parallelism
        private const double MinOverlapFrac = 0.7;
        private const double ClusterAngleTolRad = 2.0 * Math.PI / 180.0; // piece clustering
        private const double MergeGapFt = 0.5;                 // silent merge gap (drafting slop)
        private const double LoopCloseTolFt = 0.01;
        private const double MinRunFt = 0.3;                   // debris threshold for a face run
        private const double JambSnapFt = 0.35;

        // Opening bridging: walls are placed continuous across door openings
        // (doors get inserted later and cut real openings). A collinear gap on
        // the same rail up to MaxOpeningGapFt (doors incl. jamb trim; large
        // double doors) is bridged when both flanking pieces agree on measured
        // thickness AND a short perpendicular jamb run (the opening face)
        // brackets the gap. Gaps without jamb evidence stay separate.
        private const double MaxOpeningGapFt = 8.0;   // max bridged door opening
        private const double BridgeThicknessTolFt = 0.05; // same-wall thickness agreement
        private const double JambRatio = 0.35;        // short run vs neighbor lengths
        private const double JambPerpMinDeg = 60.0;   // jamb-vs-neighbor perpendicularity floor
        private const double JambSiblingParallelTolDeg = 10.0; // sibling runs face each other
        private const double JambSiblingLenTol = 0.25;  // near-equal lengths (both span the band)
        private const double BridgeJambSnapFt = 0.6;  // jamb midpoint may sit near a gap edge

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

                if (string.IsNullOrWhiteSpace(DwgNameOrId))
                {
                    Result = new Dictionary<string, object> { ["error"] = "dwgNameOrId is required" };
                    return;
                }

                var target = DwgCurveSource.ResolveDwg(doc, DwgNameOrId.Trim(), out string targetName, out string targetKind);
                if (target == null)
                {
                    Result = new Dictionary<string, object> { ["error"] = $"No imported or linked DWG matching '{DwgNameOrId}'" };
                    return;
                }

                string layerFilter = string.IsNullOrWhiteSpace(PocheLayer) ? null : PocheLayer.Trim();

                int flatSolids = 0, facesInspected = 0, facesKept = 0, degenerateFaces = 0;
                var loops = new List<List<XYZ>>();
                var loopSeen = new HashSet<string>();
                var layerHistogram = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var zValues = new List<double>();

                // Tessellation of one boundary curve loop into a deduped point
                // ring (no repeated closing vertex).
                void Tessellate(CurveLoop cl2, List<XYZ> pts)
                {
                    foreach (var cv in cl2)
                    {
                        Line ln = cv as Line;
                        if (ln != null)
                        {
                            pts.Add(ln.GetEndPoint(0));
                            continue;
                        }
                        foreach (var p in cv.Tessellate())
                            pts.Add(p);
                    }
                }

                void Walk(GeometryElement ge)
                {
                    foreach (var o in ge)
                    {
                        var gi = o as GeometryInstance;
                        if (gi != null)
                        {
                            var inst = gi.GetInstanceGeometry();
                            if (inst != null) Walk(inst);
                            continue;
                        }
                        var solid = o as Solid;
                        if (solid == null || solid.Volume >= 1e-6) continue;
                        flatSolids++;

                        foreach (Face f in solid.Faces)
                        {
                            var gs3 = doc.GetElement(f.GraphicsStyleId) as GraphicsStyle;
                            string layerName = gs3 != null && gs3.GraphicsStyleCategory != null
                                ? gs3.GraphicsStyleCategory.Name : "";
                            facesInspected++;
                            if (layerHistogram.ContainsKey(layerName)) layerHistogram[layerName]++; else layerHistogram[layerName] = 1;
                            if (layerFilter != null && !string.Equals(layerName, layerFilter, StringComparison.OrdinalIgnoreCase))
                                continue;

                            // Only planar faces carry a clean outline.
                            var pf = f as PlanarFace;
                            if (pf == null) { degenerateFaces++; continue; }

                            var loopsOfFace = pf.GetEdgesAsCurveLoops();
                            foreach (var loopDirs in loopsOfFace)
                            {
                                var tess = new List<XYZ>();
                                Tessellate(loopDirs, tess);
                                if (tess.Count < 3) { degenerateFaces++; continue; }

                                // Dedupe consecutive, drop the repeated closing vertex.
                                var vs = new List<XYZ>();
                                foreach (var p in tess)
                                {
                                    if (vs.Count > 0 && vs[vs.Count - 1].DistanceTo(p) < 0.01) continue;
                                    vs.Add(p);
                                }
                                if (vs.Count >= 2 && vs[0].DistanceTo(vs[vs.Count - 1]) < 0.01) vs.RemoveAt(vs.Count - 1);
                                if (vs.Count < 3) { degenerateFaces++; continue; }

                                // Duplicate-outline collapse: top and bottom
                                // faces of one flat solid share the same outline
                                // signature.
                                string sig = string.Join(";", vs.Select(p =>
                                    Math.Round(p.X * 64.0) + "," + Math.Round(p.Y * 64.0)));
                                if (loopSeen.Contains(sig)) continue;
                                loopSeen.Add(sig);

                                facesKept++;
                                foreach (var p in vs) zValues.Add(p.Z);
                                loops.Add(vs);
                            }
                        }
                    }
                }
                var geo = target.get_Geometry(new Options());
                if (geo != null) Walk(geo);

                if (loops.Count == 0)
                {
                    Result = new Dictionary<string, object>
                    {
                        ["error"] = layerFilter == null
                            ? "No flat hatch-fill solids found in the DWG"
                            : $"No flat hatch-fill faces found on layer '{PocheLayer}' of '{targetName}'",
                        ["flatSolids"] = flatSolids,
                        ["facesInspected"] = facesInspected,
                        ["faceLayerHistogram"] = layerHistogram
                    };
                    return;
                }

                // ---- per-loop: straight runs + within-loop face pairing ----
                double minT = MinWallThicknessFt;
                double maxT = MaxWallThicknessFt;
                var centerlinePieces = new List<double[]>(); // sX,sY,eX,eY,thick
                var jambMidpoints = new List<XYZ>();         // opening-face run midpoints (bridging evidence)
                int straightRuns = 0, pairsNoThickness = 0, pairsNoOverlap = 0, pairsMade = 0;
                int jambCandidates = 0;
                var rejects = new List<Dictionary<string, object>>();
                const int RejectLogCap = 600;

                foreach (var vs in loops)
                {
                    int n = vs.Count;
                    if (n < 3) continue;

                    // Merge consecutive collinear edges into straight runs.
                    // NOTE: rings come deduped WITHOUT the repeated closing
                    // vertex — the ring edge wraps via (k+1)%n.
                    var runs = new List<(XYZ s, XYZ e, double len)>();
                    int i = 0;
                    while (i < n)
                    {
                        XYZ d0 = vs[(i + 1) % n] - vs[i];
                        double L0 = d0.GetLength();
                        if (L0 < 1e-9) { i++; continue; }
                        XYZ dirCur = d0 / L0;
                        double lenCur = L0;
                        int k = i;
                        while (k + 1 < n)
                        {
                            XYZ n0 = vs[k + 1], n1 = vs[(k + 2) % n];
                            XYZ dN = n1 - n0;
                            double LN = dN.GetLength();
                            if (LN < 1e-9) break;
                            XYZ uN = dN / LN;
                            double dotAbs = Math.Abs(dirCur.DotProduct(uN));
                            double angDev = Math.Acos(Math.Min(1.0, dotAbs)) * 180.0 / Math.PI;
                            bool sameDir = angDev <= RunAngleTolDeg;
                            if (sameDir)
                            {
                                var vv = n0 - vs[i];
                                var perp = vv - dirCur * vv.DotProduct(dirCur);
                                if (perp.GetLength() > RailTolFt) sameDir = false;
                            }
                            if (!sameDir) break;
                            if (dirCur.DotProduct(uN) < 0) dirCur = dirCur.Negate();
                            lenCur += LN;
                            k++;
                        }
                        if (lenCur >= MinRunFt)
                        {
                            runs.Add((vs[i], vs[(k + 1) % n], lenCur));
                            straightRuns++;
                        }
                        i = k + 1;
                    }

                    // Jamb classification: a short run bracketed by two long
                    // neighbors is the face of a door opening (jamb linework),
                    // when both neighbor get perpendicular rather than
                    // collinear to it. Its midpoint becomes bridging evidence.
                    int runCount = runs.Count;
                    if (runCount >= 3)
                    {
                        for (int j = 0; j < runCount; j++)
                        {
                            var prevR = runs[(j + runCount - 1) % runCount];
                            var nextR = runs[(j + 1) % runCount];
                            if (runs[j].len >= JambRatio * Math.Min(prevR.len, nextR.len)) continue;
                            XYZ dj = runs[j].e - runs[j].s;
                            XYZ dp = prevR.e - prevR.s;
                            XYZ dn = nextR.e - nextR.s;
                            double lj = dj.GetLength(), lp = dp.GetLength(), ln2 = dn.GetLength();
                            if (lj < 1e-9 || lp < 1e-9 || ln2 < 1e-9) continue;
                            double angP = Math.Acos(Math.Min(1.0, Math.Abs(dj.DotProduct(dp) / (lj * lp)))) * 180.0 / Math.PI;
                            double angN = Math.Acos(Math.Min(1.0, Math.Abs(dj.DotProduct(dn) / (lj * ln2)))) * 180.0 / Math.PI;
                            if (angP < JambPerpMinDeg || angN < JambPerpMinDeg) continue;

                            // A jamb is one of a PAIR: door openings produce two
                            // perpendicular short runs facing each other across
                            // the opening - parallel to each other, near-equal
                            // length (both span the wall band), a sibling no
                            // farther than MaxOpeningGapFt away. A short run
                            // with no such sibling is a wall line (stub, niche
                            // edge), NOT a jamb.
                            XYZ mj = (runs[j].s + runs[j].e) * 0.5;
                            bool hasSibling = false;
                            for (int s2 = 0; s2 < runCount && !hasSibling; s2++)
                            {
                                if (s2 == j) continue;
                                XYZ ds2 = runs[s2].e - runs[s2].s;
                                double ls2 = ds2.GetLength();
                                if (ls2 < 1e-9) continue;
                                double parallelDev = Math.Acos(Math.Min(1.0, Math.Abs(dj.DotProduct(ds2) / (lj * ls2)))) * 180.0 / Math.PI;
                                if (parallelDev > JambSiblingParallelTolDeg) continue;
                                double lenDev = Math.Abs(ls2 - lj);
                                if (lenDev > JambSiblingLenTol * Math.Max(lj, ls2)) continue;
                                XYZ ms = (runs[s2].s + runs[s2].e) * 0.5;
                                if (mj.DistanceTo(ms) > MaxOpeningGapFt) continue;
                                hasSibling = true;
                            }
                            if (!hasSibling) continue;
                            jambCandidates++;
                            jambMidpoints.Add(mj);
                        }
                    }

                    // Pair runs within the loop as wall faces.
                    var runMatched = new bool[runCount];
                    for (int a = 0; a < runCount; a++)
                    {
                        var sa = runs[a].s;
                        var ea = runs[a].e;
                        var da = ea - sa;
                        double la = da.GetLength();
                        if (la < 1e-9) continue;
                        var ua = da / la;

                        for (int b = a + 1; b < runCount; b++)
                        {
                            var sb = runs[b].s;
                            var eb = runs[b].e;
                            var db = eb - sb;
                            double lb = db.GetLength();
                            if (lb < 1e-9) continue;
                            var ub = db / lb;

                            double dotAbs2 = Math.Abs(ua.DotProduct(ub));
                            double dev2 = Math.Acos(Math.Min(1.0, dotAbs2)) * 180.0 / Math.PI;
                            if (dev2 > PairAngleTolDeg) continue;

                            var mb = (sb + eb) * 0.5;
                            var vv = mb - sa;
                            var perp = vv - ua * vv.DotProduct(ua);
                            double thick = perp.GetLength();
                            if (thick < minT || thick > maxT)
                            {
                                pairsNoThickness++;
                                if (rejects.Count < RejectLogCap)
                                    rejects.Add(new Dictionary<string, object>
                                    {
                                        ["stage"] = "pairThickness",
                                        ["lenA"] = R2(la), ["lenB"] = R2(lb), ["thick"] = R2(thick),
                                        ["aStart"] = P2(sa), ["aEnd"] = P2(ea),
                                        ["bStart"] = P2(sb), ["bEnd"] = P2(eb)
                                    });
                                continue;
                            }

                            double tb0 = (sb - sa).DotProduct(ua) / la;
                            double tb1 = (eb - sa).DotProduct(ua) / la;
                            double lo = Math.Max(0.0, Math.Min(tb0, tb1));
                            double hi = Math.Min(1.0, Math.Max(tb0, tb1));
                            if (hi <= lo) continue;
                            double overlapLen = (hi - lo) * la;
                            double minLen = Math.Min(la, lb);
                            if (overlapLen / minLen < MinOverlapFrac)
                            {
                                pairsNoOverlap++;
                                if (rejects.Count < RejectLogCap)
                                    rejects.Add(new Dictionary<string, object>
                                    {
                                        ["stage"] = "pairOverlap",
                                        ["lenA"] = R2(la), ["lenB"] = R2(lb), ["thick"] = R2(thick),
                                        ["overlapFrac"] = R2(overlapLen / minLen),
                                        ["aStart"] = P2(sa), ["aEnd"] = P2(ea),
                                        ["bStart"] = P2(sb), ["bEnd"] = P2(eb)
                                    });
                                continue;
                            }

                            // Centerline: mean of point on run a and its projection on run b.
                            var pStart = sa + da * lo;
                            var projS = sb + ub * (pStart - sb).DotProduct(ub);
                            var c0 = (pStart + projS) * 0.5;
                            var pEnd = sa + da * hi;
                            var projE = sb + ub * (pEnd - sb).DotProduct(ub);
                            var c1 = (pEnd + projE) * 0.5;

                            centerlinePieces.Add(new[] { c0.X, c0.Y, c1.X, c1.Y, thick, dev2 });
                            pairsMade++;
                            runMatched[a] = true;
                            runMatched[b] = true;
                        }
                    }

                    // Runs that found no partner at all are pure linework /
                    // opening faces with no second face — record them so the
                    // cull trail shows which hatch geometry died here.
                    for (int a = 0; a < runCount; a++)
                    {
                        if (runMatched[a]) continue;
                        var ra = runs[a];
                        var ua2 = (ra.e - ra.s) / ra.len;
                        var mid = (ra.s + ra.e) * 0.5;
                        var nrm = new XYZ(-ua2.Y, ua2.X, 0); // inward probe
                        if (rejects.Count < RejectLogCap)
                            rejects.Add(new Dictionary<string, object>
                            {
                                ["stage"] = "unpairedRun",
                                ["len"] = R2(ra.len),
                                ["mid"] = P2(mid),
                                ["probe"] = P2(mid + nrm * Math.Min(maxT, 1.0))
                            });
                    }
                }

                // ---- independent collinear merge (with opening bridging) ----
                var bridgeStats = new BridgeStats();
                var merged = MergeCollinear(centerlinePieces, jambMidpoints, bridgeStats, rejects);

                int wallsAfterMerge = merged.Count(p => p.Length >= MinWallLengthFt);

                // ---- level / wall types / build ----
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

                double z = 0;
                if (zValues.Count > 0)
                {
                    zValues.Sort();
                    z = zValues[zValues.Count / 2];
                }
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

                List<XYZ> jambPoints = null;
                if (ExcludeDoorArcs)
                {
                    jambPoints = CollectDoorJambPoints(target, doc);
                }

                var preprocessor = new SilentWarningsPreprocessor();
                var createdIds = new List<long>();
                var typeSummary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int created = 0, rejectedJamb = 0, doorArcRejected = 0, buildFailed = 0;

                using (var trans = new Transaction(doc, "Create Walls from DWG Poche"))
                {
                    var fo = trans.GetFailureHandlingOptions();
                    trans.SetFailureHandlingOptions(fo.SetFailuresPreprocessor(preprocessor));
                    trans.Start();

                    foreach (var pair in merged)
                    {
                        if (created >= MaxWalls) break;

                        if (pair.Length < MinWallLengthFt)
                        {
                            rejectedJamb++;
                            if (rejects.Count < RejectLogCap)
                                rejects.Add(new Dictionary<string, object>
                                {
                                    ["stage"] = "shortCenterline",
                                    ["len"] = R2(pair.Length), ["thick"] = R2(pair.Thickness),
                                    ["start"] = P2(pair.CenterStart), ["end"] = P2(pair.CenterEnd)
                                });
                            continue;
                        }

                        if (ExcludeDoorArcs && jambPoints != null &&
                            pair.Length < MinWallLengthFt * 2.0 &&
                            NearJamb(pair.CenterStart, jambPoints) && NearJamb(pair.CenterEnd, jambPoints))
                        {
                            doorArcRejected++;
                            if (rejects.Count < RejectLogCap)
                                rejects.Add(new Dictionary<string, object>
                                {
                                    ["stage"] = "doorArc",
                                    ["len"] = R2(pair.Length), ["thick"] = R2(pair.Thickness),
                                    ["start"] = P2(pair.CenterStart), ["end"] = P2(pair.CenterEnd)
                                });
                            continue;
                        }

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
                        catch { buildFailed++; }
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
                    ["pocheLayer"] = layerFilter ?? "(all layers)",
                    ["level"] = new Dictionary<string, object> { ["id"] = DwgCurveSource.IdValue(level), ["name"] = level.Name },
                    ["heightFt"] = HeightFt,
                    ["flatSolids"] = flatSolids,
                    ["facesInspected"] = facesInspected,
                    ["facesKept"] = facesKept,
                    ["degenerateFaces"] = degenerateFaces,
                    ["faceLayerHistogram"] = layerHistogram,
                    ["closedLoops"] = loops.Count,
                    ["straightRuns"] = straightRuns,
                    ["jambCandidates"] = jambCandidates,
                    ["facePairs"] = pairsMade,
                    ["rejectedPairsThickness"] = pairsNoThickness,
                    ["rejectedPairsOverlap"] = pairsNoOverlap,
                    ["mergedCenterlines"] = merged.Count,
                    ["wallsPotential"] = wallsAfterMerge,
                    ["bridgedOpenings"] = bridgeStats.Bridged,
                    ["unbridgedGaps"] = bridgeStats.Unbridged,
                    ["straightRuns"] = straightRuns,
                    ["facePairs"] = pairsMade,
                    ["rejectedPairsThickness"] = pairsNoThickness,
                    ["rejectedPairsOverlap"] = pairsNoOverlap,
                    ["mergedCenterlines"] = merged.Count,
                    ["wallsCreated"] = created,
                    ["rejectedJamb"] = rejectedJamb,
                ["doorArcRejected"] = doorArcRejected,
                ["buildFailed"] = buildFailed,
                ["minWallLengthFt"] = MinWallLengthFt,
                ["typeSummary"] = typeSummary,
                ["createdIds"] = createdIds,
                ["rejects"] = rejects,
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

        // MergeCollinear below does the cross-loop collinear merge of the
        // centerline pieces produced above: pieces cluster by angle (wrap-safe
        // circular mean) then merge within a cluster by perpendicular offset
        // (RailTolFt) and along-track gap (MergeGapFt). Seam-split runs and
        // opening-split pieces rejoin here; duplicates collapse because a
        // redrawn piece with the same v and overlapping u loses to the longer
        // rail already in the chain.Opening bridging: gaps up to
        // MaxOpeningGapFt (door openings incl. trim) also merge when the
        // flanking pieces agree on thickness AND a classified jamb midpoint
        // sits in the cluster frame near a gap edge (BridgeJambSnapFt).
        private class BridgeStats
        {
            public int Silent;
            public int Bridged;
            public int Unbridged;
        }

        private List<WallPair> MergeCollinear(List<double[]> pieces, List<XYZ> jambMidpoints, BridgeStats stats, List<Dictionary<string, object>> rejects)
        {
            var wallPairs = new List<WallPair>();
            var items = pieces
                .Select((p, idx) => new
                {
                    ang = Math.Atan2(p[3] - p[1], p[2] - p[0]) < 0
                        ? Math.Atan2(p[3] - p[1], p[2] - p[0]) + Math.PI
                        : Math.Atan2(p[3] - p[1], p[2] - p[0]),
                    p // [sX, sY, eX, eY, thick]
                })
                .OrderBy(it => it.ang)
                .ToList();

            var clusters = new List<List<double[]>>();
            var curCluster = new List<double[]>();
            double curAng = -1;
            foreach (var it in items)
            {
                if (curCluster.Count == 0 || it.ang - curAng <= ClusterAngleTolRad)
                {
                    curCluster.Add(it.p);
                    if (curCluster.Count == 1) curAng = it.ang;
                }
                else
                {
                    clusters.Add(curCluster);
                    curCluster = new List<double[]> { it.p };
                    curAng = it.ang;
                }
            }
            if (curCluster.Count > 0) clusters.Add(curCluster);

            // wrap merge first/last clusters
            if (clusters.Count > 1)
            {
                var aF = clusters[0][0];
                var aL = clusters[clusters.Count - 1][clusters[clusters.Count - 1].Count - 1];
                double aFv = Math.Atan2(aF[3] - aF[1], aF[2] - aF[0]);
                double aLv = Math.Atan2(aL[3] - aL[1], aL[2] - aL[0]);
                double span = (aFv + Math.PI) - aLv;
                if (span <= ClusterAngleTolRad)
                {
                    clusters[clusters.Count - 1].AddRange(clusters[0]);
                    clusters.RemoveAt(0);
                }
            }

            var railsOut = new List<object[]>(); // deg(0), v(1), u0(2), u1(3), thick(4)
            foreach (var cl in clusters)
            {
                double cs0 = 0, sn0 = 0;
                foreach (var p in cl)
                {
                    double a0 = Math.Atan2(p[3] - p[1], p[2] - p[0]);
                    if (a0 < 0) a0 += Math.PI;
                    if (a0 >= Math.PI) a0 -= Math.PI;
                    cs0 += Math.Cos(a0);
                    sn0 += Math.Sin(a0);
                }
                double am = Math.Atan2(sn0, cs0);
                if (am < 0) am += Math.PI;
                if (am >= Math.PI) am -= Math.PI;
                double cu = Math.Cos(am), su = Math.Sin(am);

                var frags = new List<double[]>(); // v0, u0, u1, thick
                foreach (var p in cl)
                {
                    double u0 = p[0] * cu + p[1] * su, v0 = -p[0] * su + p[1] * cu;
                    double u1 = p[2] * cu + p[3] * su;
                    if (u1 < u0) { double t = u0; u0 = u1; u1 = t; }
                    frags.Add(new[] { v0, u0, u1, p[4] });
                }
                frags.Sort((a, b) => a[0].CompareTo(b[0]));

                var curRail = new List<double[]>();
                double railV = 0;
                foreach (var f in frags)
                {
                    if (curRail.Count == 0) { railV = f[0]; curRail.Add(f); }
                else if (Math.Abs(f[0] - railV) <= RailTolFt) { curRail.Add(f); }
                    else { FlushRailWithBridge(curRail, railV, railsOut, am, cu, su, jambMidpoints, stats, rejects); railV = f[0]; curRail.Add(f); }
            }
                FlushRailWithBridge(curRail, railV, railsOut, am, cu, su, jambMidpoints, stats, rejects);

                foreach (var r in railsOut)
                {
                    double v = (double)r[0], u0 = (double)r[1], u1 = (double)r[2], thick = (double)r[3];
                    wallPairs.Add(new WallPair
                    {
                        CenterStart = new XYZ(u0 * cu - v * su, u0 * su + v * cu, 0),
                        CenterEnd = new XYZ(u1 * cu - v * su, u1 * su + v * cu, 0),
                        Thickness = thick
                    });
                }
                railsOut.Clear();
            }
            return wallPairs;
        }

        private static void FlushRailWithBridge(List<double[]> curRail, double railV, List<object[]> railsOut,
            double am, double cu, double su, List<XYZ> jambMidpoints, BridgeStats stats,
            List<Dictionary<string, object>> rejects)
        {
            if (curRail.Count == 0) return;
            curRail.Sort((a, b) => a[1].CompareTo(b[1]));
            double u0 = curRail[0][1], u1 = curRail[0][2], thick = curRail[0][3];
            for (int k = 1; k < curRail.Count; k++)
            {
                double f0 = curRail[k][1], f1 = curRail[k][2];
                double gap = f0 - u1;
                if (gap <= MergeGapFt)
                {
                    if (f1 > u1) u1 = f1;
                    if (curRail[k][3] > thick) thick = curRail[k][3];
                    stats.Silent++;
                }
                else if (gap <= MaxOpeningGapFt &&
                         Math.Abs(curRail[k][3] - thick) <= BridgeThicknessTolFt &&
                         HasJambBetween(jambMidpoints, am, railV, u1, f0))
                {
                    if (f1 > u1) u1 = f1;
                    if (curRail[k][3] > thick) thick = curRail[k][3];
                    stats.Bridged++;
                }
                else
                {
                    railsOut.Add(new object[] { railV, u0, u1, thick });
                    stats.Unbridged++;
                    if (rejects != null && rejects.Count < 600)
                        rejects.Add(new Dictionary<string, object>
                        {
                            ["stage"] = "unbridgedGap",
                            ["gapLen"] = R2(gap),
                            ["thickA"] = R2(thick), ["thickB"] = R2(curRail[k][3]),
                            ["gapStart"] = P2(new XYZ(u1 * cu - railV * su, u1 * su + railV * cu, 0)),
                            ["gapEnd"] = P2(new XYZ(f0 * cu - railV * su, f0 * su + railV * cu, 0))
                        });
                    u0 = f0; u1 = f1; thick = curRail[k][3];
                }
            }
            railsOut.Add(new object[] { railV, u0, u1, thick });
            curRail.Clear();
        }

        // Reject diagnostics: first 600 culled candidates are logged with a
        // stage tag and geometry (ft, rounded) so runs can be diffed offline.

        private static double R2(double v) { return Math.Round(v, 2); }

        private static double[] P2(XYZ p)
        {
            return new[] { Math.Round(p.X, 2), Math.Round(p.Y, 2) };
        }

        // Bridge evidence test: any classified jamb midpoint lying on the rail
        // (perpendicular offset within RailTolFt of railV, cluster frame) with
        // an along-track coordinate near either side of the gap (this is the
        // opening's face — the closed hatch loop guarantees the jamb corners
        // touch the wall-face endpoints exactly).
        private static bool HasJambBetween(List<XYZ> jambMidpoints, double am, double railV, double gapStartU, double gapEndU)
        {
            double cu = Math.Cos(am), su = Math.Sin(am);
            foreach (var jp in jambMidpoints)
            {
                double jU = jp.X * cu + jp.Y * su;
                double jV = -jp.X * su + jp.Y * cu;
                if (Math.Abs(jV - railV) > RailTolFt) continue;
                if (gapStartU - BridgeJambSnapFt <= jU && jU <= gapEndU + BridgeJambSnapFt) return true;
            }
            return false;
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
            return "Create Walls from DWG Poche";
        }
    }
}
