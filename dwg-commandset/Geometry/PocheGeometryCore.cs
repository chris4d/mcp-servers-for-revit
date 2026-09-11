using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCPCommandSet.Geometry
{
    /// <summary>
    /// Pure 2D geometry core of create_walls_from_dwg_poche. No Revit API
    /// types: rings and midlines are plain point pairs and arrays, so the
    /// pipeline is unit-testable outside Revit (TUnit / CI). The Revit
    /// handler harvests hatch-face boundary rings (List&lt;List&lt;XYZ&gt;&gt;), feeds
    /// them here, then maps the resulting wall pairs onto Revit walls.
    /// Behaviour is a 1:1 port from the handler at snapshot
    /// dwg-snapshot-pre-geometry-refactor. All units in feet.
    /// </summary>
    public struct Pt
    {
        public double X, Y;
        public Pt(double x, double y) { X = x; Y = y; }
        public static Pt operator +(Pt a, Pt b) { return new Pt(a.X + b.X, a.Y + b.Y); }
        public static Pt operator -(Pt a, Pt b) { return new Pt(a.X - b.X, a.Y - b.Y); }
        public static Pt operator *(Pt a, double s) { return new Pt(a.X * s, a.Y * s); }
        public double Dot(Pt b) { return X * b.X + Y * b.Y; }
        public double Dist(Pt b) { double dx = X - b.X, dy = Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }
        public double Len() { return Math.Sqrt(X * X + Y * Y); }
    }

    public class BridgeStatsCore
    {
        public int Silent;
        public int Bridged;
        public int Unbridged;
    }

    /// <summary>
    /// Culled-candidate diagnostics: first 600 entries with per-stage cap of
    /// 150, each tagged by pipeline stage with rounded geometry.
    /// </summary>
    public class RejectLog
    {
        public const int TotalCap = 600;
        public const int PerStageCap = 150;
        private int _total;
        public List<Dictionary<string, object>> Entries = new List<Dictionary<string, object>>();
        public Dictionary<string, int> StageCounts = new Dictionary<string, int>();

        public void Log(string stage, Dictionary<string, object> fields)
        {
            if (!StageCounts.ContainsKey(stage)) StageCounts[stage] = 0;
            StageCounts[stage]++;
            if (StageCounts[stage] > PerStageCap || _total >= TotalCap) return;
            fields["stage"] = stage;
            Entries.Add(fields);
            _total++;
        }
    }

    public class WallPairCore
    {
        public double Sx, Sy, Ex, Ey, Thickness;
        public double Length
        {
            get { double dx = Ex - Sx, dy = Ey - Sy; return Math.Sqrt(dx * dx + dy * dy); }
        }
        public Pt Start { get { return new Pt(Sx, Sy); } }
        public Pt End { get { return new Pt(Ex, Ey); } }
    }

    public class PochePipelineOptions
    {
        public double MinWallThicknessFt = 2.0 / 12.0; // 2"
        public double MaxWallThicknessFt = 5.0;
        public double MinRunFt = 0.3;
        public double RunAngleTolDeg = 2.0;
        public double RailTolFt = 0.05;
        public double PairAngleTolDeg = 2.0;
        public double MinOverlapFrac = 0.7;
        public double ClusterAngleTolRad = 2.0 * Math.PI / 180.0;
        public double MergeGapFt = 0.8;
        public double MaxOpeningGapFt = 8.0;
        public double BridgeThicknessTolFt = 0.05;
        public double JambPerpMinDeg = 60.0;
        public double JambSiblingParallelTolDeg = 10.0;
        public double JambSiblingLenTol = 0.25;
        public double BridgeJambSnapFt = 0.6;
        public double PocheProximityFt = 0.15;
        public double PocketSlotDepthFt = 0.5;
        public double SliverRailTolFt = 0.1;
    }

    public class PochePipelineResult
    {
        public int ClosedLoops;
        public int StraightRuns;
        public int JambCandidates;
        public int FacePairs;
        public int PairsNoThickness;
        public int PairsNoOverlap;
        public int PairsInsidePoche;
        public int PairsOutsidePoche;
        public int SliversCulled;
        public int MergedCenterlines;
        public List<WallPairCore> Merged = new List<WallPairCore>();
        public List<WallPairCore> MergedAll = new List<WallPairCore>();
        public List<Pt> JambMidpoints = new List<Pt>();
        public BridgeStatsCore Bridge = new BridgeStatsCore();
        public RejectLog RejectLog = new RejectLog();
        internal List<double[]> CenterlinePieces = new List<double[]>();
    }

    public static class PocheGeometryCore
    {
        internal static double R2(double v) { return Math.Round(v, 2); }

        internal static double[] P2(Pt p) { return new[] { Math.Round(p.X, 2), Math.Round(p.Y, 2) }; }

        /// <summary>
        /// Full geometry pipeline over hatch-face boundary rings.
        /// </summary>
        public static PochePipelineResult RunPipeline(List<List<Pt>> loops, PochePipelineOptions opt)
        {
            if (loops == null) throw new ArgumentNullException("loops");
            if (opt == null) throw new ArgumentNullException("opt");
            var res = new PochePipelineResult { ClosedLoops = loops.Count };

            foreach (var vs in loops)
            {
                int n = vs.Count;
                if (n < 3) continue;

                // ---- straight runs: consecutive collinear edges ----
                var runs = new List<(Pt s, Pt e, double len)>();
                int i = 0;
                while (i < n)
                {
                    Pt d0 = vs[(i + 1) % n] - vs[i];
                    double L0 = d0.Len();
                    if (L0 < 1e-9) { i++; continue; }
                    Pt dirCur = d0 * (1.0 / L0);
                    double lenCur = L0;
                    int k = i;
                    while (k + 1 < n)
                    {
                        Pt n0 = vs[k + 1], n1 = vs[(k + 2) % n];
                        Pt dN = n1 - n0;
                        double LN = dN.Len();
                        if (LN < 1e-9) break;
                        Pt uN = dN * (1.0 / LN);
                        double dotAbs = Math.Abs(dirCur.Dot(uN));
                        double angDev = Math.Acos(Math.Min(1.0, dotAbs)) * 180.0 / Math.PI;
                        bool sameDir = angDev <= opt.RunAngleTolDeg;
                        if (sameDir)
                        {
                            var vv = n0 - vs[i];
                            var perp = vv - dirCur * vv.Dot(dirCur);
                            if (perp.Len() > opt.RailTolFt) sameDir = false;
                        }
                        if (!sameDir) break;
                        if (dirCur.Dot(uN) < 0) dirCur = new Pt(-dirCur.X, -dirCur.Y);
                        lenCur += LN;
                        k++;
                    }
                    if (lenCur >= opt.MinRunFt)
                    {
                        runs.Add((vs[i], vs[(k + 1) % n], lenCur));
                        res.StraightRuns++;
                    }
                    i = k + 1;
                }

                // ---- jamb classification (sibling test) ----
                int runCount = runs.Count;
                if (runCount >= 3)
                {
                    for (int j = 0; j < runCount; j++)
                    {
                        var prevR = runs[(j + runCount - 1) % runCount];
                        var nextR = runs[(j + 1) % runCount];
                        var dj = runs[j].e - runs[j].s;
                        var dp = prevR.e - prevR.s;
                        var dn = nextR.e - nextR.s;
                        double lj = dj.Len(), lp = dp.Len(), ln2 = dn.Len();
                        if (lj < 1e-9 || lp < 1e-9 || ln2 < 1e-9) continue;
                        double angP = Math.Acos(Math.Min(1.0, Math.Abs(dj.Dot(dp) / (lj * lp)))) * 180.0 / Math.PI;
                        double angN = Math.Acos(Math.Min(1.0, Math.Abs(dj.Dot(dn) / (lj * ln2)))) * 180.0 / Math.PI;
                        if (angP < opt.JambPerpMinDeg || angN < opt.JambPerpMinDeg) continue;

                        // Sibling test: a jamb is one of a PAIR - door
                        // openings produce two perpendicular short runs facing
                        // each other across the opening (parallel, near-equal
                        // length, within MaxOpeningGapFt).
                        var mj = (runs[j].s + runs[j].e) * 0.5;
                        bool hasSibling = false;
                        for (int s2 = 0; s2 < runCount && !hasSibling; s2++)
                        {
                            if (s2 == j) continue;
                            var ds2 = runs[s2].e - runs[s2].s;
                            double ls2 = ds2.Len();
                            if (ls2 < 1e-9) continue;
                            double parallelDev = Math.Acos(Math.Min(1.0, Math.Abs(dj.Dot(ds2) / (lj * ls2)))) * 180.0 / Math.PI;
                            if (parallelDev > opt.JambSiblingParallelTolDeg) continue;
                            double lenDev = Math.Abs(ls2 - lj);
                            if (lenDev > opt.JambSiblingLenTol * Math.Max(lj, ls2)) continue;
                            var ms = (runs[s2].s + runs[s2].e) * 0.5;
                            if (mj.Dist(ms) > opt.MaxOpeningGapFt) continue;
                            hasSibling = true;
                        }
                        if (!hasSibling) continue;
                        res.JambCandidates++;
                        res.JambMidpoints.Add(mj);
                    }
                }

                // ---- face pairing within the loop ----
                var runMatched = new bool[runCount];
                for (int a = 0; a < runCount; a++)
                {
                    var sa = runs[a].s;
                    var ea = runs[a].e;
                    var da = ea - sa;
                    double la = da.Len();
                    if (la < 1e-9) continue;
                    var ua = da * (1.0 / la);

                    for (int b = a + 1; b < runCount; b++)
                    {
                        var sb = runs[b].s;
                        var eb = runs[b].e;
                        var db = eb - sb;
                        double lb = db.Len();
                        if (lb < 1e-9) continue;
                        var ub = db * (1.0 / lb);

                        double dotAbs2 = Math.Abs(ua.Dot(ub));
                        double dev2 = Math.Acos(Math.Min(1.0, dotAbs2)) * 180.0 / Math.PI;
                        if (dev2 > opt.PairAngleTolDeg) continue;

                        var mb = (sb + eb) * 0.5;
                        var vv = mb - sa;
                        var perp = vv - ua * vv.Dot(ua);
                        double thick = perp.Len();
                        if (thick < opt.MinWallThicknessFt || thick > opt.MaxWallThicknessFt)
                        {
                            res.PairsNoThickness++;
                            res.RejectLog.Log("pairThickness", new Dictionary<string, object>
                            {
                                ["lenA"] = R2(la), ["lenB"] = R2(lb), ["thick"] = R2(thick),
                                ["aStart"] = P2(sa), ["aEnd"] = P2(ea),
                                ["bStart"] = P2(sb), ["bEnd"] = P2(eb)
                            });
                            continue;
                        }

                        double tb0 = (sb - sa).Dot(ua) / la;
                        double tb1 = (eb - sa).Dot(ua) / la;
                        double lo = Math.Max(0.0, Math.Min(tb0, tb1));
                        double hi = Math.Min(1.0, Math.Max(tb0, tb1));
                        if (hi <= lo) continue;
                        double overlapLen = (hi - lo) * la;
                        double minLen = Math.Min(la, lb);
                        if (overlapLen / minLen < opt.MinOverlapFrac)
                        {
                            res.PairsNoOverlap++;
                            res.RejectLog.Log("pairOverlap", new Dictionary<string, object>
                            {
                                ["lenA"] = R2(la), ["lenB"] = R2(lb), ["thick"] = R2(thick),
                                ["overlapFrac"] = R2(overlapLen / minLen),
                                ["aStart"] = P2(sa), ["aEnd"] = P2(ea),
                                ["bStart"] = P2(sb), ["bEnd"] = P2(eb)
                            });
                            continue;
                        }

                        var pStart = sa + da * lo;
                        var projS = sb + ub * (pStart - sb).Dot(ub);
                        var c0 = (pStart + projS) * 0.5;
                        var pEnd = sa + da * hi;
                        var projE = sb + ub * (pEnd - sb).Dot(ub);
                        var c1 = (pEnd + projE) * 0.5;

                        var mid = (c0 + c1) * 0.5;
                        if (!InsideAnyPoche(mid, loops, opt.PocheProximityFt))
                        {
                            res.PairsOutsidePoche++;
                            res.RejectLog.Log("outsidePoche", new Dictionary<string, object>
                            {
                                ["lenA"] = R2(la), ["lenB"] = R2(lb), ["thick"] = R2(thick),
                                ["mid"] = P2(mid),
                                ["aStart"] = P2(sa), ["aEnd"] = P2(ea),
                                ["bStart"] = P2(sb), ["bEnd"] = P2(eb)
                            });
                            continue;
                        }
                        res.PairsInsidePoche++;

                        res.CenterlinePieces.Add(new[] { c0.X, c0.Y, c1.X, c1.Y, thick, dev2 });
                        res.FacePairs++;
                        runMatched[a] = true;
                        runMatched[b] = true;
                    }
                }

                // Runs with no partner at all: pure linework / unpaired faces.
                for (int a = 0; a < runCount; a++)
                {
                    if (runMatched[a]) continue;
                    var ra = runs[a];
                    var dir = ra.e - ra.s;
                    double rl = dir.Len();
                    if (rl < 1e-9) continue;
                    var u2 = dir * (1.0 / rl);
                    var mid = (ra.s + ra.e) * 0.5;
                    var nrm = new Pt(-u2.Y, u2.X);
                    res.RejectLog.Log("unpairedRun", new Dictionary<string, object>
                    {
                        ["len"] = R2(ra.len),
                        ["mid"] = P2(mid),
                        ["probe"] = P2(mid + nrm * Math.Min(opt.MaxWallThicknessFt, 1.0))
                    });
                }
            }

            // ---- independent collinear merge (with opening bridging) ----
            res.Merged = MergeCollinear(res.CenterlinePieces, res.JambMidpoints, res.Bridge, opt, res.RejectLog);
            res.MergedAll = new List<WallPairCore>(res.Merged);

            // ---- pocket-door slot filter (nested sliver pairs) ----
            var kept = new List<WallPairCore>();
            foreach (var p in res.Merged)
            {
                if (IsNestedSliver(p, res.Merged, opt))
                {
                    res.SliversCulled++;
                    res.RejectLog.Log("nestedSliver", new Dictionary<string, object>
                    {
                        ["thick"] = R2(p.Thickness),
                        ["start"] = new[] { Math.Round(p.Sx, 2), Math.Round(p.Sy, 2) },
                        ["end"] = new[] { Math.Round(p.Ex, 2), Math.Round(p.Ey, 2) }
                    });
                    continue;
                }
                kept.Add(p);
            }
            res.Merged = kept;
            res.MergedCenterlines = res.Merged.Count;
            return res;
        }

        /// <summary>
        /// Cross-loop collinear merge of centerline pieces.
        /// </summary>
        public static List<WallPairCore> MergeCollinear(List<double[]> pieces, List<Pt> jambMidpoints,
            BridgeStatsCore stats, PochePipelineOptions opt, RejectLog rejects)
        {
            var wallPairs = new List<WallPairCore>();
            var items = pieces
                .Select(p => new
                {
                    ang = Math.Atan2(p[3] - p[1], p[2] - p[0]) < 0
                        ? Math.Atan2(p[3] - p[1], p[2] - p[0]) + Math.PI
                        : Math.Atan2(p[3] - p[1], p[2] - p[0]),
                    p // [sX, sY, eX, eY, thick, dev]
                })
                .OrderBy(it => it.ang)
                .ToList();

            var clusters = new List<List<double[]>>();
            var curCluster = new List<double[]>();
            double curAng = -1;
            foreach (var it in items)
            {
                if (curCluster.Count == 0 || it.ang - curAng <= opt.ClusterAngleTolRad)
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

            // wrap merge first/last clusters (angles are in [0, PI); a first
            // cluster near 0 and a last cluster near PI wrap together).
            if (clusters.Count > 1)
            {
                var aF = clusters[0][0];
                var aL = clusters[clusters.Count - 1][clusters[clusters.Count - 1].Count - 1];
                double aFv = Math.Atan2(aF[3] - aF[1], aF[2] - aF[0]);
                double aLv = Math.Atan2(aL[3] - aL[1], aL[2] - aL[0]);
                double span = (aFv + Math.PI) - aLv;
                if (span <= opt.ClusterAngleTolRad)
                {
                    clusters[clusters.Count - 1].AddRange(clusters[0]);
                    clusters.RemoveAt(0);
                }
            }

            var railsOut = new List<double[]>(); // v(0), u0(1), u1(2), thick(3)
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
                frags.Sort((aa, bb) => aa[0].CompareTo(bb[0]));

                var curRail = new List<double[]>();
                double railV = 0;
                foreach (var f in frags)
                {
                    if (curRail.Count == 0) { railV = f[0]; curRail.Add(f); }
                    else if (Math.Abs(f[0] - railV) <= opt.RailTolFt) { curRail.Add(f); }
                    else { FlushRailWithBridge(curRail, railV, railsOut, am, cu, su, jambMidpoints, stats, opt, rejects); railV = f[0]; curRail.Add(f); }
                }
                FlushRailWithBridge(curRail, railV, railsOut, am, cu, su, jambMidpoints, stats, opt, rejects);

                foreach (var r in railsOut)
                {
                    double v = r[0], u0 = r[1], u1 = r[2], thick = r[3];
                    wallPairs.Add(new WallPairCore
                    {
                        Sx = u0 * cu - v * su,
                        Sy = u0 * su + v * cu,
                        Ex = u1 * cu - v * su,
                        Ey = u1 * su + v * cu,
                        Thickness = thick
                    });
                }
                railsOut.Clear();
            }
            return wallPairs;
        }

        /// <summary>
        /// Chain one rail's fragments: silent merge for sloppy gaps, bridged
        /// merge for opening gaps with thickness agreement + jamb evidence;
        /// otherwise the rail splits (and the gap is logged).
        /// </summary>
        public static void FlushRailWithBridge(List<double[]> curRail, double railV, List<double[]> railsOut,
            double am, double cu, double su, List<Pt> jambMidpoints, BridgeStatsCore stats,
            PochePipelineOptions opt, RejectLog rejects)
        {
            if (curRail.Count == 0) return;
            curRail.Sort((a, b) => a[1].CompareTo(b[1]));
            double u0 = curRail[0][1], u1 = curRail[0][2], thick = curRail[0][3];
            for (int k = 1; k < curRail.Count; k++)
            {
                double f0 = curRail[k][1], f1 = curRail[k][2];
                double gap = f0 - u1;
                if (gap <= opt.MergeGapFt)
                {
                    if (f1 > u1) u1 = f1;
                    if (curRail[k][3] > thick) thick = curRail[k][3];
                    stats.Silent++;
                }
                else if (gap <= opt.MaxOpeningGapFt &&
                         Math.Abs(curRail[k][3] - thick) <= opt.BridgeThicknessTolFt &&
                         HasJambBetween(jambMidpoints, am, railV, u1, f0, opt))
                {
                    if (f1 > u1) u1 = f1;
                    if (curRail[k][3] > thick) thick = curRail[k][3];
                    stats.Bridged++;
                }
                else
                {
                    railsOut.Add(new double[] { railV, u0, u1, thick });
                    stats.Unbridged++;
                    if (rejects != null)
                        rejects.Log("unbridgedGap", new Dictionary<string, object>
                        {
                            ["gapLen"] = R2(gap),
                            ["thickA"] = R2(thick), ["thickB"] = R2(curRail[k][3]),
                            ["gapStart"] = new[] { Math.Round(u1 * cu - railV * su, 2), Math.Round(u1 * su + railV * cu, 2) },
                            ["gapEnd"] = new[] { Math.Round(f0 * cu - railV * su, 2), Math.Round(f0 * su + railV * cu, 2) }
                        });
                    u0 = f0; u1 = f1; thick = curRail[k][3];
                }
            }
            railsOut.Add(new double[] { railV, u0, u1, thick });
            curRail.Clear();
        }

        /// <summary>
        /// Bridge evidence: a classified jamb midpoint on the rail (perp
        /// offset within RailTolFt in the cluster frame) with an
        /// along-track coordinate near either side of the gap.
        /// </summary>
        public static bool HasJambBetween(List<Pt> jambMidpoints, double am, double railV, double gapStartU, double gapEndU, PochePipelineOptions opt)
        {
            double cu = Math.Cos(am), su = Math.Sin(am);
            foreach (var jp in jambMidpoints)
            {
                double jU = jp.X * cu + jp.Y * su;
                double jV = -jp.X * su + jp.Y * cu;
                if (Math.Abs(jV - railV) > opt.RailTolFt) continue;
                if (gapStartU - opt.BridgeJambSnapFt <= jU && jU <= gapEndU + opt.BridgeJambSnapFt) return true;
            }
            return false;
        }

        /// <summary>
        /// Poche containment: true when p lies inside one hatch polygon or
        /// within toleranceFt of one of its edges. The near-edge allowance
        /// covers multi-wythe seams (adjacent hatch patches with a thin
        /// unfilled gap between materials).
        /// </summary>
        public static bool InsideAnyPoche(Pt p, List<List<Pt>> polys, double toleranceFt)
        {
            foreach (var poly in polys)
            {
                if (poly == null || poly.Count < 3) continue;
                if (PointNearOrInsidePoly(poly, p, toleranceFt)) return true;
            }
            return false;
        }

        private static bool PointNearOrInsidePoly(List<Pt> poly, Pt p, double toleranceFt)
        {
            int n = poly.Count;
            if (toleranceFt > 0)
            {
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    var a = poly[i]; var b = poly[j];
                    var ab = b - a;
                    double L = ab.Len();
                    if (L < 1e-9) continue;
                    double t = (p - a).Dot(ab) / (L * L);
                    if (t < 0.0 || t > 1.0) continue;
                    var q = a + ab * t;
                    if (p.Dist(q) <= toleranceFt) return true;
                }
            }
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = poly[i]; var b = poly[j];
                if ((a.Y > p.Y) != (b.Y > p.Y))
                {
                    double xInter = (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X;
                    if (p.X < xInter) inside = !inside;
                }
            }
            return inside;
        }

        /// <summary>
        /// Nested sliver test (pocket-door slots): true when pair a is thin
        /// (&lt; PocketSlotDepthFt) and a clearly longer, thicker,
        /// near-parallel mate exists whose band contains a's centerline rail
        /// and whose span contains a's span.
        /// </summary>
        public static bool IsNestedSliver(WallPairCore a, List<WallPairCore> all, PochePipelineOptions opt)
        {
            if (a.Thickness >= opt.PocketSlotDepthFt) return false;
            double dAx = a.Ex - a.Sx, dAy = a.Ey - a.Sy;
            double lenA = Math.Sqrt(dAx * dAx + dAy * dAy);
            if (lenA < 1e-9) return false;
            var uAx = dAx / lenA; var uAy = dAy / lenA;
            foreach (var b in all)
            {
                if (ReferenceEquals(a, b)) continue;
                if (b.Thickness < a.Thickness + 0.02) continue;
                double dBx = b.Ex - b.Sx, dBy = b.Ey - b.Sy;
                double lenB = Math.Sqrt(dBx * dBx + dBy * dBy);
                if (lenB < 1e-9) continue;
                if (lenB < lenA * 1.25) continue; // must be clearly longer

                if (Math.Abs(uAx * (dBx / lenB) + uAy * (dBy / lenB)) < 0.995) continue; // near-parallel rails
                var uBx = dBx / lenB; var uBy = dBy / lenB;
                var nBx = -uBy; var nBy = uBx;
                var mBx = (b.Sx + b.Ex) * 0.5; var mBy = (b.Sy + b.Ey) * 0.5;
                var mAx = (a.Sx + a.Ex) * 0.5; var mAy = (a.Sy + a.Ey) * 0.5;
                double rx = mAx - mBx, ry = mAy - mBy;
                double along = rx * uBx + ry * uBy;
                double perp = rx * nBx + ry * nBy;
                if (Math.Abs(perp) > b.Thickness / 2.0 + opt.SliverRailTolFt) continue;

                double a0 = (a.Sx - mBx) * uBx + (a.Sy - mBy) * uBy;
                double a1 = (a.Ex - mBx) * uBx + (a.Ey - mBy) * uBy;
                double b0 = along - lenB / 2.0, b1 = along + lenB / 2.0;
                double lo = Math.Min(a0, a1), hi = Math.Max(a0, a1);
                if (lo < b0 - 0.1 || hi > b1 + 0.1) continue;
                return true;
            }
            return false;
        }
    }
}
