using System;
using System.Reflection;
using NUnit.Framework;
using UdonSharp.CE.NetPhysics;
using UdonSharp.CE.Persistence;
using UdonSharp.CE.Procgen;
using UnityEngine;

namespace CEShowcase.Tests.Editor
{
    public class CEAuditPersistenceProcgenRegressionTests
    {
        [TestCase(-1)]
        [TestCase(int.MinValue)]
        [TestCase(0)]
        public void Random_WideRanges_StayInsideExclusiveBounds(int seed)
        {
            var random = new CERandom(seed);
            bool negative = false, positive = false;
            for (int i = 0; i < 1000; i++)
            {
                int value = random.Range(int.MinValue, int.MaxValue);
                Assert.That(value, Is.LessThan(int.MaxValue));
                negative |= value < 0;
                positive |= value > 0;
            }
            Assert.That(negative && positive, Is.True);
        }

        [Test]
        public void Simplex3D_DenseSamples_StayFinite()
        {
            var random = new CERandom(17);
            for (int i = 0; i < 3000; i++)
            {
                float value = CENoise.Simplex3D(random.Range(-50f, 50f), random.Range(-50f, 50f), random.Range(-50f, 50f));
                Assert.That(float.IsNaN(value) || float.IsInfinity(value), Is.False);
            }
        }

        [Test]
        public void Noise_ExactNegativeIntegers_AreContinuousAndSeedAffectsWorley()
        {
            float at = CENoise.Perlin2D(-2f, 0.3f);
            Assert.That(Mathf.Abs(at - CENoise.Perlin2D(-1.99999f, 0.3f)), Is.LessThan(0.001f));
            CENoise.Initialize(1);
            float first = CENoise.Worley2D(0.3f, 0.7f);
            CENoise.Initialize(999);
            Assert.That(CENoise.Worley2D(0.3f, 0.7f), Is.Not.EqualTo(first));
        }

        [TestCase(1)]
        [TestCase(64)]
        public void Wfc_MaximumTileMaskAndZeroWeights_Terminate(int count)
        {
            var tiles = new WFCTile[count];
            var ids = new int[count];
            for (int i = 0; i < count; i++) ids[i] = i - 1;
            for (int i = 0; i < count; i++) tiles[i] = new WFCTile { Id = i - 1, Weight = 0f,
                ValidNorth = ids, ValidSouth = ids, ValidEast = ids, ValidWest = ids };
            var solver = new WFCSolver(new WFCConfig { Width = 3, Height = 3, Tiles = tiles }, 1);
            Assert.That(solver.Solve(), Is.EqualTo(WFCStatus.Completed));
            Assert.That(solver.CollapsedCount, Is.EqualTo(9));
            Assert.That(solver.GetResult(), Has.Length.EqualTo(9));
        }

        [Test]
        public void Wfc_RepeatedSetTile_DoesNotDoubleCount()
        {
            var solver = new WFCSolver(new WFCConfig { Width = 1, Height = 1, Tiles = new[] { new WFCTile { Id = -1 } } }, 1);
            Assert.That(solver.SetTile(0, 0, -1), Is.True);
            Assert.That(solver.SetTile(0, 0, -1), Is.True);
            Assert.That(solver.CollapsedCount, Is.EqualTo(1));
        }

        [Test]
        public void Dungeon_CanPlaceRoomsAtTheLastValidColumn()
        {
            var config = new DungeonConfig { RoomCount = 2, MinRoomSize = Vector2Int.one, MaxRoomSize = Vector2Int.one, MaxBounds = new Vector2Int(2, 1), RoomSpacing = 0 };
            var layout = CEDungeon.Generate(new CERandom(12), config);
            Assert.That(layout.Rooms, Has.Length.EqualTo(2));
        }

        [TestCase(float.MaxValue)]
        [TestCase(float.MinValue)]
        public void SizeEstimator_LargeFloat_DoesNotOverflow(float value)
            => Assert.That(SizeEstimator.EstimateFloatSize(value), Is.GreaterThan(0));

        [Test]
        public void SizeEstimator_IntegerLimitsAndNestedTokens_AreSafe()
        {
            Assert.That(SizeEstimator.EstimateIntegerSize(long.MinValue), Is.EqualTo(20));
            Assert.That(SizeEstimator.EstimateDoubleSize(double.MaxValue), Is.GreaterThan(0));
            var data = new VRC.SDK3.Data.DataDictionary();
            var list = new VRC.SDK3.Data.DataList(); list.Add(12);
            data["nested"] = list;
            Assert.That(SizeEstimator.EstimateDataDictionarySize(data), Is.GreaterThan(10));
        }
    }

    public class CEAuditNetPhysicsRegressionTests
    {
        private static PhysicsSnapshot Snapshot(int frame, Vector3 position)
        {
            var state = new PhysicsSnapshot(1) { Frame = frame, EntityCount = 1 };
            state.EntityIds[0] = 7; state.Positions[0] = position;
            state.Rotations[0] = Quaternion.identity;
            return state;
        }

        [Test]
        public void Compressor_RoundTripsArenaCoordinatesAndInputLag()
        {
            var codec = new StateCompressor { WorldMin = new Vector3(400, -10, 250), WorldMax = new Vector3(600, 50, 450) };
            var state = Snapshot(10, new Vector3(555, 2, 410)); state.InputLagFrames[0] = 12;
            var decoded = new PhysicsSnapshot(1);
            Assert.That(codec.Decompress(codec.Compress(state), decoded), Is.True);
            Assert.That(Vector3.Distance(decoded.Positions[0], state.Positions[0]), Is.LessThan(0.02f));
            Assert.That(decoded.InputLagFrames[0], Is.EqualTo(12));
        }

        [Test]
        public void FrameHistory_ReplacingAFrame_DoesNotSlideTheWindow()
        {
            var history = new FrameHistory(4, 1);
            for (int frame = 10; frame < 14; frame++) history.Record(frame, Snapshot(frame, Vector3.zero), new InputFrame());
            history.Record(11, Snapshot(11, Vector3.one), new InputFrame());
            Assert.That(history.Count, Is.EqualTo(4));
            Assert.That(history.OldestFrame, Is.EqualTo(10));
            Assert.That(history.NewestFrame, Is.EqualTo(13));
            Assert.That(history.GetState(11).Positions[0], Is.EqualTo(Vector3.one));
        }

        [Test]
        public void Reconciliation_MeasuresAnOffsetAtTheSharedFrame()
        {
            var correction = new RollbackManager { PositionThreshold = 0.01f };
            Assert.That(correction.CalculateCorrection(Snapshot(10, new Vector3(12, 0, 0)), 0, Snapshot(10, new Vector3(10, 0, 0)), 0, 0f), Is.True);
            Assert.That(correction.PositionDelta, Is.EqualTo(new Vector3(2, 0, 0)));
            Assert.That(RollbackManager.ExtrapolatePosition(Vector3.zero, new Vector3(2, 0, 0), 5, 20, 0.1f), Is.EqualTo(Vector3.right));
        }

        [Test]
        public void CorrectionBlender_DecaysAnOffset_AndClears()
        {
            var blend = new CorrectionBlender(); blend.AddOffset(new Vector3(10, 0, 0), Quaternion.identity);
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < 1000; i++) sum += blend.TakePositionStep();
            Assert.That(sum.x, Is.EqualTo(10f).Within(0.02f));
            blend.AddOffset(Vector3.one, Quaternion.identity); blend.Clear();
            Assert.That(blend.TakePositionStep(), Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void InputBuffer_PlayerIdsBeyondCapacity_StayIndependent()
        {
            var buffer = new InputBuffer();
            buffer.EnqueueInput(81, new InputFrame { FrameNumber = 10, Throttle = 127 });
            Assert.That(buffer.ConsumeInput(81, 20).Throttle, Is.EqualTo(127));
            Assert.That(buffer.GetInputLag(81), Is.EqualTo(10));
            buffer.EnqueueInput(161, new InputFrame { FrameNumber = 21, Throttle = 1 });
            Assert.That(buffer.PeekInput(81).Throttle, Is.EqualTo(127));
            Assert.That(buffer.ConsumeInput(161, 22).Throttle, Is.EqualTo(1));
            Assert.That(buffer.PeekInput(-1).Throttle, Is.EqualTo(0));
        }

        [Test]
        public void InputBuffer_FullTable_PreservesActivePlayersAndReusesReleasedSlots()
        {
            var buffer = new InputBuffer(2);
            buffer.EnqueueInput(1, new InputFrame { FrameNumber = 1, Throttle = 11 });
            buffer.EnqueueInput(3, new InputFrame { FrameNumber = 1, Throttle = 33 });
            buffer.EnqueueInput(5, new InputFrame { FrameNumber = 1, Throttle = 55 });
            Assert.That(buffer.PeekInput(1).Throttle, Is.EqualTo(11));
            Assert.That(buffer.PeekInput(3).Throttle, Is.EqualTo(33));
            Assert.That(buffer.PeekInput(5).Throttle, Is.EqualTo(0));
            buffer.RemovePlayer(1);
            buffer.EnqueueInput(5, new InputFrame { FrameNumber = 2, Throttle = 55 });
            Assert.That(buffer.PeekInput(5).Throttle, Is.EqualTo(55));
            Assert.That(buffer.PeekInput(3).Throttle, Is.EqualTo(33));
        }

        [Test]
        public void InputPredictor_CollidingIds_KeepIndependentHistory()
        {
            var predictor = new InputPredictor(2);
            predictor.UpdateLastKnown(1, new InputFrame { FrameNumber = 10, Throttle = 11 }, 1f);
            predictor.UpdateLastKnown(3, new InputFrame { FrameNumber = 10, Throttle = 33 }, 1f);
            Assert.That(predictor.Predict(1, 11, 1f).Throttle, Is.EqualTo(11));
            Assert.That(predictor.Predict(3, 11, 1f).Throttle, Is.EqualTo(33));
            predictor.RemovePlayer(1);
            predictor.UpdateLastKnown(5, new InputFrame { FrameNumber = 11, Throttle = 55 }, 1f);
            Assert.That(predictor.Predict(5, 12, 1f).Throttle, Is.EqualTo(55));
            Assert.That(predictor.Predict(3, 12, 1f).Throttle, Is.EqualTo(33));
        }

        [Test]
        public void PriorityDebt_EventuallySendsLowPriorityEntities()
        {
            var priorities = new[] { 100f, 1f, 1f }; var debt = new float[3]; var selection = new int[3];
            var seen = new bool[3]; var prioritizer = new SyncPrioritizer();
            for (int packet = 0; packet < 300; packet++)
            {
                Assert.That(prioritizer.SelectForPacket(priorities, debt, 3, 1, selection), Is.EqualTo(1));
                seen[selection[0]] = true;
            }
            Assert.That(seen, Is.All.True);
        }

        [Test]
        public void WorldStart_PreservesEarlyRegistrationAndUsesRelativeBounds()
        {
            var host = new GameObject("Physics fixture"); var bodyHost = new GameObject("Entity fixture");
            try
            {
                host.transform.position = new Vector3(500, 0, 350);
                var world = host.AddComponent<NetPhysicsWorld>(); world.AutoSimulate = false;
                var entity = bodyHost.AddComponent<NetBall>(); world.RegisterEntity(entity);
                typeof(NetPhysicsWorld).GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(world, null);
                Assert.That(world.EntityCount, Is.EqualTo(1));
                Assert.That(world.GetEntity(entity.EntityId), Is.SameAs(entity));
                var codec = (StateCompressor)typeof(NetPhysicsWorld).GetField("_stateCompressor", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(world);
                Assert.That(codec.WorldMin, Is.EqualTo(host.transform.position + world.StateBoundsMin));
            }
            finally { UnityEngine.Object.DestroyImmediate(bodyHost); UnityEngine.Object.DestroyImmediate(host); }
        }
    }
}
