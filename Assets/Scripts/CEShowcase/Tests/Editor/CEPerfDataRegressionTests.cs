using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CESyncRuntimeTests;
using NUnit.Framework;
using UdonSharp;
using UdonSharp.CE.Data;
using UdonSharp.CE.Perf;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.Udon;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// Regression tests for the CE.Perf and CE.Data runtime libraries.
    ///
    /// Each test here pins one defect that produced a wrong answer without producing an error: a
    /// dictionary that dropped the entry that made it grow, a system switch that turned off a different
    /// system, a spatial grid that lost exactly the entities standing on its far wall, a JSON round trip
    /// that came back all zeros. None of them threw, logged or failed to compile, which is why nothing
    /// noticed -- so every assertion below is on the value that was wrong.
    ///
    /// Most run as plain .NET against the runtime assembly. Two execute the fixture behaviour as a
    /// compiled Udon program instead: the CEDictionary rebuild test, because the defect it covers is a
    /// property of how U# lays out a method call and does not exist under the CLR at all, and
    /// CoreCollectionsAndCompiler_ExecuteCorrectlyInUdon, which runs the fixture's RunAudit checks for
    /// the U# collections, compiler lowering and Procgen.
    /// </summary>
    public class CEPerfDataRegressionTests
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

            for (int i = _createdAssetPaths.Count - 1; i >= 0; i--)
            {
                string assetPath = _createdAssetPaths[i];
                if (string.IsNullOrEmpty(assetPath))
                    continue;

                // The compiled program is a separate asset in Assets/SerializedUdonPrograms and is not
                // removed along with the program asset that points at it. Read its path first; once the
                // program asset is gone nothing knows which serialized program was its.
                string serializedProgramPath = SerializedProgramPathOf(assetPath);

                AssetDatabase.DeleteAsset(assetPath);

                if (!string.IsNullOrEmpty(serializedProgramPath))
                    AssetDatabase.DeleteAsset(serializedProgramPath);
            }

            _createdAssetPaths.Clear();
            AssetDatabase.Refresh();

            // Leave the project compiled without the fixture, the way the next test expects to find it.
            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
        }

        // -----------------------------------------------------------------------------------------
        // CEDictionary: growth must not go back through Add (Udon VM only)
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The table used to be rebuilt by calling Add for each existing entry from inside Add. U# gives
        /// a method one static set of parameter slots, so the inner calls overwrote the outer call's key
        /// and value: when Resize returned, Add carried on with the last pair it had just rehashed,
        /// stored that a second time, and the entry the caller actually passed was gone. Count still
        /// went up by one, so the dictionary looked the right size.
        ///
        /// Under the CLR the same source is correct, so this cannot be a .NET test. The fixture is
        /// compiled to an Udon program and run by the VM, and the assertions read its heap.
        ///
        /// One test covers both ways into the rebuild, growth and the tombstone sweep on removal,
        /// because each run of it costs two whole-project Udon compiles.
        /// </summary>
        [Test]
        public void CEDictionary_RebuildingInsideTheUdonVM_KeepsEveryEntry()
        {
            UdonSharpProgramAsset asset = CreateProgramAssetForFixture();
            CompileAll();

            using (new UdonSyncHarness.RuntimeBootstrap())
            using (var pair = UdonSyncHarness.CreatePair(asset))
            {
                UdonBehaviour behaviour = pair.Owner;

                // Growth: 100 entries from 4 slots is five rebuilds.
                behaviour.SendCustomEvent(nameof(CEPerfDataRegressionTestBehaviour.RunGrowth));

                Assert.That(behaviour.GetProgramVariable("growthRan"), Is.EqualTo(true),
                    "RunGrowth never reached its last line, so the Udon program halted part-way through. " +
                    "Nothing below can be trusted until that is understood.");

                Assert.That(behaviour.GetProgramVariable("growthMissingKeys"), Is.EqualTo(0),
                    "Keys that were added are no longer in the dictionary; the first one missing is " +
                    behaviour.GetProgramVariable("growthFirstMissingKey") + ". A rebuild that re-enters " +
                    "Add loses the entry that triggered it.");

                Assert.That(behaviour.GetProgramVariable("growthWrongValues"), Is.EqualTo(0),
                    "Keys survived but came back with another entry's value.");

                Assert.That(behaviour.GetProgramVariable("growthCount"), Is.EqualTo(100),
                    "Count is wrong after 100 distinct adds.");

                // Removal: taking half the entries out crosses the tombstone threshold and rebuilds the
                // table in place, through the same code.
                behaviour.SendCustomEvent(nameof(CEPerfDataRegressionTestBehaviour.RunChurn));

                Assert.That(behaviour.GetProgramVariable("churnRan"), Is.EqualTo(true),
                    "RunChurn never reached its last line, so the Udon program halted part-way through.");

                Assert.That(behaviour.GetProgramVariable("churnCountAfterRemove"), Is.EqualTo(50));
                Assert.That(behaviour.GetProgramVariable("churnSurvivorsMissing"), Is.EqualTo(0),
                    "Entries that were never removed are missing or hold the wrong value after the " +
                    "in-place rebuild.");
                Assert.That(behaviour.GetProgramVariable("churnRemovedStillPresent"), Is.EqualTo(0),
                    "Removed keys are still being found.");

                Assert.That(behaviour.GetProgramVariable("churnCountAfterReAdd"), Is.EqualTo(100));
                Assert.That(behaviour.GetProgramVariable("churnMissingAfterReAdd"), Is.EqualTo(0),
                    "Keys added back after the rebuild cannot be found.");
            }
        }

        [Test]
        public void CoreCollectionsAndCompiler_ExecuteCorrectlyInUdon()
        {
            UdonSharpProgramAsset asset = CreateProgramAssetForFixture();
            CompileAll();
            using (new UdonSyncHarness.RuntimeBootstrap())
            using (var pair = UdonSyncHarness.CreatePair(asset))
            {
                pair.Owner.SendCustomEvent(nameof(CEPerfDataRegressionTestBehaviour.RunAudit));
                Assert.That(pair.Owner.GetProgramVariable("auditRan"), Is.EqualTo(true), "The Udon VM halted during the regression fixture.");
                Assert.That(pair.Owner.GetProgramVariable("auditFailures"), Is.EqualTo(0));
            }
        }

        // -----------------------------------------------------------------------------------------
        // CEDictionary: removal must not grow the table
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Sweeping tombstones used to go through the same routine as growing, which always doubled the
        /// table. Keys that never repeat -- player ids, handles, anything monotonic -- leave a tombstone
        /// behind on every removal, so capacity tracked the number of removals ever made instead of the
        /// number of entries held: 10,000 add/remove cycles on a dictionary that never held more than
        /// one entry left it tens of thousands of slots wide, and GetKeys, ContainsValue, Clear and
        /// enumeration all walk every slot.
        /// </summary>
        [Test]
        public void CEDictionary_AddRemoveChurnWithFreshKeys_DoesNotGrowTheTable()
        {
            var dictionary = new CEDictionary<int, int>();
            int initialCapacity = CapacityOf(dictionary);

            // A few entries that stay put for the whole run, so the in-place rebuilds have something to
            // carry across and lose.
            for (int resident = -1; resident >= -5; resident--)
                dictionary.Add(resident, resident * 10);

            for (int i = 0; i < 10000; i++)
            {
                dictionary.Add(i, i);
                Assert.That(dictionary.Remove(i), Is.True);
            }

            Assert.That(dictionary.Count, Is.EqualTo(5));

            for (int resident = -1; resident >= -5; resident--)
            {
                Assert.That(dictionary.TryGetValue(resident, out int value), Is.True,
                    $"Resident key {resident} did not survive the tombstone sweeps.");
                Assert.That(value, Is.EqualTo(resident * 10));
            }

            Assert.That(CapacityOf(dictionary), Is.EqualTo(initialCapacity),
                "Capacity grew although the dictionary never held more than six entries. Removal is " +
                "meant to rebuild the table at the size it already has.");
        }

        // -----------------------------------------------------------------------------------------
        // CEWorld: the index RegisterSystem returns must keep naming the same system
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Sorting systems by order used to move the systems themselves, along with their enabled flags.
        /// The index RegisterSystem had already handed back was not updated, so from the first Tick on it
        /// named whichever system had been sorted into that position -- and DisableSystem(physics)
        /// switched off input instead, while physics kept running.
        /// </summary>
        [Test]
        public void CEWorld_SystemIndex_StillNamesTheSameSystemAfterTickSortsByOrder()
        {
            CEPerfDataRegressionTestBehaviour fixture = CreateFixtureComponent();
            var world = new CEWorld(4);

            // Registered late-first, so sorting by order has to reverse them.
            int late = world.RegisterSystem(fixture, nameof(CEPerfDataRegressionTestBehaviour.SystemLate), 20);
            int early = world.RegisterSystem(fixture, nameof(CEPerfDataRegressionTestBehaviour.SystemEarly), 10);

            world.Tick();
            Assert.That(fixture.systemLog, Is.EqualTo("EL"), "Systems did not run lowest order first.");

            world.DisableSystem(late);

            Assert.That(world.IsSystemEnabled(late), Is.False);
            Assert.That(world.IsSystemEnabled(early), Is.True);

            fixture.systemLog = "";
            world.Tick();

            Assert.That(fixture.systemLog, Is.EqualTo("E"),
                "Disabling the system registered with order 20 should leave only the order-10 one " +
                "running. A log of \"L\" means the index was applied to the sorted position and the " +
                "wrong system was switched off.");

            world.EnableSystem(late);
            world.DisableSystem(early);

            fixture.systemLog = "";
            world.Tick();

            Assert.That(fixture.systemLog, Is.EqualTo("L"));
        }

        // -----------------------------------------------------------------------------------------
        // CEWorld: deferred-destroy queue
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The flush queue is only emptied by a flush, and deferring used to append unconditionally. An
        /// entity deferred, cancelled and deferred again took two entries; once the queue was full the
        /// next deferral set the entity's state and returned true without queueing it, and that entity
        /// was then never flushed -- stuck in PendingDestroy, in neither the active list nor the free
        /// list, its slot gone for good.
        /// </summary>
        [Test]
        public void CEWorld_DeferCancelDefer_DoesNotCrowdAnotherEntityOutOfTheFlushQueue()
        {
            var world = new CEWorld(2);
            int a = world.CreateEntity();
            int b = world.CreateEntity();

            Assert.That(world.DestroyEntityDeferred(a), Is.True);
            Assert.That(world.CancelDestruction(a), Is.True);
            Assert.That(world.DestroyEntityDeferred(a), Is.True);
            Assert.That(world.DestroyEntityDeferred(b), Is.True);

            Assert.That(world.FlushPendingDestructions(), Is.EqualTo(2));

            Assert.That(world.EntityExists(a), Is.False);
            Assert.That(world.EntityExists(b), Is.False,
                "Entity b was deferred, reported as deferred, and then never destroyed.");
            Assert.That(world.AvailableEntitySlots, Is.EqualTo(2));
            Assert.That(world.PendingDestroyCount, Is.EqualTo(0));

            // Both ids must be reusable, and a second round through the queue must behave the same.
            Assert.That(world.CreateEntity(), Is.Not.EqualTo(CEWorld.InvalidEntity));
            Assert.That(world.CreateEntity(), Is.Not.EqualTo(CEWorld.InvalidEntity));
        }

        /// <summary>
        /// PendingDestroyCount used to be the length of the flush queue, which keeps an entry for every
        /// deferral until the next flush whatever happened to the entity since. A world that defers and
        /// then destroys immediately without ever calling Tick -- the Bullet Storm showcase does exactly
        /// that -- reported its full capacity as pending, permanently, with nothing pending at all.
        /// </summary>
        [Test]
        public void CEWorld_PendingDestroyCount_CountsEntitiesThatAreActuallyPending()
        {
            var world = new CEWorld(4);

            int destroyed = world.CreateEntity();
            world.DestroyEntityDeferred(destroyed);
            Assert.That(world.PendingDestroyCount, Is.EqualTo(1));
            world.DestroyEntity(destroyed);
            Assert.That(world.PendingDestroyCount, Is.EqualTo(0), "Destroyed immediately while pending.");

            int cancelled = world.CreateEntity();
            world.DestroyEntityDeferred(cancelled);
            world.CancelDestruction(cancelled);
            Assert.That(world.PendingDestroyCount, Is.EqualTo(0), "Destruction was cancelled.");

            int disabled = world.CreateEntity();
            world.DestroyEntityDeferred(disabled);
            world.DisableEntity(disabled);
            Assert.That(world.PendingDestroyCount, Is.EqualTo(0), "Disabling cancels a pending destruction.");

            // Many more defer-then-destroy cycles than the world has slots: the count must stay live
            // and every deferral must still take effect.
            for (int i = 0; i < 20; i++)
            {
                int id = world.CreateEntity();
                Assert.That(id, Is.Not.EqualTo(CEWorld.InvalidEntity), $"Ran out of entities on cycle {i}.");
                Assert.That(world.DestroyEntityDeferred(id), Is.True);
                Assert.That(world.GetEntityState(id), Is.EqualTo(EntityState.PendingDestroy));
                Assert.That(world.DestroyEntity(id), Is.True);
            }

            Assert.That(world.PendingDestroyCount, Is.EqualTo(0));

            int last = world.CreateEntity();
            world.DestroyEntityDeferred(last);
            Assert.That(world.PendingDestroyCount, Is.EqualTo(1));
            Assert.That(world.FlushPendingDestructions(), Is.EqualTo(1));
            Assert.That(world.EntityExists(last), Is.False);
            Assert.That(world.PendingDestroyCount, Is.EqualTo(0));
        }

        // -----------------------------------------------------------------------------------------
        // CEWorld: ClearEntities and entity versions
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Versions exist so that a held (id, version) pair stops matching once its entity is gone.
        /// ClearEntities destroyed every entity without moving any version and then handed ids out again
        /// from 0, so a pair held from before the clear matched the unrelated entity created after it.
        /// </summary>
        [Test]
        public void CEWorld_ClearEntities_InvalidatesVersionsOfTheEntitiesItDestroyed()
        {
            var world = new CEWorld(4);

            int live = world.CreateEntity();
            int disabled = world.CreateEntity();
            world.DisableEntity(disabled);

            int liveVersion = world.GetEntityVersion(live);
            int disabledVersion = world.GetEntityVersion(disabled);
            int untouchedVersion = world.GetEntityVersion(3);

            world.ClearEntities();

            int recycled = world.CreateEntity();
            Assert.That(recycled, Is.EqualTo(live), "The test relies on ids restarting from 0 after a clear.");

            Assert.That(world.GetEntityVersion(recycled), Is.Not.EqualTo(liveVersion),
                "A handle taken before ClearEntities still validates against the entity created after it.");
            Assert.That(world.GetEntityVersion(disabled), Is.Not.EqualTo(disabledVersion));
            Assert.That(world.GetEntityVersion(3), Is.EqualTo(untouchedVersion),
                "A slot that never held an entity has nothing to invalidate.");
        }

        // -----------------------------------------------------------------------------------------
        // CEGrid: bounds
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// A coordinate exactly on the max face floors to one past the last cell whenever the extent is
        /// a whole number of cells, and was rejected as out of bounds. Mathf.Clamp(pos, min, max) lands
        /// on max exactly, so this dropped precisely the entities a caller had pinned to its arena wall
        /// out of every query -- on the +X, +Y and +Z sides only; the min faces always worked. The
        /// bounds below are the Flocking showcase's, which clamps its agents this way every frame.
        /// </summary>
        [Test]
        public void CEGrid_PositionExactlyOnMaxBounds_IsInsideTheLastCell()
        {
            var grid = new CEGrid(new Vector3(-25f, 0f, -25f), new Vector3(25f, 5f, 25f), 5f, 8);

            Assert.That(grid.Insert(1, new Vector3(25f, 2f, 0f)), Is.True, "+X face");
            Assert.That(grid.Insert(2, new Vector3(0f, 5f, 0f)), Is.True, "+Y face");
            Assert.That(grid.Insert(3, new Vector3(0f, 2f, 25f)), Is.True, "+Z face");
            Assert.That(grid.Insert(4, new Vector3(25f, 5f, 25f)), Is.True, "max corner");
            Assert.That(grid.Insert(5, new Vector3(-25f, 0f, -25f)), Is.True, "min corner");
            Assert.That(grid.EntityCount, Is.EqualTo(5));

            Assert.That(grid.PositionToCellIndex(new Vector3(25f, 5f, 25f)), Is.EqualTo(grid.TotalCells - 1),
                "The max corner belongs to the last cell.");

            // Still a boundary: anything past the face is outside.
            Assert.That(grid.PositionToCellIndex(new Vector3(25.01f, 2f, 0f)), Is.EqualTo(-1));
            Assert.That(grid.PositionToCellIndex(new Vector3(0f, 5.01f, 0f)), Is.EqualTo(-1));
            Assert.That(grid.PositionToCellIndex(new Vector3(0f, 2f, 25.01f)), Is.EqualTo(-1));
            Assert.That(grid.PositionToCellIndex(new Vector3(-25.01f, 2f, 0f)), Is.EqualTo(-1));

            // And what was stored on the face has to be found from the face, including by a query that
            // begins exactly there.
            int[] buffer = new int[16];

            int found = grid.QueryRadius(new Vector3(25f, 2f, 0f), 0f, buffer);
            Assert.That(Ids(buffer, found), Does.Contain(1));

            found = grid.QueryBox(new Vector3(25f, 5f, 25f), new Vector3(30f, 10f, 30f), buffer);
            Assert.That(Ids(buffer, found), Is.EquivalentTo(new[] { 4 }));
        }

        /// <summary>
        /// "Everything, however far away" is a reasonable query, and it returned nothing. A query bound
        /// that large was converted straight to int, which does not saturate: on x64 the result is
        /// int.MinValue, and as the upper end of the cell range that value won the Mathf.Min against the
        /// last cell, so the cell loops never ran.
        ///
        /// The old behaviour depended on how the platform converts an out-of-range float, so on a
        /// machine where that conversion saturates this test would also have passed before the fix.
        /// </summary>
        [Test]
        public void CEGrid_UnboundedQuery_ReturnsEverythingInsteadOfNothing()
        {
            var grid = new CEGrid(new Vector3(-10f, -10f, -10f), new Vector3(10f, 10f, 10f), 2f, 8);

            grid.Insert(1, new Vector3(-9f, -9f, -9f));
            grid.Insert(2, Vector3.zero);
            grid.Insert(3, new Vector3(9f, 9f, 9f));

            int[] buffer = new int[8];

            int found = grid.QueryRadius(Vector3.zero, float.PositiveInfinity, buffer);
            Assert.That(Ids(buffer, found), Is.EquivalentTo(new[] { 1, 2, 3 }), "infinite radius");

            found = grid.QueryRadius(Vector3.zero, float.MaxValue, buffer);
            Assert.That(Ids(buffer, found), Is.EquivalentTo(new[] { 1, 2, 3 }), "float.MaxValue radius");

            found = grid.QueryBox(
                new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity),
                new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity),
                buffer);
            Assert.That(Ids(buffer, found), Is.EquivalentTo(new[] { 1, 2, 3 }), "infinite box");

            // Saturating must not turn "far outside the grid" into "the edge of the grid".
            Assert.That(grid.QueryRadius(new Vector3(1e12f, 0f, 0f), 1f, buffer), Is.EqualTo(0));
            Assert.That(grid.QueryRadius(new Vector3(-1e12f, 0f, 0f), 1f, buffer), Is.EqualTo(0));
        }

        // -----------------------------------------------------------------------------------------
        // CE.Data: numbers read back from DataTokens
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// VRCJson returns every JSON number as a Double, and the typed conversions used to insist on
        /// an exact token type. A list of ints written with ToJson therefore came back from FromJson with
        /// the right length and every element 0 -- no error, no warning, just the data gone.
        /// </summary>
        [Test]
        public void CEData_IntegersAndFloats_SurviveAJsonRoundTrip()
        {
            CEList<int> parsed = CEList<int>.FromJson("[1,2,3]");
            Assert.That(ToArray(parsed), Is.EqualTo(new[] { 1, 2, 3 }));

            var original = new CEList<int>(new[] { 40, -5, 2147483647 });
            Assert.That(ToArray(CEList<int>.FromJson(original.ToJson())), Is.EqualTo(new[] { 40, -5, 2147483647 }));

            CEList<float> floats = CEList<float>.FromJson("[1.5,-2.25]");
            Assert.That(ToArray(floats), Is.EqualTo(new[] { 1.5f, -2.25f }));

            var counts = new CEDictionary<string, int>();
            counts.Add("coins", 5);
            counts.Add("keys", 2);

            CEDictionary<string, int> restored = CEDictionary<string, int>.FromJson(counts.ToJson());
            Assert.That(restored.Count, Is.EqualTo(2));
            Assert.That(restored["coins"], Is.EqualTo(5));
            Assert.That(restored["keys"], Is.EqualTo(2));
        }

        /// <summary>
        /// The coercion has rules, and each of them is here because the alternative is a wrong value or,
        /// in Udon, a halted behaviour: a float-to-integer cast there is System.Convert, which throws on
        /// overflow, so an oversized number must be clamped before it is cast.
        /// </summary>
        [Test]
        public void CEData_NumericCoercion_TruncatesClampsAndKeepsExactMatchesExact()
        {
            // Truncation toward zero, as a C# cast would.
            Assert.That(ToArray(CEList<int>.FromDataList(Tokens(new DataToken(2.9), new DataToken(-2.9)))),
                Is.EqualTo(new[] { 2, -2 }));

            // Out of range clamps to the target's range instead of overflowing.
            Assert.That(ToArray(CEList<int>.FromDataList(Tokens(new DataToken(1e12), new DataToken(-1e12)))),
                Is.EqualTo(new[] { int.MaxValue, int.MinValue }));
            Assert.That(ToArray(CEList<byte>.FromDataList(Tokens(new DataToken(300.0), new DataToken(-5.0)))),
                Is.EqualTo(new byte[] { 255, 0 }));
            Assert.That(ToArray(CEList<uint>.FromDataList(Tokens(new DataToken(-1.0), new DataToken(1e12)))),
                Is.EqualTo(new[] { 0u, uint.MaxValue }));

            // long.MaxValue is not representable as a double; the clamp stops at the largest double
            // that still fits rather than at the rounded-up value that would overflow the cast.
            CEList<long> longs = CEList<long>.FromDataList(Tokens(new DataToken(1e30), new DataToken(-1e30)));
            Assert.That(longs[0], Is.EqualTo(9223372036854774784L));
            Assert.That(longs[1], Is.EqualTo(long.MinValue));

            CEList<ulong> ulongs = CEList<ulong>.FromDataList(Tokens(new DataToken(1e30), new DataToken(-1.0)));
            Assert.That(ulongs[0], Is.EqualTo(18446744073709549568UL));
            Assert.That(ulongs[1], Is.EqualTo(0UL));

            // A token that already has the right type is not routed through double, which could not
            // hold this value.
            CEList<long> exact = CEList<long>.FromDataList(Tokens(new DataToken(long.MaxValue)));
            Assert.That(exact[0], Is.EqualTo(long.MaxValue));

            // Any numeric token will do, not only a Double.
            Assert.That(ToArray(CEList<float>.FromDataList(Tokens(new DataToken(7), new DataToken(2.5)))),
                Is.EqualTo(new[] { 7f, 2.5f }));
            Assert.That(ToArray(CEList<double>.FromDataList(Tokens(new DataToken(7), new DataToken(1.5f)))),
                Is.EqualTo(new[] { 7.0, 1.5 }));

            // A boolean or a string where a number was expected is a wrong payload, not a number.
            Assert.That(ToArray(CEList<int>.FromDataList(Tokens(new DataToken(true), new DataToken("12")))),
                Is.EqualTo(new[] { 0, 0 }));
        }

        // -----------------------------------------------------------------------------------------
        // BatchProcessor
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// A changed item count used to send the pass back to index 0. Time-slicing exists for
        /// collections that change between frames, so with a count that moved every frame only the
        /// first batch was ever processed: everything after it starved, the pass never completed and
        /// CompletedPasses stayed at 0.
        /// </summary>
        [Test]
        public void BatchProcessor_WhenTheItemCountChangesEveryFrame_StillReachesEveryItem()
        {
            var processor = new BatchProcessor(50);
            var visits = new Dictionary<int, int>();
            Action<int> record = index =>
            {
                visits.TryGetValue(index, out int seen);
                visits[index] = seen + 1;
            };

            Assert.That(processor.Process(120, record), Is.False);
            Assert.That(processor.CurrentIndex, Is.EqualTo(50));

            Assert.That(processor.Process(121, record), Is.False);
            Assert.That(processor.CurrentIndex, Is.EqualTo(100),
                "The pass restarted because the count changed, so it will never get past the first batch.");

            Assert.That(processor.Process(122, record), Is.True);
            Assert.That(processor.CompletedPasses, Is.EqualTo(1));

            Assert.That(visits.Count, Is.EqualTo(122));
            Assert.That(visits.Values.All(count => count == 1), Is.True,
                "Every index should have been processed exactly once in the pass.");
        }

        /// <summary>
        /// The other direction: the collection shrinks beneath the pass. The position is pulled back to
        /// the new end, which ends the pass; it must not index past the collection.
        /// </summary>
        [Test]
        public void BatchProcessor_WhenTheCollectionShrinksBeneathThePass_EndsThePassWithoutOverrunning()
        {
            var processor = new BatchProcessor(50);
            int highestIndex = -1;
            Func<int, bool> record = index =>
            {
                if (index > highestIndex) highestIndex = index;
                return true;
            };

            Assert.That(processor.ProcessWhile(200, record), Is.False);
            Assert.That(processor.ProcessWhile(201, record), Is.False);
            Assert.That(processor.CurrentIndex, Is.EqualTo(100),
                "ProcessWhile restarted because the count changed.");

            highestIndex = -1;
            Assert.That(processor.ProcessWhile(60, record), Is.True,
                "Only 60 items remain and 100 have been handled, so the pass is over.");
            Assert.That(highestIndex, Is.EqualTo(-1), "Nothing beyond the new end may be processed.");
            Assert.That(processor.CompletedPasses, Is.EqualTo(1));
        }

        // -----------------------------------------------------------------------------------------
        // CEPool
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// A handle carried only the slot index. Slots are reused last-in-first-out, so after Release(h)
        /// the very next acquire hands out h's index again -- and a second Release(h), an ordinary double
        /// release, returned the new owner's object to the pool while they were still using it.
        /// </summary>
        [Test]
        public void CEPool_StaleHandle_CannotReleaseTheSlotsNextOwner()
        {
            var pool = new CEPool<object>(4);
            pool.Initialize(index => new object());

            PoolHandle<object> first = pool.AcquireHandle();
            Assert.That(pool.Release(first), Is.True);

            PoolHandle<object> second = pool.AcquireHandle();
            Assert.That(second.Index, Is.EqualTo(first.Index),
                "The test relies on the freed slot being handed straight back out.");

            Assert.That(pool.Release(first), Is.False, "A handle that was already released freed its slot's new owner.");
            Assert.That(pool.IsInUse(second.Index), Is.True);
            Assert.That(pool.ActiveCount, Is.EqualTo(1));

            Assert.That(pool.Release(second), Is.True);
            Assert.That(pool.Release(second), Is.False);
            Assert.That(pool.ActiveCount, Is.EqualTo(0));

            // ReleaseAll retires outstanding handles too.
            PoolHandle<object> third = pool.AcquireHandle();
            pool.ReleaseAll();
            PoolHandle<object> fourth = pool.AcquireHandle();
            Assert.That(fourth.Index, Is.EqualTo(third.Index));
            Assert.That(pool.Release(third), Is.False);
            Assert.That(pool.Release(fourth), Is.True);
        }

        // -----------------------------------------------------------------------------------------
        // CEList
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// GetEnumerator handed back one cached iterator every time. A nested loop over the same list
        /// reset the outer loop's position and left it at the end when the inner loop finished, so the
        /// outer loop ran once: nine pairs expected, three delivered.
        /// </summary>
        [Test]
        public void CEList_NestedEnumeration_VisitsEveryPair()
        {
            var list = new CEList<int>(new[] { 1, 2, 3 });

            var pairs = new List<string>();
            foreach (int outer in list)
            {
                foreach (int inner in list)
                    pairs.Add(outer + ":" + inner);
            }

            Assert.That(pairs, Is.EqualTo(new[]
            {
                "1:1", "1:2", "1:3",
                "2:1", "2:2", "2:3",
                "3:1", "3:2", "3:3",
            }));

            // Leaving a loop early must not strand the list: the next enumeration starts from the top.
            foreach (int value in list)
            {
                if (value == 1)
                    break;
            }

            var afterBreak = new List<int>();
            foreach (int value in list)
                afterBreak.Add(value);

            Assert.That(afterBreak, Is.EqualTo(new[] { 1, 2, 3 }));

            // Two enumerators taken before either has moved -- what comparing a list with itself does --
            // have to be independent too, not the same cached object advanced by both.
            System.Collections.IEnumerator first = list.GetEnumerator();
            System.Collections.IEnumerator second = list.GetEnumerator();

            Assert.That(first.MoveNext(), Is.True);
            Assert.That(second.MoveNext(), Is.True);
            Assert.That(first.Current, Is.EqualTo(1),
                "Moving the second enumerator moved the first: both are the list's cached iterator.");
            Assert.That(second.Current, Is.EqualTo(1));
        }

        // -----------------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------------

        private CEPerfDataRegressionTestBehaviour CreateFixtureComponent()
        {
            var host = new GameObject("CE.PerfDataRegressionFixture") { hideFlags = HideFlags.HideAndDontSave };
            _createdObjects.Add(host);

            return host.AddComponent<CEPerfDataRegressionTestBehaviour>();
        }

        /// <summary>
        /// Gives the fixture a program asset so the Udon compiler picks it up. UdonSharpCompilerV1 only
        /// compiles the scripts program assets point at, and the fixture ships without one: it is created
        /// here and deleted again in TearDown, together with the serialized program it produces.
        /// </summary>
        private UdonSharpProgramAsset CreateProgramAssetForFixture()
        {
            CEPerfDataRegressionTestBehaviour fixture = CreateFixtureComponent();

            UdonSharpProgramAsset asset = UdonSharpEditorUtility.GetUdonSharpProgramAsset(fixture);
            Assert.That(asset, Is.Not.Null, "No program asset could be created for the Udon VM fixture.");

            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (!string.IsNullOrEmpty(assetPath) && !_createdAssetPaths.Contains(assetPath))
                _createdAssetPaths.Add(assetPath);

            return asset;
        }

        private static string SerializedProgramPathOf(string programAssetPath)
        {
            var programAsset = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(programAssetPath);

            if (programAsset == null || programAsset.SerializedProgramAsset == null)
                return null;

            return AssetDatabase.GetAssetPath(programAsset.SerializedProgramAsset);
        }

        private static void CompileAll()
        {
            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });

            var errors = UdonSharpEditorCache.Instance.LastCompileDiagnostics?
                .Where(d => d.severity == DiagnosticSeverity.Error)
                .Select(d => d.message)
                .ToArray() ?? Array.Empty<string>();

            Assert.That(errors, Is.Empty, "Project failed to compile:\n" + string.Join("\n", errors));
        }

        /// <summary>
        /// Slot count of a dictionary's table. Read by reflection because capacity is deliberately not
        /// part of the public surface, and it is the only place the unbounded growth was visible.
        /// </summary>
        private static int CapacityOf<TKey, TValue>(CEDictionary<TKey, TValue> dictionary)
        {
            FieldInfo capacityField = typeof(CEDictionary<TKey, TValue>).GetField(
                "_capacity", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(capacityField, Is.Not.Null,
                "CEDictionary no longer has a _capacity field; point this helper at whatever replaced it.");

            return (int)capacityField.GetValue(dictionary);
        }

        private static DataList Tokens(params DataToken[] tokens)
        {
            var list = new DataList();

            foreach (DataToken token in tokens)
                list.Add(token);

            return list;
        }

        private static T[] ToArray<T>(CEList<T> list)
        {
            var result = new T[list.Count];

            for (int i = 0; i < result.Length; i++)
                result[i] = list[i];

            return result;
        }

        private static int[] Ids(int[] buffer, int count)
        {
            var result = new int[count];
            Array.Copy(buffer, result, count);
            return result;
        }
    }
}
