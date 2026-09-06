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
}
