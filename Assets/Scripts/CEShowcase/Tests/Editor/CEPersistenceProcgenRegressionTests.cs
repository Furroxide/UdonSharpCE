using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using CESyncRuntimeTests;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using UdonSharp;
using UdonSharp.CE.Persistence;
using UdonSharp.CE.Procgen;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.Udon;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// Regression tests for CE.Procgen that run as plain .NET.
    ///
    /// Each test pins a defect from the Procgen audit that was not already pinned by
    /// CEPersistenceProcgenRegressionTests, or pins that a fix left the generated content unchanged. The
    /// defects that only exist in the Udon VM are in CEProcgenPersistenceUdonVmRegressionTests.
    /// </summary>
    public class CEProcgenRegressionTests
    {
        // -----------------------------------------------------------------------------------------
        // CERandom
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The original generator, written out with the plain casts it used. CERandom now widens its seed by
        /// hand because UdonSharp lowers '(ulong)seed' to Convert.ToUInt64, which throws for every negative
        /// seed; this is what "the same seed still gives the same world" is checked against.
        /// </summary>
        private sealed class OriginalGenerator
        {
            public ulong S0;
            public ulong S1;

            public OriginalGenerator(int seed)
            {
                ulong z = (ulong)seed + 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                S0 = z ^ (z >> 31);

                z = S0 + 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                S1 = z ^ (z >> 31);

                if (S0 == 0 && S1 == 0)
                    S0 = 1;
            }

            public ulong Next()
            {
                ulong s0 = S0;
                ulong s1 = S1;
                ulong result = s0 + s1;

                s1 ^= s0;
                S0 = ((s0 << 55) | (s0 >> 9)) ^ s1 ^ (s1 << 14);
                S1 = (s1 << 36) | (s1 >> 28);

                return result;
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(12345)]
        [TestCase(int.MaxValue)]
        [TestCase(-1)]
        [TestCase(-12345)]
        [TestCase(int.MinValue)]
        public void Random_EverySeed_KeepsTheSequenceTheOriginalCastGave(int seed)
        {
            var original = new OriginalGenerator(seed);
            var random = new CERandom(seed);

            random.GetState(out ulong s0, out ulong s1);
            Assert.That(s0, Is.EqualTo(original.S0), "The hand-widened seed no longer matches the sign-extending cast.");
            Assert.That(s1, Is.EqualTo(original.S1));

            for (int i = 0; i < 64; i++)
                Assert.That(random.NextInt(), Is.EqualTo((int)(original.Next() >> 33)), $"Draw {i} of seed {seed} changed.");
        }

        /// <summary>
        /// Spans up to 2^30 must keep the modulo reduction they always had, or every existing seed would lay
        /// out a different world after the wide-span fix.
        /// </summary>
        [TestCase(0, 10)]
        [TestCase(-50, 50)]
        [TestCase(5, 6)]
        [TestCase(0, 1 << 30)]
        [TestCase(-1000000000, 73741824)]
        public void Random_SpansUpTo2To30_KeepTheirOriginalSequence(int min, int maxExclusive)
        {
            var original = new OriginalGenerator(-77);
            var random = new CERandom(-77);

            for (int i = 0; i < 200; i++)
            {
                int expected = min + (int)(original.Next() >> 34) % (maxExclusive - min);
                Assert.That(random.Range(min, maxExclusive), Is.EqualTo(expected));
            }
        }

        /// <summary>
        /// A 30-bit draw reduced by modulo never reached the top of a span wider than 2^30:
        /// Range(0, int.MaxValue) stayed below 2^30 for ever.
        /// </summary>
        [Test]
        public void Random_SpansWiderThan2To30_ReachTheirUpperPartAndStayInBounds()
        {
            var random = new CERandom(42);
            int highest = int.MinValue;

            for (int i = 0; i < 20000; i++)
            {
                int value = random.Range(0, int.MaxValue);
                Assert.That(value, Is.InRange(0, int.MaxValue - 1));
                highest = Math.Max(highest, value);
            }

            Assert.That(highest, Is.GreaterThan(1 << 30));
        }

        /// <summary>
        /// max + 1 wrapped to int.MinValue, which Range reads as empty, so RangeInclusive(x, int.MaxValue)
        /// always returned x.
        /// </summary>
        [Test]
        public void Random_RangeInclusiveUpToIntMax_IsNotStuckOnItsMinimum()
        {
            var random = new CERandom(7);
            bool movedOffMinimum = false;
            bool reachedTop = false;

            for (int i = 0; i < 2000; i++)
            {
                int value = random.RangeInclusive(5, int.MaxValue);
                Assert.That(value, Is.GreaterThanOrEqualTo(5));
                movedOffMinimum |= value != 5;
            }

            for (int i = 0; i < 2000; i++)
            {
                int value = random.RangeInclusive(int.MaxValue - 1, int.MaxValue);
                Assert.That(value, Is.InRange(int.MaxValue - 1, int.MaxValue));
                reachedTop |= value == int.MaxValue;
            }

            Assert.That(movedOffMinimum, Is.True);
            Assert.That(reachedTop, Is.True);
        }

        [Test]
        public void Random_EmptyRange_ReturnsMinimumWithoutConsumingADraw()
        {
            var random = new CERandom(3);
            random.GetState(out ulong before0, out ulong before1);

            Assert.That(random.Range(7, 7), Is.EqualTo(7));
            Assert.That(random.Range(9, 3), Is.EqualTo(9));

            random.GetState(out ulong after0, out ulong after1);
            Assert.That(after0, Is.EqualTo(before0));
            Assert.That(after1, Is.EqualTo(before1));
        }

        // -----------------------------------------------------------------------------------------
        // CENoise
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The old floor was off by one on exact negative integers, so the noise read the wrong lattice
        /// column there and jumped -- exactly where grid sampling like x * 0.1f lands.
        /// </summary>
        [Test]
        public void Noise_ExactNegativeIntegerCoordinates_DoNotJump()
        {
            CENoise.Initialize(12345);

            foreach (float lattice in new[] { -1f, -2f, -3f, -17f, -256f, -257f })
            {
                AssertContinuous(x => CENoise.Perlin1D(x), lattice, "Perlin1D(x)");
                AssertContinuous(x => CENoise.Perlin2D(x, 0.37f), lattice, "Perlin2D(x, 0.37)");
                AssertContinuous(y => CENoise.Perlin2D(0.37f, y), lattice, "Perlin2D(0.37, y)");
                AssertContinuous(z => CENoise.Perlin3D(0.4f, 0.7f, z), lattice, "Perlin3D(0.4, 0.7, z)");
                AssertContinuous(x => CENoise.Fractal2D(x, 0.3f), lattice, "Fractal2D(x, 0.3)");
            }
        }

        private static void AssertContinuous(Func<float, float> noise, float at, string label)
        {
            float value = noise(at);
            Assert.That(Mathf.Abs(value - noise(at - 0.0001f)), Is.LessThan(0.01f), $"{label} jumps just below {at}.");
            Assert.That(Mathf.Abs(value - noise(at + 0.0001f)), Is.LessThan(0.01f), $"{label} jumps just above {at}.");
        }

        [Test]
        public void Noise_Simplex3D_EveryLatticeCellStaysInsideItsTables()
        {
            CENoise.Initialize(0);

            for (float x = -300f; x < 300f; x += 23.7f)
            {
                for (float y = -300f; y < 300f; y += 29.3f)
                {
                    for (float z = -300f; z < 300f; z += 31.1f)
                    {
                        float value = CENoise.Simplex3D(x, y, z);
                        Assert.That(value, Is.InRange(-1.5f, 1.5f), $"Simplex3D({x}, {y}, {z})");
                    }
                }
            }
        }

        /// <summary>No octave ran, maxValue stayed 0, and every fractal variant returned 0/0 = NaN.</summary>
        [Test]
        public void Noise_FractalVariantsWithNoOctaves_ReturnZeroNotNaN()
        {
            Assert.That(CENoise.Fractal2D(1.5f, 2.5f, 0), Is.EqualTo(0f));
            Assert.That(CENoise.Fractal3D(1.5f, 2.5f, 0.5f, -3), Is.EqualTo(0f));
            Assert.That(CENoise.Ridged2D(1.5f, 2.5f, 0), Is.EqualTo(0f));
            Assert.That(CENoise.Ridged3D(1.5f, 2.5f, 0.5f, 0), Is.EqualTo(0f));
            Assert.That(CENoise.Turbulence2D(1.5f, 2.5f, 0), Is.EqualTo(0f));
        }

        /// <summary>Worley hashed raw cell coordinates, so every seed produced the same cells.</summary>
        [Test]
        public void Noise_Worley3D_FollowsTheSeedAndRepeatsForIt()
        {
            int differing = 0;

            for (int i = 0; i < 50; i++)
            {
                float x = i * 0.37f - 9f, y = i * 0.61f - 15f, z = i * 0.23f;

                CENoise.Initialize(1);
                float first = CENoise.Worley3D(x, y, z);
                CENoise.Initialize(999);
                float second = CENoise.Worley3D(x, y, z);
                CENoise.Initialize(1);
                Assert.That(CENoise.Worley3D(x, y, z), Is.EqualTo(first), "The same seed must give the same cells.");

                if (first != second)
                    differing++;
            }

            Assert.That(differing, Is.GreaterThan(40));
        }

        // -----------------------------------------------------------------------------------------
        // WFCSolver
        // -----------------------------------------------------------------------------------------

        private static WFCTile Tile(int id, float weight, int[] eastWest, int[] northSouth)
        {
            return new WFCTile
            {
                Id = id,
                Weight = weight,
                ValidEast = eastWest,
                ValidWest = eastWest,
                ValidNorth = northSouth,
                ValidSouth = northSouth
            };
        }

        private static WFCTile[] MutuallyCompatible(int count, int firstId, float weight)
        {
            var ids = new int[count];
            for (int i = 0; i < count; i++)
                ids[i] = firstId + i;

            var tiles = new WFCTile[count];
            for (int i = 0; i < count; i++)
                tiles[i] = Tile(firstId + i, weight, ids, ids);

            return tiles;
        }

        private static void AssertLegal(WFCSolver solver, WFCTile[] tiles)
        {
            for (int x = 0; x < solver.Width; x++)
            {
                for (int y = 0; y < solver.Height; y++)
                {
                    Assert.That(solver.IsCollapsed(x, y), Is.True, $"({x}, {y}) is not collapsed.");
                    WFCTile tile = tiles.Single(t => t.Id == solver.GetTileAt(x, y));

                    if (x + 1 < solver.Width)
                        Assert.That(tile.ValidEast, Does.Contain(solver.GetTileAt(x + 1, y)), $"({x}, {y}) east");
                    if (y + 1 < solver.Height)
                        Assert.That(tile.ValidNorth, Does.Contain(solver.GetTileAt(x, y + 1)), $"({x}, {y}) north");
                }
            }
        }

        /// <summary>
        /// A collapsed cell was recognised by its tile id being non-negative, so a tile with a negative id
        /// was collapsed again on every iteration and the solve never finished.
        /// </summary>
        [Test]
        public void Wfc_NegativeTileIds_FinishWithinOneIterationPerCell()
        {
            WFCTile[] tiles = MutuallyCompatible(3, -2, 1f);
            var solver = new WFCSolver(new WFCConfig { Tiles = tiles, Width = 4, Height = 4, MaxIterationsPerFrame = 1 }, 3);

            for (int tick = 0; tick < 17 && solver.Status == WFCStatus.InProgress; tick++)
                solver.Tick();

            Assert.That(solver.Status, Is.EqualTo(WFCStatus.Completed));
            Assert.That(solver.CollapsedCount, Is.EqualTo(16));
            Assert.That(solver.Progress, Is.EqualTo(1f));
            AssertLegal(solver, tiles);
        }

        /// <summary>
        /// A cell whose only remaining tile weighs nothing was reported as a contradiction, failing grids
        /// that had a legal answer. A zero weight still means "never volunteer it".
        /// </summary>
        [Test]
        public void Wfc_ZeroWeightTileForcedByItsNeighbours_IsPlacedButNeverVolunteered()
        {
            WFCTile[] alternating =
            {
                Tile(0, 1f, new[] { 1 }, new[] { 0, 1 }),
                Tile(1, 0f, new[] { 0 }, new[] { 0, 1 }),
            };

            for (int seed = 0; seed < 20; seed++)
            {
                var solver = new WFCSolver(new WFCConfig { Tiles = alternating, Width = 6, Height = 3 }, seed);
                Assert.That(solver.Solve(), Is.EqualTo(WFCStatus.Completed), $"seed {seed}");
                AssertLegal(solver, alternating);
            }

            WFCTile[] free = MutuallyCompatible(2, 0, 1f);
            free[1].Weight = 0f;
            var freeSolver = new WFCSolver(new WFCConfig { Tiles = free, Width = 8, Height = 8 }, 9);
            Assert.That(freeSolver.Solve(), Is.EqualTo(WFCStatus.Completed));
            Assert.That(freeSolver.GetResult(), Has.No.Member(1));
        }

        /// <summary>
        /// Forcing a tile the constraints had already excluded overwrote the cell anyway and left the
        /// solver running, so the grid then completed with illegal neighbours.
        /// </summary>
        [Test]
        public void Wfc_SetTileTheConstraintsExclude_IsRefusedAndTheGridStaysLegal()
        {
            WFCTile[] tiles =
            {
                Tile(0, 1f, new[] { 0 }, new[] { 0, 1 }),
                Tile(1, 1f, new[] { 1 }, new[] { 0, 1 }),
            };

            var solver = new WFCSolver(new WFCConfig { Tiles = tiles, Width = 2, Height = 1 }, 3);

            Assert.That(solver.SetTile(0, 0, 0), Is.True);
            Assert.That(solver.SetTile(1, 0, 1), Is.False);
            Assert.That(solver.Status, Is.EqualTo(WFCStatus.InProgress));
            Assert.That(solver.CollapsedCount, Is.EqualTo(1));
            Assert.That(solver.GetTileAt(1, 0), Is.EqualTo(-1));

            Assert.That(solver.Solve(), Is.EqualTo(WFCStatus.Completed));
            AssertLegal(solver, tiles);
        }

        /// <summary>
        /// A forced tile whose propagation emptied a neighbour returned false but left the solver
        /// InProgress, as if nothing had happened.
        /// </summary>
        [Test]
        public void Wfc_SetTileThatEmptiesANeighbour_FailsTheSolver()
        {
            WFCTile[] tiles =
            {
                Tile(0, 1f, new int[0], new[] { 0, 1 }),
                Tile(1, 1f, new[] { 0, 1 }, new[] { 0, 1 }),
            };

            var solver = new WFCSolver(new WFCConfig { Tiles = tiles, Width = 2, Height = 1 }, 3);

            Assert.That(solver.SetTile(0, 0, 0), Is.False);
            Assert.That(solver.Status, Is.EqualTo(WFCStatus.Failed));
        }

        /// <summary>
        /// An unusable configuration left the grid unallocated, and every accessor then dereferenced it.
        /// </summary>
        [Test]
        public void Wfc_UnusableConfigurations_FailWithoutAnyAccessorThrowing()
        {
            var configurations = new[]
            {
                null,
                new WFCConfig { Tiles = null, Width = 2, Height = 2 },
                new WFCConfig { Tiles = new WFCTile[0], Width = 2, Height = 2 },
                new WFCConfig { Tiles = MutuallyCompatible(65, 0, 1f), Width = 2, Height = 2 },
                new WFCConfig { Tiles = new WFCTile[] { null }, Width = 2, Height = 2 },
                new WFCConfig { Tiles = MutuallyCompatible(2, 0, 1f), Width = -1, Height = 2 },
                new WFCConfig { Tiles = MutuallyCompatible(2, 0, 1f), Width = 70000, Height = 70000 },
            };

            foreach (WFCConfig config in configurations)
            {
                var solver = new WFCSolver(config, 1);

                Assert.That(solver.Status, Is.EqualTo(WFCStatus.Failed));
                Assert.That(solver.GetTileAt(0, 0), Is.EqualTo(-1));
                Assert.That(solver.GetResult(), Is.Empty);
                Assert.That(solver.GetEntropyAt(0, 0), Is.EqualTo(0));
                Assert.That(solver.GetPossibilitiesAt(0, 0), Is.Empty);
                Assert.That(solver.IsCollapsed(0, 0), Is.False);
                Assert.That(solver.SetTile(0, 0, 0), Is.False);
                Assert.That(solver.Tick(), Is.EqualTo(WFCStatus.Failed));
                Assert.That(solver.Solve(), Is.EqualTo(WFCStatus.Failed));
            }
        }

        /// <summary>A budget of zero or less ran no iteration, so Tick() never moved the solve forward.</summary>
        [Test]
        public void Wfc_NonPositiveIterationBudget_StillProgresses()
        {
            var solver = new WFCSolver(new WFCConfig { Tiles = MutuallyCompatible(2, 0, 1f), Width = 2, Height = 2, MaxIterationsPerFrame = 0 }, 1);

            for (int tick = 0; tick < 5 && solver.Status == WFCStatus.InProgress; tick++)
                solver.Tick();

            Assert.That(solver.Status, Is.EqualTo(WFCStatus.Completed));
        }

        [Test]
        public void Wfc_Reset_ReplaysTheSameGrid()
        {
            var solver = new WFCSolver(new WFCConfig { Tiles = MutuallyCompatible(5, 0, 1f), Width = 6, Height = 6 }, 77);
            solver.Solve();
            int[] first = solver.GetResult();

            solver.Reset();
            Assert.That(solver.CollapsedCount, Is.EqualTo(0));
            solver.Solve();

            Assert.That(solver.GetResult(), Is.EqualTo(first));
        }

        // -----------------------------------------------------------------------------------------
        // CEDungeon
        // -----------------------------------------------------------------------------------------

        /// <summary>Nothing ever set DungeonCorridor.IsOnCriticalPath, so it read false everywhere.</summary>
        [Test]
        public void Dungeon_CorridorsBetweenConsecutiveCriticalRooms_AreFlagged()
        {
            for (int seed = 0; seed < 30; seed++)
            {
                DungeonLayout layout = CEDungeon.Generate(new CERandom(seed), new DungeonConfig { RoomCount = 15 });
                int[] path = layout.CriticalPath;
                int flagged = 0;

                foreach (DungeonCorridor corridor in layout.Corridors)
                {
                    bool joinsConsecutive = false;
                    for (int step = 0; step + 1 < path.Length; step++)
                    {
                        joinsConsecutive |= (path[step] == corridor.FromRoom && path[step + 1] == corridor.ToRoom) ||
                                            (path[step] == corridor.ToRoom && path[step + 1] == corridor.FromRoom);
                    }

                    Assert.That(corridor.IsOnCriticalPath, Is.EqualTo(joinsConsecutive), $"seed {seed}, corridor {corridor.FromRoom}-{corridor.ToRoom}");

                    if (corridor.IsOnCriticalPath)
                        flagged++;
                }

                Assert.That(flagged, Is.EqualTo(path.Length - 1), $"seed {seed}");
            }
        }

        /// <summary>
        /// A room size the bounds could not hold was placed at the origin anyway and overhung them: 8x8 bounds
        /// produced an 11x9 room.
        /// </summary>
        [TestCase(8, 8)]
        [TestCase(5, 30)]
        [TestCase(1, 1)]
        [TestCase(13, 40)]
        public void Dungeon_RoomsLargerThanTheBounds_AreShrunkToFit(int boundsX, int boundsY)
        {
            for (int seed = 0; seed < 20; seed++)
            {
                DungeonLayout layout = CEDungeon.Generate(new CERandom(seed), new DungeonConfig { RoomCount = 6, MaxBounds = new Vector2Int(boundsX, boundsY) });

                Assert.That(layout.Rooms, Is.Not.Empty);
                foreach (DungeonRoom room in layout.Rooms)
                {
                    Assert.That(room.Position.x, Is.GreaterThanOrEqualTo(0));
                    Assert.That(room.Position.y, Is.GreaterThanOrEqualTo(0));
                    Assert.That(room.Max.x, Is.LessThanOrEqualTo(boundsX), $"seed {seed}: room {room.Position}+{room.Size}");
                    Assert.That(room.Max.y, Is.LessThanOrEqualTo(boundsY), $"seed {seed}: room {room.Position}+{room.Size}");
                    Assert.That(room.Size.x, Is.GreaterThanOrEqualTo(1));
                    Assert.That(room.Size.y, Is.GreaterThanOrEqualTo(1));
                }
            }
        }

        [Test]
        public void Dungeon_EmptyBounds_GiveAnEmptyLayout()
        {
            DungeonLayout layout = CEDungeon.Generate(new CERandom(1), new DungeonConfig { MaxBounds = new Vector2Int(0, 10) });

            Assert.That(layout.Rooms, Is.Empty);
            Assert.That(layout.Corridors, Is.Empty);
            Assert.That(layout.CriticalPath, Is.Empty);
            Assert.That(layout.StartRoom, Is.Null);
        }

        /// <summary>The lone room of a minimal layout is its whole critical path but was never flagged so.</summary>
        [Test]
        public void Dungeon_SingleRoomLayout_IsItsOwnCriticalPath()
        {
            DungeonLayout layout = CEDungeon.Generate(new CERandom(7), new DungeonConfig { RoomCount = 5, MaxBounds = new Vector2Int(8, 8) });

            Assert.That(layout.Rooms, Has.Length.EqualTo(1));
            DungeonRoom room = layout.Rooms[0];
            Assert.That(room.IsOnCriticalPath, Is.True);
            Assert.That(room.Type, Is.EqualTo(RoomType.Start));
            Assert.That(layout.CriticalPath, Is.EqualTo(new[] { 0 }));
            Assert.That(layout.BoundsMin, Is.EqualTo(room.Position));
            Assert.That(layout.BoundsSize, Is.EqualTo(room.Size));
        }

        [Test]
        public void Dungeon_SameSeed_GivesTheSameLayout()
        {
            DungeonLayout first = CEDungeon.Generate(new CERandom(99), new DungeonConfig { RoomCount = 20 });
            DungeonLayout second = CEDungeon.Generate(new CERandom(99), new DungeonConfig { RoomCount = 20 });

            Assert.That(CEProcgenPersistenceVmTestBehaviour.Summarize(second), Is.EqualTo(CEProcgenPersistenceVmTestBehaviour.Summarize(first)));
        }
    }

    /// <summary>
    /// Regression tests for CE.Persistence: the size estimator, and the code the persistence generator
    /// emits, compiled and run as .NET.
    /// </summary>
    public class CEPersistenceRegressionTests
    {
        // -----------------------------------------------------------------------------------------
        // SizeEstimator
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// VRCJson refuses a dictionary with a non-string key, so such an entry can never be saved -- but the
        /// estimator read .String off the key and threw.
        /// </summary>
        [Test]
        public void SizeEstimator_DictionaryWithANonStringKey_IsEstimatedWithoutThrowing()
        {
            var withOddKey = new DataDictionary();
            withOddKey[5] = "five";
            withOddKey["s"] = "x";

            var stringKeysOnly = new DataDictionary();
            stringKeysOnly["s"] = "x";

            int estimate = 0;
            Assert.DoesNotThrow(() => estimate = SizeEstimator.EstimateDataDictionarySize(withOddKey));
            Assert.That(estimate, Is.GreaterThanOrEqualTo(SizeEstimator.EstimateDataDictionarySize(stringKeysOnly)));
        }

        /// <summary>
        /// VRCJson writes non-ASCII text out raw, as UTF-8, and the estimator counted UTF-16 characters: CJK text
        /// cost three times what it was estimated at, so a save called safe could overrun the quota.
        /// </summary>
        [Test]
        public void SizeEstimator_NonAsciiText_IsNeverUnderTheBytesVRCJsonWrites()
        {
            var data = new DataDictionary();
            data["name"] = new string('日', 40) + "été";
            data["plain"] = "ascii";

            Assert.That(VRCJson.TrySerializeToJson(data, JsonExportType.Minify, out DataToken json), Is.True);
            int written = Encoding.UTF8.GetByteCount(json.String);

            Assert.That(SizeEstimator.EstimateDataDictionarySize(data), Is.GreaterThanOrEqualTo(written));
        }

        // -----------------------------------------------------------------------------------------
        // Generated converters
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// 64-bit fields are stored as invariant strings, but a save written before that holds a plain JSON
        /// number; it still has to load whenever it is representable.
        /// </summary>
        [Test]
        public void Converters_SixtyFourBitFields_RoundTripAndStillAcceptOldNumericSaves()
        {
            Assert.That(GeneratorFixture.Run(@"using UdonSharp.CE.Persistence;
                [PlayerData(""wide"")] public class Model {
                    [PersistKey(""l"")] public long l;
                    [PersistKey(""u"")] public ulong u;
                    [PersistKey(""ls"")] public long[] ls;
                    [PersistKey(""us"")] public ulong[] us;
                }
                public static class AuditEntry { public static string Run() {
                    var model = new Model { l = long.MaxValue, u = ulong.MaxValue, ls = new[] { long.MinValue, 7L }, us = new[] { 0UL, ulong.MaxValue } };
                    string json = CEPersistence.ToJson(model);
                    if (!json.Contains(""\""9223372036854775807\"""")) return ""long not written as a string: "" + json;
                    if (CEPersistence.FromJson(json, out Model back) != RestoreResult.Success) return ""round trip failed"";
                    if (back.l != long.MaxValue || back.u != ulong.MaxValue || back.ls[0] != long.MinValue || back.ls[1] != 7 || back.us[1] != ulong.MaxValue) return ""values changed"";
                    if (CEPersistence.FromJson(""{\""l\"":12345,\""u\"":42,\""ls\"":[1,\""2\"",-3]}"", out Model old) != RestoreResult.Success) return ""old numeric save rejected"";
                    if (old.l != 12345 || old.u != 42 || old.ls[1] != 2 || old.ls[2] != -3) return ""old numeric save misread"";
                    if (CEPersistence.FromJson(""{\""l\"":1e30}"", out Model _) != RestoreResult.ParseError) return ""1e30 accepted into a long"";
                    if (CEPersistence.FromJson(""{\""u\"":-1}"", out Model _) != RestoreResult.ParseError) return ""-1 accepted into a ulong"";
                    if (CEPersistence.FromJson(""{\""l\"":\""12a\""}"", out Model _) != RestoreResult.ParseError) return ""malformed string accepted"";
                    if (CEPersistence.FromJson(""{\""l\"":1.5}"", out Model _) != RestoreResult.ParseError) return ""fraction accepted into a long"";
                    return ""ok"";
                } }"), Is.EqualTo("ok"));
        }

        /// <summary>
        /// Null strings, null arrays, null elements and every primitive width round-trip; a stored value of
        /// the wrong type is a ParseError rather than an exception that halts the behaviour.
        /// </summary>
        [Test]
        public void Converters_EveryFieldKind_RoundTripsNullsAndRejectsMismatchedTokens()
        {
            Assert.That(GeneratorFixture.Run(@"using UdonSharp.CE.Persistence; using VRC.SDK3.Data;
                [PlayerData(""kinds"")] public class Model {
                    [PersistKey(""s"")] public string s;
                    [PersistKey(""ss"")] public string[] ss;
                    [PersistKey(""none"")] public int[] none;
                    [PersistKey(""list"")] public DataList list;
                    [PersistKey(""dict"")] public DataDictionary dict;
                    [PersistKey(""b"")] public bool b;
                    [PersistKey(""by"")] public byte by;
                    [PersistKey(""sb"")] public sbyte sb;
                    [PersistKey(""sh"")] public short sh;
                    [PersistKey(""us"")] public ushort us;
                    [PersistKey(""ui"")] public uint ui;
                    [PersistKey(""f"")] public float f;
                    [PersistKey(""d"")] public double d;
                }
                public static class AuditEntry { public static string Run() {
                    var list = new DataList(); list.Add(1);
                    var dict = new DataDictionary(); dict[""k""] = ""v"";
                    var model = new Model { s = null, ss = new[] { ""a"", null }, none = null, list = list, dict = dict, b = true,
                        by = 255, sb = -128, sh = -32768, us = 65535, ui = 4294967295, f = 0.1f, d = 1.0 / 3.0 };
                    string json = CEPersistence.ToJson(model);
                    if (json.Contains(""\""s\"""") || json.Contains(""\""none\"""")) return ""null reference written: "" + json;
                    if (CEPersistence.FromJson(json, out Model back) != RestoreResult.Success) return ""round trip failed: "" + json;
                    if (back.s != null || back.ss.Length != 2 || back.ss[0] != ""a"" || back.ss[1] != null || back.none != null) return ""references changed"";
                    if (back.list.Count != 1 || back.dict[""k""].String != ""v"" || !back.b) return ""containers changed"";
                    if (back.by != 255 || back.sb != -128 || back.sh != -32768 || back.us != 65535 || back.ui != 4294967295 || back.f != 0.1f || System.Math.Abs(back.d - 1.0 / 3.0) > 1e-12) return ""numbers changed"";
                    if (CEPersistence.FromJson(""{\""s\"":null,\""list\"":null}"", out Model nulls) != RestoreResult.Success || nulls.s != null || nulls.list != null) return ""explicit null rejected"";
                    string[] corrupt = { ""{\""s\"":5}"", ""{\""b\"":1}"", ""{\""by\"":256}"", ""{\""sb\"":1.5}"", ""{\""ui\"":-1}"", ""{\""f\"":1e300}"", ""{\""ss\"":[1]}"", ""{\""list\"":{}}"", ""{\""none\"":\""x\""}"" };
                    foreach (string text in corrupt)
                        if (CEPersistence.FromJson(text, out Model bad) != RestoreResult.ParseError || bad != null) return ""accepted "" + text;
                    return ""ok"";
                } }"), Is.EqualTo("ok"));
        }

        /// <summary>
        /// A [PlayerData] class with no access modifier is internal, and a public overload taking an internal
        /// type is CS0051 -- in a generated file shared by every model, so the whole project failed to compile.
        /// </summary>
        [Test]
        public void Generator_InternalModel_GetsOverloadsThatCompile()
        {
            Assert.That(GeneratorFixture.Run(@"using UdonSharp.CE.Persistence;
                [PlayerData(""hidden"")] class Hidden { [PersistKey(""n"")] public int n; }
                public static class AuditEntry { public static string Run() {
                    string json = CEPersistence.ToJson(new Hidden { n = 4 });
                    return CEPersistence.FromJson(json, out Hidden back) == RestoreResult.Success && back.n == 4 ? ""ok"" : json;
                } }"), Is.EqualTo("ok"));
        }

        /// <summary>
        /// A model the generated overloads cannot reach or construct produced a generated file that failed
        /// the whole project's compile. It is rejected at the declaration instead.
        /// </summary>
        [TestCase("public class Outer { [PlayerData(\"m\")] private class Model { [PersistKey(\"n\")] public int n; } }")]
        [TestCase("public class Outer { [PlayerData(\"m\")] protected class Model { [PersistKey(\"n\")] public int n; } }")]
        [TestCase("public class Wrapper { private class Outer { [PlayerData(\"m\")] public class Model { [PersistKey(\"n\")] public int n; } } }")]
        [TestCase("public struct Outer { [PlayerData(\"m\")] public class Model { [PersistKey(\"n\")] public int n; } }")]
        [TestCase("[PlayerData(\"m\")] public abstract class Model { [PersistKey(\"n\")] public int n; }")]
        [TestCase("[PlayerData(\"m\")] public class Model { [PersistKey(\"n\")] public int n; public Model(int n) { this.n = n; } }")]
        [TestCase("[PlayerData(\"m\")] public class Model { [PersistKey(\"n\")] public int n; private Model() { } }")]
        public void Generator_ModelItsOverloadsCannotReachOrConstruct_IsRejected(string declaration)
        {
            Assert.Throws<CodeGenerationException>(() => GeneratorFixture.Generate("using UdonSharp.CE.Persistence; " + declaration));
        }

        /// <summary>
        /// "R" prints an integral Range bound without a decimal point, and a bare negative int cannot be
        /// compared with a ulong (CS0034), so Range(-1, 10) on a ulong field failed the compile.
        /// </summary>
        [Test]
        public void Generator_UlongFieldWithANegativeRangeBound_Compiles()
        {
            Assert.That(GeneratorFixture.Run(@"using UdonSharp.CE.Persistence;
                [PlayerData(""bounded"")] public class Model { [PersistKey(""u""), Range(-1, 10)] public ulong u; }
                public static class AuditEntry { public static string Run() {
                    bool inside = CEPersistence.Validate(new Model { u = 10 }, out var noErrors);
                    bool outside = CEPersistence.Validate(new Model { u = 11 }, out var errors);
                    return inside && !outside && errors.Count == 1 ? ""ok"" : ""bounds misapplied"";
                } }"), Is.EqualTo("ok"));
        }

        /// <summary>
        /// Constant arguments the old generator could not read -- a parenthesised or signed literal, a const
        /// reference -- were dropped without a word, so the Range never applied.
        /// </summary>
        [Test]
        public void Generator_ConstantExpressionArguments_AreApplied()
        {
            Assert.That(GeneratorFixture.Run(@"using UdonSharp.CE.Persistence;
                public static class Limits { public const int Max = 10; public const int Version = 4; }
                [PlayerData(""limits"", Version = Limits.Version)] public class Model { [PersistKey(""n""), Range(-(5), Limits.Max)] public int n; }
                public static class AuditEntry { public static string Run() {
                    if (!CEPersistence.Validate(new Model { n = -5 }, out var a) || !CEPersistence.Validate(new Model { n = 10 }, out var b)) return ""bounds too tight"";
                    if (CEPersistence.Validate(new Model { n = -6 }, out var c) || CEPersistence.Validate(new Model { n = 11 }, out var d)) return ""bounds not applied"";
                    var data = CEPersistence.ToData(new Model { n = 1 });
                    return data[""__ce_version""].Number == 4 ? ""ok"" : ""version not applied"";
                } }"), Is.EqualTo("ok"));
        }

        /// <summary>
        /// An argument the generator cannot evaluate -- here because it does not bind -- has to fail the
        /// compile naming it, not be dropped and leave the field unvalidated.
        /// </summary>
        [Test]
        public void Generator_ArgumentItCannotEvaluate_IsRejectedNamingIt()
        {
            var failure = Assert.Throws<CodeGenerationException>(() => GeneratorFixture.Generate(@"using UdonSharp.CE.Persistence;
                [PlayerData(""m"")] public class Model { [PersistKey(""n""), Range(0, Undeclared.Max)] public int n; }"));

            Assert.That(failure.Message, Does.Contain("Undeclared.Max"));
        }

        /// <summary>
        /// DefaultValue is typed object, so a null default on a value type, a value out of the field's range
        /// or of the wrong type reached the generated file as code that did not compile.
        /// </summary>
        [Test]
        public void Generator_DefaultValues_AreCheckedAgainstTheFieldType()
        {
            Assert.That(GeneratorFixture.Run(@"using UdonSharp.CE.Persistence;
                [PlayerData(""defaults"")] public class Model {
                    [PersistKey(""n"", Optional = true, DefaultValue = null)] public int n = 7;
                    [PersistKey(""whole"", Optional = true, DefaultValue = 2.0)] public int whole;
                    [PersistKey(""f"", Optional = true, DefaultValue = 1.1f)] public float f;
                    [PersistKey(""big"", Optional = true, DefaultValue = -9223372036854775808)] public long big;
                    [PersistKey(""s"", Optional = true, DefaultValue = ""fallback"")] public string s;
                    [PersistKey(""flag"", Optional = true, DefaultValue = true)] public bool flag;
                }
                public static class AuditEntry { public static string Run() {
                    if (CEPersistence.FromJson(""{}"", out Model m) != RestoreResult.Success) return ""restore failed"";
                    return m.n == 0 && m.whole == 2 && m.f == 1.1f && m.big == long.MinValue && m.s == ""fallback"" && m.flag ? ""ok"" : ""defaults misapplied"";
                } }"), Is.EqualTo("ok"));
        }

        [TestCase("[PersistKey(\"n\", Optional = true, DefaultValue = 300)] public byte n;")]
        [TestCase("[PersistKey(\"n\", Optional = true, DefaultValue = 1.5)] public int n;")]
        [TestCase("[PersistKey(\"n\", Optional = true, DefaultValue = \"x\")] public int n;")]
        [TestCase("[PersistKey(\"n\", Optional = true, DefaultValue = 1)] public bool n;")]
        [TestCase("[PersistKey(\"n\", Optional = true, DefaultValue = 5)] public string n;")]
        [TestCase("[PersistKey(\"n\", Optional = true, DefaultValue = -1)] public ulong n;")]
        public void Generator_DefaultValueTheFieldCannotHold_IsRejected(string field)
        {
            Assert.Throws<CodeGenerationException>(() => GeneratorFixture.Generate(
                "using UdonSharp.CE.Persistence; [PlayerData(\"m\")] public class Model { " + field + " }"));
        }

        /// <summary>
        /// Jagged and multidimensional arrays were classified as supported and generated
        /// list.Add(model.jagged[i]) and new int[][n], which do not compile. The model is now skipped.
        /// </summary>
        [TestCase("[PersistKey(\"j\")] public int[][] j;")]
        [TestCase("[PersistKey(\"g\")] public int[,] g;")]
        public void Generator_JaggedOrMultidimensionalArrayField_DoesNotBreakTheCompile(string field)
        {
            SyntaxTree[] trees = GeneratorFixture.Generate(
                "using UdonSharp.CE.Persistence; [PlayerData(\"arrays\")] public class Model { " + field + " }");

            GeneratorFixture.Compile(trees);
        }

        /// <summary>
        /// Only the literal 'private' keyword used to be rejected, so an implicitly private, protected or
        /// readonly [PersistKey] field generated code that read or assigned it from outside the class.
        /// </summary>
        [TestCase("[PersistKey(\"n\")] int n;")]
        [TestCase("[PersistKey(\"n\")] protected int n;")]
        [TestCase("[PersistKey(\"n\")] public readonly int n = 1;")]
        public void Generator_FieldItsConverterCannotReachOrAssign_DoesNotBreakTheCompile(string field)
        {
            SyntaxTree[] trees = GeneratorFixture.Generate(
                "using UdonSharp.CE.Persistence; [PlayerData(\"fields\")] public class Model { " + field + " }");

            GeneratorFixture.Compile(trees);
        }

        /// <summary>
        /// A model in Data.Saves was emitted unqualified inside namespace UdonSharp.CE.Persistence, where
        /// "Data" binds to UdonSharp.CE.Data; a model in nested namespace blocks recorded only the innermost
        /// name. Both generated references that did not compile.
        /// </summary>
        [Test]
        public void Generator_ModelsInShadowedOrNestedNamespaces_Compile()
        {
            Assert.That(GeneratorFixture.Run(@"using UdonSharp.CE.Persistence;
                namespace Data.Saves { [PlayerData(""profile"")] public class Profile { [PersistKey(""n"")] public int n; } }
                namespace Outer { namespace Inner { [PlayerData(""nested"")] public class Slot { [PersistKey(""n"")] public int n; } } }
                public static class AuditEntry { public static string Run() {
                    bool profile = CEPersistence.FromJson(CEPersistence.ToJson(new Data.Saves.Profile { n = 1 }), out Data.Saves.Profile p) == RestoreResult.Success && p.n == 1;
                    bool slot = CEPersistence.FromJson(CEPersistence.ToJson(new Outer.Inner.Slot { n = 2 }), out Outer.Inner.Slot s) == RestoreResult.Success && s.n == 2;
                    return profile && slot ? ""ok"" : ""round trip failed"";
                } }"), Is.EqualTo("ok"));
        }

        /// <summary>
        /// A call site inside nested namespace blocks could not choose between two models sharing a short
        /// name: its scope held "Inner" but not "Outer.Inner", so the call was left on the generic overload.
        /// </summary>
        [Test]
        public void Generator_CallInsideNestedNamespaceBlocks_ResolvesTheModelInScope()
        {
            const string source = @"using UdonSharp.CE.Persistence;
                namespace Outer { namespace Inner {
                    [PlayerData(""inner_save"")] public class Save { [PersistKey(""n"")] public int n; }
                    public static class Probe { public static bool Run() {
                        string json = CEPersistence.ToJson<Save>(new Save { n = 3 });
                        return CEPersistence.FromJson<Save>(json, out var back) == RestoreResult.Success && back.n == 3;
                    } }
                } }
                namespace Elsewhere { [PlayerData(""elsewhere_save"")] public class Save { [PersistKey(""n"")] public int n; } }
                public static class AuditEntry { public static string Run() { return Outer.Inner.Probe.Run() ? ""ok"" : ""generic overload used""; } }";

            SyntaxTree[] trees = GeneratorFixture.Generate(source);
            Assert.That(trees[0].ToString(), Does.Not.Contain("ToJson<Save>").And.Not.Contain("FromJson<Save>"));
            Assert.That(GeneratorFixture.Run(trees), Is.EqualTo("ok"));
        }

        /// <summary>Two models under one key overwrite each other's save, and the stored model key cannot tell.</summary>
        [Test]
        public void Generator_DuplicateModelKey_IsRejectedNamingBothModels()
        {
            var failure = Assert.Throws<CodeGenerationException>(() => GeneratorFixture.Generate(@"using UdonSharp.CE.Persistence;
                [PlayerData(""shared"")] public class First { [PersistKey(""n"")] public int n; }
                [PlayerData(""shared"")] public class Second { [PersistKey(""n"")] public int n; }"));

            Assert.That(failure.Message, Does.Contain("First").And.Contain("Second"));
        }

        /// <summary>Fields declared on a base class were silently left out of the save.</summary>
        [Test]
        public void Generator_InheritedPersistKeyField_IsRejected()
        {
            Assert.Throws<CodeGenerationException>(() => GeneratorFixture.Generate(@"using UdonSharp.CE.Persistence;
                public class BaseSave { [PersistKey(""hp"")] public int hp; }
                [PlayerData(""derived"")] public class DerivedSave : BaseSave { [PersistKey(""xp"")] public int xp; }"));
        }

        /// <summary>
        /// A private DataDictionary beside the public raw payload field is the model's own state; it must not
        /// stop the model generating. A raw payload the converter cannot reach is still rejected.
        /// </summary>
        [Test]
        public void Generator_PrivateScratchDictionaryBesideTheRawField_IsAccepted()
        {
            Assert.That(GeneratorFixture.Run(@"using UdonSharp.CE.Persistence; using VRC.SDK3.Data;
                [PlayerData(""raw"")] public class RawModel { private DataDictionary scratch; public DataDictionary data; }
                public static class AuditEntry { public static string Run() {
                    var model = new RawModel { data = new DataDictionary() }; model.data[""k""] = 5;
                    return CEPersistence.FromJson(CEPersistence.ToJson(model), out RawModel back) == RestoreResult.Success && back.data[""k""].Number == 5 ? ""ok"" : ""raw round trip failed"";
                } }"), Is.EqualTo("ok"));

            Assert.Throws<CodeGenerationException>(() => GeneratorFixture.Generate(@"using UdonSharp.CE.Persistence; using VRC.SDK3.Data;
                [PlayerData(""unreachable"")] public class Hidden { private DataDictionary data; }"));
        }

        /// <summary>
        /// Runs the persistence generator over a fixture plus the CE runtime sources, then compiles and runs
        /// the result as .NET. Kept local rather than shared so this file compiles on its own.
        /// </summary>
        private static class GeneratorFixture
        {
            private const string RuntimeRoot = "Packages/com.merlin.UdonSharp/Runtime/Libraries/CE";

            public static SyntaxTree[] Generate(string fixture)
            {
                var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp9);
                var input = new List<(SyntaxTree tree, string filePath)>
                {
                    (CSharpSyntaxTree.ParseText(fixture, options, "PersistenceFixture.cs"), "PersistenceFixture.cs")
                };

                foreach (string path in Directory.GetFiles(RuntimeRoot, "*.cs", SearchOption.AllDirectories))
                    input.Add((CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path), path));

                Type generator = AppDomain.CurrentDomain.GetAssemblies()
                    .Single(assembly => assembly.GetName().Name == "UdonSharp.CE.Editor")
                    .GetType("UdonSharp.CE.Editor.Persistence.CEPersistenceAutoRegistrationOptimizer", true);
                MethodInfo generate = generator.GetMethod("Generate", BindingFlags.Static | BindingFlags.NonPublic);

                try
                {
                    var output = ((SyntaxTree tree, string filePath)[])generate.Invoke(null, new object[] { input.ToArray() });
                    return output.Select(pair => pair.tree).ToArray();
                }
                catch (TargetInvocationException failure) when (failure.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(failure.InnerException).Throw();
                    throw;
                }
            }

            public static CSharpCompilation Compile(SyntaxTree[] trees)
            {
                var references = new List<MetadataReference>(CompilationContext.GetMetadataReferences());

                foreach (string name in new[] { "UdonSharp.Runtime", "UdonSharp.Lib" })
                {
                    Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().Single(candidate => candidate.GetName().Name == name);
                    if (!references.OfType<PortableExecutableReference>().Any(reference => reference.FilePath == assembly.Location))
                        references.Add(MetadataReference.CreateFromFile(assembly.Location));
                }

                // The CE runtime is compiled from source alongside the fixture, so its built copy must not
                // also be referenced.
                references.RemoveAll(reference => reference is PortableExecutableReference executable &&
                    Path.GetFileNameWithoutExtension(executable.FilePath) == "UdonSharp.CE.Runtime");

                var compilation = CSharpCompilation.Create("CEPersistenceRegression_" + Guid.NewGuid().ToString("N"), trees, references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
                Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(diagnostic => diagnostic.ToString())));
                return compilation;
            }

            public static object Run(string fixture) => Run(Generate(fixture));

            public static object Run(SyntaxTree[] trees)
            {
                CSharpCompilation compilation = Compile(trees);

                using (var stream = new MemoryStream())
                {
                    var emitted = compilation.Emit(stream);
                    Assert.That(emitted.Success, Is.True, string.Join("\n", emitted.Diagnostics));

                    Assembly assembly = Assembly.Load(stream.ToArray());
                    MethodInfo run = assembly.GetType("AuditEntry").GetMethod("Run");

                    try
                    {
                        return run.Invoke(null, null);
                    }
                    catch (TargetInvocationException failure) when (failure.InnerException != null)
                    {
                        ExceptionDispatchInfo.Capture(failure.InnerException).Throw();
                        throw;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Procgen and Persistence executed by the Udon VM, compared with the same calls executed by .NET.
    ///
    /// Every defect pinned here is invisible to .NET: UdonSharp lowers each numeric cast to System.Convert,
    /// which throws where C# wraps, and the throw halts the behaviour -- a negative seed, a clock-seeded
    /// generator, a noise coordinate past int range, a float.MaxValue size estimate and a NaN dungeon
    /// connectivity all did. CEDungeon and WFCSolver used multidimensional arrays and could not be compiled
    /// to Udon at all. Matching the .NET results also pins that the same seed builds the same world on every
    /// client whatever runs it.
    ///
    /// One test covers everything because each run costs two whole-project Udon compiles.
    /// </summary>
    public class CEProcgenPersistenceUdonVmRegressionTests
    {
        private readonly List<UnityEngine.Object> _createdObjects = new List<UnityEngine.Object>();
        private readonly List<string> _createdAssetPaths = new List<string>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                if (_createdObjects[i] != null)
                    UnityEngine.Object.DestroyImmediate(_createdObjects[i]);
            }

            _createdObjects.Clear();

            if (_createdAssetPaths.Count == 0)
                return;

            foreach (string assetPath in _createdAssetPaths)
            {
                // The compiled program is a separate asset under Assets/SerializedUdonPrograms that deleting
                // the program asset leaves behind; its path has to be read before the program asset goes.
                var programAsset = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath);
                string serializedProgramPath = programAsset != null && programAsset.SerializedProgramAsset != null
                    ? AssetDatabase.GetAssetPath(programAsset.SerializedProgramAsset)
                    : null;

                AssetDatabase.DeleteAsset(assetPath);

                if (!string.IsNullOrEmpty(serializedProgramPath))
                    AssetDatabase.DeleteAsset(serializedProgramPath);
            }

            _createdAssetPaths.Clear();
            AssetDatabase.Refresh();

            // Leave the project compiled without the fixture, the way the next test expects to find it.
            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
        }

        [Test]
        public void ProcgenAndPersistence_RunInTheUdonVm_ExactlyAsInDotNet()
        {
            UdonSharpProgramAsset asset = CreateProgramAssetForFixture();
            CompileAll();

            using (new UdonSyncHarness.RuntimeBootstrap())
            using (var pair = UdonSyncHarness.CreatePair(asset))
            {
                UdonBehaviour vm = pair.Owner;

                vm.SendCustomEvent(nameof(CEProcgenPersistenceVmTestBehaviour.RunRandom));
                AssertRan(vm, "randomRan", "CERandom");
                Assert.That(vm.GetProgramVariable("negativeSeedDraws"), Is.EqualTo(Draws(new CERandom(-12345), r => r.NextInt(), 8)));
                Assert.That(vm.GetProgramVariable("lowestSeedDraws"), Is.EqualTo(Draws(new CERandom(int.MinValue), r => r.Range(-50, 50), 8)));
                Assert.That(vm.GetProgramVariable("wideRangeDraws"), Is.EqualTo(Draws(new CERandom(99), r => r.Range(int.MinValue, int.MaxValue), 16)));
                Assert.That(vm.GetProgramVariable("inclusiveTopDraws"), Is.EqualTo(Draws(new CERandom(7), r => r.RangeInclusive(5, int.MaxValue), 8)));
                Assert.That(vm.GetProgramVariable("clockSeededDistinctDraws"), Is.GreaterThan(1),
                    "new CERandom() is stuck in the all-zero state, so every draw is 0.");

                vm.SendCustomEvent(nameof(CEProcgenPersistenceVmTestBehaviour.RunNoise));
                AssertRan(vm, "noiseRan", "CENoise");
                CENoise.Initialize(-7);
                var expectedSimplex = new float[64];
                int n = 0;
                for (int x = 0; x < 4; x++)
                    for (int y = 0; y < 4; y++)
                        for (int z = 0; z < 4; z++)
                            expectedSimplex[n++] = CENoise.Simplex3D(x * 61.7f - 100f, y * 53.3f - 100f, z * 47.9f - 100f);
                // Udon rounds every float operation to float through its extern, while Mono's JIT may keep
                // intermediates wider; at coordinates near +/-100 that alone moves a sample by about 1e-5.
                // An indexing defect -- the bug this pins -- moves it by a large fraction of the [-1, 1] range.
                Assert.That(vm.GetProgramVariable("simplexSamples"), Is.EqualTo(expectedSimplex).Within(1e-3f));
                Assert.That(vm.GetProgramVariable("latticeSamples"), Is.EqualTo(new[]
                {
                    CENoise.Perlin2D(-2f, 0.37f), CENoise.Perlin2D(-1.9999f, 0.37f), CENoise.Perlin1D(-17f), CENoise.Perlin3D(0.4f, -3f, 0.7f)
                }).Within(1e-5f));
                Assert.That((float)vm.GetProgramVariable("farCoordinateSample"), Is.EqualTo(CENoise.Perlin2D(3.0e9f, 0.5f)).Within(1e-5f));
                Assert.That(vm.GetProgramVariable("noOctaveSample"), Is.EqualTo(0f));
                Assert.That(vm.GetProgramVariable("worleySeedOne"), Is.Not.EqualTo(vm.GetProgramVariable("worleySeedTwo")));

                vm.SendCustomEvent(nameof(CEProcgenPersistenceVmTestBehaviour.RunDungeon));
                AssertRan(vm, "dungeonRan", "CEDungeon");
                Assert.That(vm.GetProgramVariable("dungeonSummary"),
                    Is.EqualTo(CEProcgenPersistenceVmTestBehaviour.Summarize(CEDungeon.Generate(new CERandom(2024), new DungeonConfig()))));
                Assert.That(vm.GetProgramVariable("hostileConnectivityRooms"), Is.EqualTo(8));

                vm.SendCustomEvent(nameof(CEProcgenPersistenceVmTestBehaviour.RunWfc));
                AssertRan(vm, "wfcRan", "WFCSolver");
                var expectedSolver = new WFCSolver(new WFCConfig
                {
                    Tiles = new[]
                    {
                        CEProcgenPersistenceVmTestBehaviour.MakeTile(-1, 1f, new[] { -1, 4 }),
                        CEProcgenPersistenceVmTestBehaviour.MakeTile(4, 2f, new[] { -1, 4, 9 }),
                        CEProcgenPersistenceVmTestBehaviour.MakeTile(9, 0f, new[] { 4, 9 }),
                    },
                    Width = 6,
                    Height = 5,
                }, 31);
                Assert.That(expectedSolver.Solve(), Is.EqualTo(WFCStatus.Completed));
                Assert.That(vm.GetProgramVariable("wfcStatus"), Is.EqualTo((int)WFCStatus.Completed));
                Assert.That(vm.GetProgramVariable("wfcGrid"), Is.EqualTo(expectedSolver.GetResult()));

                vm.SendCustomEvent(nameof(CEProcgenPersistenceVmTestBehaviour.RunSizeEstimator));
                AssertRan(vm, "estimatorRan", "SizeEstimator");
                var inner = new DataDictionary();
                inner["depth"] = 2;
                var middle = new DataList();
                middle.Add(inner);
                middle.Add("text");
                var outer = new DataDictionary();
                outer["list"] = middle;
                outer["n"] = 12.5;
                var oddKeys = new DataDictionary();
                oddKeys[5] = "five";
                oddKeys["s"] = "x";
                Assert.That(vm.GetProgramVariable("estimates"), Is.EqualTo(new[]
                {
                    SizeEstimator.EstimateFloatSize(float.MaxValue),
                    SizeEstimator.EstimateDoubleSize(double.MaxValue),
                    SizeEstimator.EstimateDoubleSize(-1e300),
                    SizeEstimator.EstimateDataDictionarySize(outer),
                    SizeEstimator.EstimateDataDictionarySize(oddKeys),
                    SizeEstimator.EstimateStringSize("日本語"),
                }));

                vm.SendCustomEvent(nameof(CEProcgenPersistenceVmTestBehaviour.RunPersistence));
                AssertRan(vm, "persistenceRan", "the generated persistence converters");
                Assert.That(vm.GetProgramVariable("persistenceRestored"), Is.EqualTo(true), "JSON written by the VM: " + vm.GetProgramVariable("persistenceJson"));
                Assert.That(vm.GetProgramVariable("restoredName"), Is.EqualTo("vm"));
                Assert.That(vm.GetProgramVariable("restoredBig"), Is.EqualTo(long.MinValue));
                Assert.That(vm.GetProgramVariable("restoredHuge"), Is.EqualTo(ulong.MaxValue));
                Assert.That(vm.GetProgramVariable("restoredScores"), Is.EqualTo(new[] { 3, -1, 40 }));
                Assert.That(vm.GetProgramVariable("restoredTags"), Is.EqualTo(new[] { "first", null }));
                Assert.That(vm.GetProgramVariable("legacyNumberRestored"), Is.EqualTo(true));
                Assert.That(vm.GetProgramVariable("legacyNumberValue"), Is.EqualTo(12345L));
                Assert.That(vm.GetProgramVariable("corruptRejected"), Is.EqualTo(true));
                Assert.That(vm.GetProgramVariable("outOfRangeRejected"), Is.EqualTo(true));
                Assert.That(vm.GetProgramVariable("validationErrorCount"), Is.EqualTo(1));
            }
        }

        private static void AssertRan(UdonBehaviour vm, string flag, string what)
        {
            Assert.That(vm.GetProgramVariable(flag), Is.EqualTo(true),
                $"The Udon VM halted inside the {what} run before reaching its last line; see the Udon error logged above.");
        }

        private static int[] Draws(CERandom random, Func<CERandom, int> draw, int count)
        {
            var draws = new int[count];
            for (int i = 0; i < count; i++)
                draws[i] = draw(random);
            return draws;
        }

        /// <summary>
        /// UdonSharpCompilerV1 only compiles scripts a program asset points at, and the fixture ships without
        /// one: it is created here and deleted again in TearDown with the program it produces.
        /// </summary>
        private UdonSharpProgramAsset CreateProgramAssetForFixture()
        {
            var host = new GameObject("CE.ProcgenPersistenceVmFixture") { hideFlags = HideFlags.HideAndDontSave };
            _createdObjects.Add(host);

            CEProcgenPersistenceVmTestBehaviour fixture = host.AddComponent<CEProcgenPersistenceVmTestBehaviour>();
            UdonSharpProgramAsset asset = UdonSharpEditorUtility.GetUdonSharpProgramAsset(fixture);
            Assert.That(asset, Is.Not.Null, "No program asset could be created for the Udon VM fixture.");

            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (!string.IsNullOrEmpty(assetPath) && !_createdAssetPaths.Contains(assetPath))
                _createdAssetPaths.Add(assetPath);

            return asset;
        }

        private static void CompileAll()
        {
            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });

            string[] errors = UdonSharpEditorCache.Instance.LastCompileDiagnostics?
                .Where(diagnostic => diagnostic.severity == UdonSharp.Compiler.DiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.message)
                .ToArray() ?? Array.Empty<string>();

            Assert.That(errors, Is.Empty, "Project failed to compile:\n" + string.Join("\n", errors));
        }
    }
}
