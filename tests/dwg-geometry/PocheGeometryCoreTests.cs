using RevitMCPCommandSet.Geometry;
using TUnit.Assertions;
using TUnit.Core;

namespace RevitMCPCommandSet.Geometry.Tests
{
    // Scenario builders: each loop is a closed hatch-face ring (feet, deduped
    // and without a repeated closing vertex). Face decomposition mirrors
    // Revit's GetEdgesAsCurveLoops: outer ring and interior holes come back
    // as separate rings.

    public static class RingHelpers
    {
        public static List<Pt> Ring(params (double x, double y)[] pts)
        {
            var l = new List<Pt>();
            foreach (var p in pts) l.Add(new Pt(p.x, p.y));
            return l;
        }
    }

    public class StraightBandTests
    {
        private static PochePipelineOptions Opt(double maxT = 5.0)
        {
            return new PochePipelineOptions { MaxWallThicknessFt = maxT };
        }

        [Test]
        public async Task CleanStraightBand_ProducesSingleWallThroughHatch()
        {
            var loops = new List<List<Pt>>
            {
                RingHelpers.Ring((0, 10), (10, 10), (10, 11), (0, 11))
            };
            var res = PocheGeometryCore.RunPipeline(loops, Opt());

            await Assert.That(res.Merged.Count).IsEqualTo(1);
            var w = res.Merged[0];
            await Assert.That(w.Length).IsEqualTo(10.0).Within(0.01);
            await Assert.That(w.Thickness).IsEqualTo(1.0).Within(0.01);
            await Assert.That(Math.Min(w.Sy, w.Ey)).IsEqualTo(10.5).Within(0.01);
            await Assert.That(res.FacePairs).IsEqualTo(1);
        }

        [Test]
        public async Task RoomCrosser_IsCulledByPocheContainment()
        {
            // U-shaped hatch: two 1ft walls joined by a 5ft sill; the cavity
            // (x 1..4, y 1..6) is un-hatched. Opposite-side rails pair across
            // the cavity (thick 3ft, passes the 5ft band) but their
            // centerline lies in the open cavity - containment culls them.
            var loop = RingHelpers.Ring(
                (0, 0), (5, 0), (5, 6), (4, 6), (4, 1), (1, 1), (1, 6), (0, 6));
            var loops = new List<List<Pt>> { loop };
            var res = PocheGeometryCore.RunPipeline(loops, Opt(5.0));

            foreach (var w in res.Merged)
            {
                var midX = (w.Sx + w.Ex) / 2.0;
                var midY = (w.Sy + w.Ey) / 2.0;
                bool cavityPair = midX > 1.2 && midX < 4.0 && midY > 1.0;
                await Assert.That(cavityPair).IsFalse();
            }
            await Assert.That(res.PairsOutsidePoche).IsGreaterThan(0);
            // The two vertical walls (x≈0.5 / x≈4.5 rails) and the sill wall
            // (y≈0.5 span, legitimate in the U anatomy) survive.
            await Assert.That(res.Merged.Count).IsEqualTo(3);
        }

        [Test]
        public async Task SiblingJambs_BridgeTheTwoPieceWall()
        {
            // Two wall pieces 6ft long with a 4ft opening: each opening face
            // is a full band-thickness run, so both sides have paired jamb
            // midpoints. Expect one bridged continuous wall.
            var bot = 10.0;
            var loops = new List<List<Pt>>
            {
                RingHelpers.Ring((0, bot), (6, bot), (6, bot + 1), (0, bot + 1)),
                RingHelpers.Ring((10, bot), (16, bot), (16, bot + 1), (10, bot + 1))
            };
            var res = PocheGeometryCore.RunPipeline(loops, Opt());

            await Assert.That(res.Merged.Count).IsEqualTo(1);
            await Assert.That(res.Merged[0].Length).IsGreaterThan(15.0);
            await Assert.That(res.Bridge.Bridged).IsEqualTo(1);
            await Assert.That(res.JambCandidates).IsGreaterThanOrEqualTo(2);
        }
    }

    public class PocketDoorTests
    {
        private static PochePipelineOptions Opt() => new PochePipelineOptions { MaxWallThicknessFt = 5.0 };

        /// <summary>
        /// Pocket door slot anatomy from test.dwg (x 966.7-976.8, y
        /// 109.2-110.2): a full 1ft band ring plus the slot ring (4ft x
        /// 0.34ft void). The slot strips pair into short thin walls inside
        /// the band; only the band wall must survive.
        /// </summary>
        [Test]
        public async Task PocketSlot_StripsDoNotCreateWalls_BandSurvives()
        {
            var loops = new List<List<Pt>>
            {
                RingHelpers.Ring((0, 0), (10, 0), (10, 1), (0, 1)),          // band outer
                RingHelpers.Ring((2, 0.41), (6, 0.41), (6, 0.75), (2, 0.75))  // slot void (hole)
            };

            var res = PocheGeometryCore.RunPipeline(loops, Opt());

            bool hasBand = res.Merged.Any(w =>
                Math.Abs(w.Thickness - 1.0) < 0.05 && w.Length > 8.0);
            await Assert.That(hasBand).IsTrue();

            // No surviving wall may be a thin slot strip wall.
            bool hasSlotStripWall = res.Merged.Any(w => w.Thickness < 0.5 &&
                w.Length > 0.5 && Math.Abs(((w.Sy + w.Ey) / 2.0) - 0.58) < 0.4 &&
                w.Sx >= 0.0 && w.Ex <= 10.0);
            await Assert.That(hasSlotStripWall).IsFalse();
        }
    }

    public class InvariantTests
    {
        private static PochePipelineOptions Opt() => new PochePipelineOptions { MaxWallThicknessFt = 5.0 };

        [Test]
        public async Task EverySurvivingWallMidlineLiesInsideHatch()
        {
            var loops = new List<List<Pt>>
            {
                RingHelpers.Ring((0, 0), (20, 0), (20, 1), (0, 1)),
                RingHelpers.Ring((14, 0.41), (18, 0.75), (18, 0.75), (14, 0.41))
            };
            var slot = RingHelpers.Ring((14, 0.41), (18, 0.41), (18, 0.75), (14, 0.75));
            var res = PocheGeometryCore.RunPipeline(new List<List<Pt>> { loops[0], slot }, Opt());

            foreach (var w in res.Merged)
            {
                var mx = (w.Sx + w.Ex) / 2.0;
                var my = (w.Sy + w.Ey) / 2.0;
                var inside = PocheGeometryCore.InsideAnyPoche(new Pt(mx, my),
                    new List<List<Pt>> { loops[0], slot }, 0.06);
                await Assert.That(inside).IsTrue();
            }
        }

        [Test]
        public async Task ReturnedWallTypeIsMonotonicByThickness()
        {
            // thickness must be in the [MinWallThicknessFt..MaxWallThicknessFt]
            var loops = new List<List<Pt>>
            {
                RingHelpers.Ring((0, 0), (10, 0), (10, 1), (0, 1))
            };
            var res = PocheGeometryCore.RunPipeline(loops, Opt());
            foreach (var w in res.Merged)
            {
                await Assert.That(w.Thickness).IsGreaterThanOrEqualTo(2.0 / 12.0);
                await Assert.That(w.Thickness).IsLessThanOrEqualTo(5.0 + 0.01);
            }
        }
    }

    // Post-merge cleanup pass: junction duplicate bands, crossing cap
    // stubs, same-rail fragment merge/cull. All rules run over the final
    // wall list with no knowledge of the intended layout; the golden
    // reference fixture doubles as a safety net (nothing real may be culled).
    public class DedupAndCleanTests
    {
        private static PochePipelineOptions Opt() => new PochePipelineOptions { MaxWallThicknessFt = 5.0 };

        private static WallPairCore W(double sx, double sy, double ex, double ey, double thick)
            => new WallPairCore { Sx = sx, Sy = sy, Ex = ex, Ey = ey, Thickness = thick };

        [Test]
        public async Task SameRailFragment_AbsorbedIntoLongerKeeper()
        {
            var walls = new List<WallPairCore>
            {
                W(0, 10, 7, 10, 1.0),      // keeper
                W(6, 10, 16, 10, 1.0)      // overlapping piece on the same rail -> absorbed
            };
            var stats = new PocheGeometryCore.DedupStats();
            var outWalls = PocheGeometryCore.DedupAndClean(walls, Opt(), null, stats);

            await Assert.That(outWalls.Count).IsEqualTo(1);
            await Assert.That(stats.Absorbed).IsEqualTo(1);
            await Assert.That(outWalls[0].Length).IsEqualTo(16.0).Within(0.01);
        }

        [Test]
        public async Task SameRailGap_WallsStaySeparate()
        {
            // Real walls can sit a few feet apart on one rail (openings);
            // a gap must NOT be bridged by the post-merge absorb rule.
            var walls = new List<WallPairCore>
            {
                W(0, 10, 7, 10, 1.0),
                W(9, 10, 16, 10, 1.0)      // 2ft gap: stays a separate wall
            };
            var stats = new PocheGeometryCore.DedupStats();
            var outWalls = PocheGeometryCore.DedupAndClean(walls, Opt(), null, stats);

            await Assert.That(outWalls.Count).IsEqualTo(2);
            await Assert.That(stats.Absorbed).IsEqualTo(0);
        }

        [Test]
        public async Task OverlappingSameRailFragment_Culled()
        {
            var walls = new List<WallPairCore>
            {
                W(0, 10, 20, 10, 1.0),   // keeper, longer
                W(5, 10.02, 12, 10.02, 1.0) // overlapping piece on near-identical rail
            };
            var stats = new PocheGeometryCore.DedupStats();
            var outWalls = PocheGeometryCore.DedupAndClean(walls, Opt(), null, stats);

            await Assert.That(outWalls.Count).IsEqualTo(1);
            await Assert.That(stats.Fragments).IsEqualTo(1);
        }

        [Test]
        public async Task ThickJunctionBand_HuggingWallEdge_IsCulled()
        {
            // Long thin wall + short thick band just outside its face
            // (junction thickening): the band must be culled.
            var walls = new List<WallPairCore>
            {
                W(0, 10, 20, 10, 1.0),   // keeper
                W(5, 8.9, 10, 8.9, 3.0)  // thick band, rail 1.1 off the keeper rail
            };
            var stats = new PocheGeometryCore.DedupStats();
            var outWalls = PocheGeometryCore.DedupAndClean(walls, Opt(), null, stats);

            await Assert.That(outWalls.Count).IsEqualTo(1);
            await Assert.That(stats.Bands).IsEqualTo(1);
        }

        [Test]
        public async Task RealWallInsideKeeperBand_IsNotCulled()
        {
            // Regression shape from test.dwg: a short real wall whose rail sits
            // INSIDE a longer keeper's band (perp < keeper half-thickness) must
            // survive: it is not an edge-hugging junction band.
            var walls = new List<WallPairCore>
            {
                W(0, 10.25, 17, 10.25, 1.5),   // keeper (long horizontal, y=10.25)
                W(14.0, 10.0, 15.25, 10.0, 2.0) // real short wall, rail 0.25 inside keeper band
            };
            var stats = new PocheGeometryCore.DedupStats();
            var outWalls = PocheGeometryCore.DedupAndClean(walls, Opt(), null, stats);

            await Assert.That(outWalls.Count).IsEqualTo(2);
            await Assert.That(stats.Fragments + stats.Bands + stats.Stubs).IsEqualTo(0);
        }

        [Test]
        public async Task CrossingCapStub_AgainstLongKeeper_IsCulled()
        {
            // End-cap artifact: short thick stub crossing the body of a much
            // longer wall (crossing point well inside both spans).
            var walls = new List<WallPairCore>
            {
                W(10, 0, 10, 20, 1.0),      // keeper: vertical wall L=20
                W(8, 10, 11, 10, 3.0)       // stub L=3 crossing at (10,10)
            };
            var stats = new PocheGeometryCore.DedupStats();
            var outWalls = PocheGeometryCore.DedupAndClean(walls, Opt(), null, stats);

            await Assert.That(outWalls.Count).IsEqualTo(1);
            await Assert.That(stats.Stubs).IsEqualTo(1);
        }

        [Test]
        public async Task RectangleCapStub_CrossingTwoParallelKeepers_IsCulled()
        {
            // Closed-rectangle pochte caps: two short parallel walls plus a
            // thick cap crossing both rails (keepers too short for the 2x rule).
            var walls = new List<WallPairCore>
            {
                W(0, 10, 4, 10, 1.0),
                W(0, 11, 4, 11, 1.0),
                W(2, 9.5, 2, 11.5, 3.0)      // cap: crosses both rails
            };
            var stats = new PocheGeometryCore.DedupStats();
            var outWalls = PocheGeometryCore.DedupAndClean(walls, Opt(), null, stats);

            await Assert.That(outWalls.Count).IsEqualTo(2);
            await Assert.That(stats.Stubs).IsEqualTo(1);
        }

        [Test]
        public async Task LegitTWall_NotCulled()
        {
            // A real T-junction: thin stub ending at the keeper's face (its
            // centerline stops at the rail, margin fails) plus a thin nub must
            // survive; neither is thick, neither crosses interiorly.
            var walls = new List<WallPairCore>
            {
                W(0, 10, 20, 10, 1.0),        // keeper
                W(5, 10, 5, 13, 0.83),        // T stub: starts AT the keeper rail
                W(9.8, 10, 11, 10, 0.75)      // thin nub overlapping the rail
            };
            var stats = new PocheGeometryCore.DedupStats();
            var outWalls = PocheGeometryCore.DedupAndClean(walls, Opt(), null, stats);

            await Assert.That(outWalls.Count).IsEqualTo(3);
            await Assert.That(stats.Fragments + stats.Bands + stats.Stubs).IsEqualTo(0);
        }

        [Test]
        public async Task GoldenReferenceWalls_SurviveUnchanged()
        {
            // Safety invariant: the cleanup pass may never cull or absorb a
            // wall from the golden reference layout (173 real walls from
            // test.dwg). Exercises the exact rule thresholds against real
            // wall geometry - adjacency cases included.
            // Walk up from the test bin dir to the fixtures folder.
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            string path = null;
            while (dir != null && path == null)
            {
                var cand = System.IO.Path.Combine(dir.FullName, "fixtures", "golden-targets.json");
                if (System.IO.File.Exists(cand)) path = cand;
                else dir = dir.Parent;
            }
            await Assert.That(path).IsNotNull();
            var json = await System.IO.File.ReadAllTextAsync(path);
            var walls = new List<WallPairCore>();
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    // fixture stores reference layout at +500ft X offset; strip it
                    // so coordinates match the created-wall frame used by the rules.
                    walls.Add(W(
                        el.GetProperty("sx").GetDouble() - 500.0,
                        el.GetProperty("sy").GetDouble(),
                        el.GetProperty("ex").GetDouble() - 500.0,
                        el.GetProperty("ey").GetDouble(),
                        el.GetProperty("w").GetDouble()));
                }
            }

            var stats = new PocheGeometryCore.DedupStats();
            var outWalls = PocheGeometryCore.DedupAndClean(walls, Opt(), null, stats);

            await Assert.That(outWalls.Count).IsEqualTo(walls.Count);
            await Assert.That(stats.Fragments + stats.Bands + stats.Stubs).IsEqualTo(0);
            await Assert.That(stats.Absorbed).IsEqualTo(0);
        }
    }

    // End extension: walls end on crossing walls' rails (reference
    // convention). Only outward moves; crossing wall must reach the
    // junction and be long enough to be a wall.
    public class ExtendEndsTests
    {
        private static PochePipelineOptions Opt() => new PochePipelineOptions { MaxWallThicknessFt = 5.0 };

        private static WallPairCore W(double sx, double sy, double ex, double ey, double thick)
            => new WallPairCore { Sx = sx, Sy = sy, Ex = ex, Ey = ey, Thickness = thick };

        [Test]
        public async Task EndNearCrossingWall_ExtendsToItsRail()
        {
            var walls = new List<WallPairCore>
            {
                W(2, 10, 8, 10, 1.0),   // stops 2ft short of the vertical wall's rail
                W(10, 5, 10, 25, 1.0)   // vertical crossing wall
            };
            int extended;
            var outWalls = PocheGeometryCore.ExtendEnds(walls, Opt(), null, null, out extended);

            await Assert.That(extended).IsEqualTo(1);
            await Assert.That(outWalls[0].Ex).IsEqualTo(10.0).Within(0.01);
            await Assert.That(outWalls[0].Sx).IsEqualTo(2.0).Within(0.01);
        }

        [Test]
        public async Task CrossingWallTooShort_NoExtension()
        {
            var walls = new List<WallPairCore>
            {
                W(2, 10, 8, 10, 1.0),
                W(10, 12, 10, 25, 1.0)   // rail crosses at (10,10) but span starts at y=12
            };
            int extended;
            var outWalls = PocheGeometryCore.ExtendEnds(walls, Opt(), null, null, out extended);

            await Assert.That(extended).IsEqualTo(0);
            await Assert.That(outWalls[0].Ex).IsEqualTo(8.0).Within(0.01);
        }

        [Test]
        public async Task BeyondMaxRange_NoExtension()
        {
            var walls = new List<WallPairCore>
            {
                W(2, 10, 4, 10, 1.0),   // 6ft short of the crossing rail
                W(10, 5, 10, 25, 1.0)
            };
            int extended;
            var outWalls = PocheGeometryCore.ExtendEnds(walls, Opt(), null, null, out extended);

            await Assert.That(extended).IsEqualTo(0);
        }

        [Test]
        public async Task ParallelWall_IsNotAnExtensionTarget()
        {
            var walls = new List<WallPairCore>
            {
                W(2, 10, 8, 10, 1.0),
                W(12, 10, 20, 10, 1.0)   // parallel, 4ft ahead: not a crossing wall
            };
            int extended;
            var outWalls = PocheGeometryCore.ExtendEnds(walls, Opt(), null, null, out extended);

            await Assert.That(extended).IsEqualTo(0);
        }

        [Test]
        public async Task TwoEnds_BothExtendBetweenCrossingWalls()
        {
            // Vertical stub bracketed by two horizontal walls that span the
            // junction: both ends extend onto their rails (the test.dwg
            // bracket pattern).
            var walls = new List<WallPairCore>
            {
                W(10, 18, 10, 21, 1.0),   // vertical stub
                W(5, 15, 12, 15, 1.0),    // horizontal below, spans x=10
                W(5, 23, 12, 23, 1.0)     // horizontal above, spans x=10
            };
            int extended;
            var outWalls = PocheGeometryCore.ExtendEnds(walls, Opt(), null, null, out extended);

            await Assert.That(extended).IsEqualTo(2);
            await Assert.That(outWalls[0].Sy).IsEqualTo(15.0).Within(0.01);
            await Assert.That(outWalls[0].Ey).IsEqualTo(23.0).Within(0.01);
        }
    }

    // Evidence-gated gap bridging: beyond jamb runs, a crossing wall's
    // piece through the gap or hatch at the gap midpoint explains a
    // junction interruption; door openings carry neither evidence.
    public class JunctionEvidenceTests
    {
        private static PochePipelineOptions Opt()
        {
            var o = new PochePipelineOptions { MaxWallThicknessFt = 5.0 };
            o.EnableJunctionEvidenceBridge = true;
            return o;
        }

        [Test]
        public async Task GapWithCrossingPiece_IsBridged()
        {
            // rail fragments on v=0 (am=0: cu=1, su=0): [0,5] and [8.5,11.5],
            // thickness 1.0 - gap 3.5ft (beyond the silent-merge band) with a
            // perpendicular piece straddling the rail inside the gap.
            var curRail = new List<double[]> { new[] { 0.0, 0.0, 5.0, 1.0 }, new[] { 0.0, 8.5, 11.5, 1.0 } };
            var pieces = new List<double[]>
            {
                new[] { 6.5, -2.0, 6.5, 2.0, 1.0, 0 }   // vertical piece crossing the rail at u=6.5
            };
            var railsOut = new List<double[]>();
            var stats = new BridgeStatsCore();
            PocheGeometryCore.FlushRailWithBridge(curRail, 0.0, railsOut, 0.0, 1.0, 0.0,
                new List<Pt>(), stats, Opt(), null, pieces, null);

            await Assert.That(railsOut.Count).IsEqualTo(1);
            await Assert.That(railsOut[0][1]).IsEqualTo(0.0).Within(0.01);      // u0
            await Assert.That(railsOut[0][2]).IsEqualTo(11.5).Within(0.01);     // u1 - bridged
            await Assert.That(stats.BridgedEvidence).IsEqualTo(1);
        }

        [Test]
        public async Task GapWithHatchAtMidpoint_IsBridged()
        {
            var curRail = new List<double[]> { new[] { 0.0, 0.0, 5.0, 1.0 }, new[] { 0.0, 8.5, 11.5, 1.0 } };
            // hatch patch covering the gap midpoint (6.75, 0)
            var loops = new List<List<Pt>>
            {
                RingHelpers.Ring((6.0, -1.0), (7.5, -1.0), (7.5, 1.0), (6.0, 1.0))
            };
            var railsOut = new List<double[]>();
            var stats = new BridgeStatsCore();
            PocheGeometryCore.FlushRailWithBridge(curRail, 0.0, railsOut, 0.0, 1.0, 0.0,
                new List<Pt>(), stats, Opt(), null, null, loops);

            await Assert.That(railsOut.Count).IsEqualTo(1);
            await Assert.That(railsOut[0][2]).IsEqualTo(11.5).Within(0.01);
            await Assert.That(stats.BridgedEvidence).IsEqualTo(1);
        }

        [Test]
        public async Task GapWithoutEvidence_StaysSplit()
        {
            var curRail = new List<double[]> { new[] { 0.0, 0.0, 5.0, 1.0 }, new[] { 0.0, 8.5, 11.5, 1.0 } };
            var railsOut = new List<double[]>();
            var stats = new BridgeStatsCore();
            PocheGeometryCore.FlushRailWithBridge(curRail, 0.0, railsOut, 0.0, 1.0, 0.0,
                new List<Pt>(), stats, Opt(), null, null, null);

            await Assert.That(railsOut.Count).IsEqualTo(2);
            await Assert.That(stats.Unbridged).IsEqualTo(1);
        }

        [Test]
        public async Task DoorOpeningGap_CrossingOutsideGapStaysSplit()
        {
            // a perpendicular piece exists but crosses OUTSIDE the gap span
            var curRail = new List<double[]> { new[] { 0.0, 0.0, 5.0, 1.0 }, new[] { 0.0, 8.5, 11.5, 1.0 } };
            var pieces = new List<double[]>
            {
                new[] { 3.0, -2.0, 3.0, 2.0, 1.0, 0 }   // crosses at u=3 - inside the WALL, not the gap
            };
            var railsOut = new List<double[]>();
            var stats = new BridgeStatsCore();
            PocheGeometryCore.FlushRailWithBridge(curRail, 0.0, railsOut, 0.0, 1.0, 0.0,
                new List<Pt>(), stats, Opt(), null, pieces, null);

            await Assert.That(railsOut.Count).IsEqualTo(2);
            await Assert.That(stats.BridgedEvidence).IsEqualTo(0);
        }
    }
}

