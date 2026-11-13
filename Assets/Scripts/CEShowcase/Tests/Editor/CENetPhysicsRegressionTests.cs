using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UdonSharp.CE.NetPhysics;
using UnityEngine;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// Pins the CE.NetPhysics behaviour the shipped Rocket League arena depends on and that the audit
    /// tests in CEAuditRuntimeRegressionTests do not reach: the encoding box at the arena's real
    /// coordinates, non-finite state on the wire, entity ids that agree across clients, correction
    /// applied at the present rather than at the packet's frame, the input acknowledgement for the
    /// driven car, the ground probe, force units, and the input stream.
    /// </summary>
    /// <remarks>
    /// There is no ClientSim here, so
    /// Networking.LocalPlayer is null: everything is driven beneath the paths that need a player.
    /// The tests that lean on edit-mode physics -- raycasts, Rigidbody poses, accumulated forces --
    /// first check that the physics scene answers at all and report Inconclusive, not Failed, where
    /// it does not.
    /// </remarks>
    public class CENetPhysicsRegressionTests
    {
        // Well away from anything in whatever scene is open, so physics queries only see the fixtures.
        private static readonly Vector3 FixtureOrigin = new Vector3(5000f, 3000f, 5000f);

        private readonly List<GameObject> _createdObjects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                if (_createdObjects[i] != null)
                    UnityEngine.Object.DestroyImmediate(_createdObjects[i]);
            }

            _createdObjects.Clear();
        }

        // ------------------------------------------------------------------------------------------
        // State encoding
        // ------------------------------------------------------------------------------------------

        [Test]
        public void StateBounds_DefaultsCoverTheShippedArena()
        {
            // The arena's NetPhysicsWorld sits at the RocketLeagueArena root, 121.1 m down the z axis.
            // With the box fixed around the world origin, everything south of z = -100 -- the blue half,
            // its spawn and its goal -- arrived pinned to that plane on every client but the authority.
            NetPhysicsWorld world = CreateComponent<NetPhysicsWorld>("ArenaWorld");
            world.transform.position = new Vector3(0f, 0f, -121.1f);
            world.AutoSimulate = false;
            InvokePrivate(world, "Start");

            StateCompressor codec = GetField<StateCompressor>(world, "_stateCompressor");

            Vector3[] points =
            {
                new Vector3(-40f, 0f, -181.1f),  // floor corners
                new Vector3(40f, 0f, -61.1f),
                new Vector3(-40f, 10f, -61.1f),  // wall tops
                new Vector3(40f, 10f, -181.1f),
                new Vector3(0f, 0.5f, -161.1f),  // blue spawn
                new Vector3(0f, 0.5f, -81.1f),   // orange spawn
                new Vector3(0f, 2.5f, -179.1f),  // blue goal mouth
                new Vector3(0f, 2.5f, -63.1f),   // orange goal mouth
                new Vector3(0f, 1f, -121.1f),    // ball spawn
                // The arena has no ceiling. Jump, double jump and a full tank straight up peak about
                // 50 m up with the shipped car's drag and about 130 m up without any; a 50 m box
                // clamped that climb onto its lid.
                new Vector3(-12f, 50.5f, -100f),
                new Vector3(30f, 131.5f, -150f),
            };

            var state = new PhysicsSnapshot(points.Length) { Frame = 1, EntityCount = points.Length };
            for (int i = 0; i < points.Length; i++)
            {
                state.EntityIds[i] = i;
                state.Positions[i] = points[i];
                state.Rotations[i] = Quaternion.identity;
            }

            var decoded = new PhysicsSnapshot(points.Length);
            Assert.That(codec.Decompress(codec.Compress(state), decoded), Is.True);

            for (int i = 0; i < points.Length; i++)
            {
                Assert.That(Vector3.Distance(decoded.Positions[i], points[i]), Is.LessThan(0.01f),
                    $"{points[i]} did not survive the trip; it is outside the encoding box.");
            }
        }

        [Test]
        public void StateCompressor_NonFiniteState_IsReplacedInsteadOfHaltingTheSender()
        {
            // Under Udon a NaN position reached a narrowing Convert as int.MinValue and threw, halting
            // the authority's world. In C# the same cast wraps silently, so what is checked here is what
            // arrives: something sensible, not whatever the wrapped bits decode to.
            var codec = new StateCompressor { WorldMin = new Vector3(-100f, -10f, -100f), WorldMax = new Vector3(100f, 150f, 100f) };
            var state = new PhysicsSnapshot(3) { Frame = 9, EntityCount = 3 };
            for (int i = 0; i < 3; i++)
            {
                state.EntityIds[i] = i;
                state.Rotations[i] = Quaternion.identity;
            }

            state.Positions[0] = new Vector3(float.NaN, 1f, 2f);
            state.Velocities[0] = new Vector3(float.NaN, float.PositiveInfinity, float.NegativeInfinity);
            state.Positions[1] = new Vector3(float.PositiveInfinity, float.NegativeInfinity, 3f);
            state.Rotations[1] = new Quaternion(float.NaN, 0f, 0f, 1f);
            state.Positions[2] = new Vector3(4f, 5f, 6f);
            state.Rotations[2] = new Quaternion(float.PositiveInfinity, 0f, 0f, 1f);
            state.AngularVelocities[2] = new Vector3(0f, float.NaN, 0f);

            byte[] packet = null;
            Assert.DoesNotThrow(() => packet = codec.Compress(state));

            var decoded = new PhysicsSnapshot(3);
            Assert.That(codec.Decompress(packet, decoded), Is.True);

            // A NaN coordinate travels as the middle of its axis; the finite ones beside it are intact.
            Assert.That(decoded.Positions[0].x, Is.EqualTo(0f).Within(0.01f));
            Assert.That(decoded.Positions[0].y, Is.EqualTo(1f).Within(0.01f));
            Assert.That(decoded.Positions[0].z, Is.EqualTo(2f).Within(0.01f));

            // A NaN velocity is zero; an infinite one is the largest the wire holds, sign intact.
            Assert.That(decoded.Velocities[0].x, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(decoded.Velocities[0].y, Is.EqualTo(204.7f).Within(0.01f));
            Assert.That(decoded.Velocities[0].z, Is.EqualTo(-204.7f).Within(0.01f));

            // Infinite coordinates are clamped onto the faces of the box.
            Assert.That(decoded.Positions[1].x, Is.EqualTo(100f).Within(0.01f));
            Assert.That(decoded.Positions[1].y, Is.EqualTo(-10f).Within(0.01f));

            // A rotation with a NaN or infinite component arrives as no rotation, not as four numbers
            // that are not a rotation.
            Assert.That(Quaternion.Angle(decoded.Rotations[1], Quaternion.identity), Is.LessThan(0.1f));
            Assert.That(Quaternion.Angle(decoded.Rotations[2], Quaternion.identity), Is.LessThan(0.1f));
            Assert.That(Vector3.Distance(decoded.Positions[2], new Vector3(4f, 5f, 6f)), Is.LessThan(0.01f));
            Assert.That(decoded.AngularVelocities[2].y, Is.EqualTo(0f).Within(1e-4f));
        }

        // ------------------------------------------------------------------------------------------
        // Entity ids
        // ------------------------------------------------------------------------------------------

        [Test]
        public void EntityIds_AgreeWhateverOrderTheEntitiesRegisterIn()
        {
            // Ids used to go out in registration order, which is Start order, which is not the same on
            // every client: two clients could hold the ball and a car under each other's ids.
            int[] first = RegisterIdFixture("IdsFirstOrder", new[] { 3, 1, 2, 0 });
            int[] second = RegisterIdFixture("IdsSecondOrder", new[] { 0, 2, 1, 3 });

            // A, then A's child C, then B, by hierarchy; E pinned itself to 1, so the automatic ids step
            // over it.
            Assert.That(first, Is.EqualTo(new[] { 0, 2, 3, 1 }));
            Assert.That(second, Is.EqualTo(first));
        }

        [Test]
        public void EntityIds_ALateRegistrationRenumbersToTheSameResult_AndDropsStaleHistory()
        {
            NetPhysicsWorld world = CreateComponent<NetPhysicsWorld>("LateIdsWorld");
            world.AutoSimulate = false;
            InvokePrivate(world, "Start");

            GameObject root = CreateObject("LateIdsRoot");
            NetBall a = CreateChildEntity(root.transform, "A");
            NetBall c = CreateChildEntity(a.transform, "C");
            NetBall b = CreateChildEntity(root.transform, "B");

            world.RegisterEntity(b);
            world.RegisterEntity(a);
            world.SimulateSingleTick(InputFrame.Empty);
            Assert.That(new[] { a.EntityId, b.EntityId }, Is.EqualTo(new[] { 0, 1 }));
            Assert.That(world.GetHistory().Count, Is.EqualTo(1));

            // C starts late on this client. It still ends up where it would have been had it started
            // with the others, and B moves to make room -- so frames recorded under B's old id go.
            world.RegisterEntity(c);
            Assert.That(world.GetEntity(1), Is.SameAs(c));
            Assert.That(new[] { a.EntityId, c.EntityId, b.EntityId }, Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(world.GetHistory().Count, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------------------------------
        // History and correction
        // ------------------------------------------------------------------------------------------

        [Test]
        public void FrameHistory_AGapStartsANewWindowInsteadOfClaimingStaleSlots()
        {
            var history = new FrameHistory(4, 1);
            for (int frame = 10; frame < 14; frame++)
                history.Record(frame, Snapshot(frame, 1, new Vector3(frame, 0f, 0f), Vector3.zero), new InputFrame());

            history.Record(20, Snapshot(20, 1, new Vector3(20f, 0f, 0f), Vector3.zero), new InputFrame());

            Assert.That(history.Count, Is.EqualTo(1));
            Assert.That(history.OldestFrame, Is.EqualTo(20));
            Assert.That(history.NewestFrame, Is.EqualTo(20));
            Assert.That(history.GetState(13), Is.Null, "Frame 13's slot now belongs to the run that starts at 20.");
        }

        [Test]
        public void FrameHistory_OffsetEntity_MovesOnlyTheFramesFromTheCorrectionOn()
        {
            var history = new FrameHistory(8, 1);
            for (int frame = 10; frame < 14; frame++)
                history.Record(frame, Snapshot(frame, 4, new Vector3(frame, 0f, 0f), Vector3.zero), new InputFrame());

            int shifted = history.OffsetEntity(4, 12, Vector3.up, Quaternion.identity, Vector3.zero, Vector3.zero);

            Assert.That(shifted, Is.EqualTo(2));
            Assert.That(history.GetState(11).Positions[0], Is.EqualTo(new Vector3(11f, 0f, 0f)));
            Assert.That(history.GetState(12).Positions[0], Is.EqualTo(new Vector3(12f, 1f, 0f)));
            Assert.That(history.GetState(13).Positions[0], Is.EqualTo(new Vector3(13f, 1f, 0f)));
        }

        [Test]
        public void Reconcile_AppliesTheMeasuredErrorAtThePresent_AndOnlyOnce()
        {
            // A ball travelling at 12 m/s. The authority's state for frame 104 says this client had it
            // half a metre short. Restoring that state -- what the rewind-and-replay used to amount to,
            // since the replay moved no bodies -- would drag the ball back over a metre to where it was
            // six frames ago. The error belongs on the ball as it is now.
            NetBall ball = CreateEntity<NetBall>("ReconcileBall", 3);
            var history = new FrameHistory(32, 4);
            for (int frame = 100; frame <= 110; frame++)
                history.Record(frame, Snapshot(frame, 3, new Vector3(10f + 0.2f * (frame - 100), 0f, 0f), new Vector3(12f, 0f, 0f)), new InputFrame());

            ball.Position = new Vector3(12f, 0f, 0f);

            var correction = new RollbackManager { PositionThreshold = 0.05f, TimingSlackSteps = 0f };
            correction.Initialize(null, history);
            var entities = new NetPhysicsEntity[] { ball };

            correction.Reconcile(104, Snapshot(104, 3, new Vector3(11.3f, 0f, 0f), new Vector3(12f, 0f, 0f)), entities, 1, 111, 60, -1);

            Assert.That(correction.LastCorrectionCount, Is.EqualTo(1));
            Assert.That(ball.Position.x, Is.EqualTo(12.5f).Within(1e-3f));
            Assert.That(history.GetState(103).Positions[0].x, Is.EqualTo(10.6f).Within(1e-3f), "Frames before the measured one are left alone.");
            Assert.That(history.GetState(104).Positions[0].x, Is.EqualTo(11.3f).Within(1e-3f));
            Assert.That(history.GetState(110).Positions[0].x, Is.EqualTo(12.5f).Within(1e-3f));

            // The next packet agrees with the corrected prediction, so it must not correct again.
            correction.Reconcile(106, Snapshot(106, 3, new Vector3(11.7f, 0f, 0f), new Vector3(12f, 0f, 0f)), entities, 1, 112, 60, -1);

            Assert.That(correction.LastCorrectionCount, Is.EqualTo(0));
            Assert.That(ball.Position.x, Is.EqualTo(12.5f).Within(1e-3f));
        }

        [Test]
        public void Reconcile_WithoutHistory_CarriesTheReceivedPoseForwardToThePresent()
        {
            NetBall ball = CreateEntity<NetBall>("UnrecordedBall", 2);
            var correction = new RollbackManager { PositionThreshold = 0.05f, TimingSlackSteps = 0f, MaxRollbackFrames = 30 };
            correction.Initialize(null, new FrameHistory(16, 4));
            var entities = new NetPhysicsEntity[] { ball };
            PhysicsSnapshot received = Snapshot(200, 2, new Vector3(10f, 0f, 0f), new Vector3(6f, 0f, 0f));

            // Ten frames old at 60 Hz: one sixth of a second along a 6 m/s velocity.
            ball.Position = Vector3.zero;
            correction.Reconcile(200, received, entities, 1, 211, 60, -1);
            Assert.That(ball.Position.x, Is.EqualTo(11f).Within(1e-3f));

            // A stale packet is carried forward no further than MaxRollbackFrames.
            ball.Position = Vector3.zero;
            correction.Reconcile(200, received, entities, 1, 301, 60, -1);
            Assert.That(ball.Position.x, Is.EqualTo(13f).Within(1e-3f));
        }

        [Test]
        public void Reconcile_DrivenCar_IsComparedAtTheFrameOfTheInputTheAuthorityHadApplied()
        {
            // The driver's prediction runs a dozen frames of input ahead of the authority. Compared at
            // the same frame number an accelerating car reads as more than three metres wrong and was
            // pulled back on every packet; compared at the frame of the input the authority had applied
            // it is exactly right.
            NetVehicle car = CreateEntity<NetVehicle>("DrivenCar", 1);
            SetField(car, "_ownerPlayerId", 5);

            var history = new FrameHistory(64, 4);
            for (int frame = 100; frame <= 120; frame++)
            {
                float t = frame - 100;
                history.Record(frame, Snapshot(frame, 1, new Vector3(0.01f * t * t, 0f, 0f), Vector3.zero), new InputFrame());
            }

            var correction = new RollbackManager { PositionThreshold = 0.05f, TimingSlackSteps = 0f };
            correction.Initialize(null, history);
            var entities = new NetPhysicsEntity[] { car };

            PhysicsSnapshot lagged = Snapshot(120, 1, new Vector3(0.64f, 0f, 0f), Vector3.zero);
            lagged.InputLagFrames[0] = 12;

            car.Position = new Vector3(4f, 0f, 0f);
            correction.Reconcile(120, lagged, entities, 1, 121, 60, 5);
            Assert.That(correction.LastCorrectionCount, Is.EqualTo(0), "The driver's own car was corrected against a frame it had not reached.");
            Assert.That(car.Position.x, Is.EqualTo(4f).Within(1e-4f));

            // The same packet on any other client describes somebody else's car, compared as it stands.
            correction.Reconcile(120, lagged, entities, 1, 121, 60, 6);
            Assert.That(correction.LastCorrectionCount, Is.EqualTo(1));
            Assert.That(car.Position.x, Is.EqualTo(0.64f).Within(1e-3f));
        }

        [Test]
        public void Reconcile_DrivenCarWithoutAnInputFigure_IsOnlyCorrectedPastSnapDistance()
        {
            NetVehicle car = CreateEntity<NetVehicle>("UnackedCar", 1);
            SetField(car, "_ownerPlayerId", 5);

            var history = new FrameHistory(16, 4);
            history.Record(120, Snapshot(120, 1, new Vector3(4f, 0f, 0f), Vector3.zero), new InputFrame());

            var correction = new RollbackManager { PositionThreshold = 0.05f, TimingSlackSteps = 0f };
            correction.Initialize(null, history);
            var entities = new NetPhysicsEntity[] { car };

            car.Position = new Vector3(4f, 0f, 0f);

            PhysicsSnapshot unknown = Snapshot(120, 1, new Vector3(5f, 0f, 0f), Vector3.zero);
            unknown.InputLagFrames[0] = PhysicsSnapshot.UnknownInputLag;
            correction.Reconcile(120, unknown, entities, 1, 121, 60, 5);
            Assert.That(car.Position.x, Is.EqualTo(4f).Within(1e-4f), "A metre is the input delay talking, not an error.");

            unknown.Positions[0] = new Vector3(7f, 0f, 0f);
            correction.Reconcile(120, unknown, entities, 1, 121, 60, 5);
            Assert.That(car.Position.x, Is.EqualTo(7f).Within(1e-3f));
        }

        [Test]
        public void Tolerance_GrowsWithSpeed()
        {
            // Two clients sample frame N up to a physics step apart, which at 20 m/s is 0.4 m of travel.
            Assert.That(RollbackManager.CalculateTolerance(0.05f, 20f, 0f, 0.03f), Is.EqualTo(0.65f).Within(1e-5f));

            var correction = new RollbackManager { PositionThreshold = 0.05f };
            PhysicsSnapshot server = Snapshot(10, 1, new Vector3(0.3f, 0f, 0f), Vector3.zero);
            PhysicsSnapshot local = Snapshot(10, 1, Vector3.zero, Vector3.zero);
            Assert.That(correction.CalculateCorrection(server, 0, local, 0, 0.03f), Is.True, "At rest 0.3 m is an error.");

            server.Velocities[0] = new Vector3(20f, 0f, 0f);
            local.Velocities[0] = new Vector3(20f, 0f, 0f);
            Assert.That(correction.CalculateCorrection(server, 0, local, 0, 0.03f), Is.False, "At 20 m/s 0.3 m is sampling, not error.");
        }

        [Test]
        public void CorrectionBlender_FinishesOnABodyThatKeepsMoving()
        {
            // The old blender chased a fixed point and settled a fixed distance short of it on anything
            // moving. An offset is handed over whatever the body does in between.
            var blend = new CorrectionBlender { PositionBlendRate = 0.2f, RotationBlendRate = 0.2f };
            Quaternion turn = Quaternion.Euler(0f, 30f, 0f);
            blend.AddOffset(new Vector3(1.5f, 0f, 0f), turn);

            Vector3 body = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            int steps = 0;
            while (blend.HasOffset && steps < 200)
            {
                body += new Vector3(0.4f, 0f, 0f); // 20 m/s at 50 Hz
                body += blend.TakePositionStep();
                rotation = blend.TakeRotationStep() * rotation;
                steps++;
            }

            Assert.That(blend.HasOffset, Is.False);
            Assert.That(steps, Is.LessThan(60));
            Assert.That(body.x, Is.EqualTo(0.4f * steps + 1.5f).Within(1e-3f));
            Assert.That(Quaternion.Angle(rotation, turn), Is.LessThan(0.1f));
        }

        [Test]
        public void EntityCorrection_IsDroppedByTheAuthorityAndByATeleport()
        {
            NetBall ball = CreateEntity<NetBall>("BlendBall", 9);
            Rigidbody body = ball.gameObject.AddComponent<Rigidbody>();
            body.useGravity = false;
            SetField(ball, "_rb", body);
            ball.CorrectionSnapDistance = 2f;
            ball.CorrectionBlendRate = 0.2f;
            ball.Position = FixtureOrigin;
            Assume.That(Vector3.Distance(body.position, FixtureOrigin), Is.LessThan(1e-3f),
                "A Rigidbody pose written in edit mode does not read back in this editor.");

            ball.ApplyCorrectionDelta(new Vector3(0.5f, 0f, 0f), Quaternion.identity, Vector3.zero, Vector3.zero);
            Assert.That(ball.PendingCorrectionPosition.x, Is.EqualTo(0.5f).Within(1e-5f), "Half a metre is eased in, not jumped.");
            Assert.That(ball.CorrectedPosition.x, Is.EqualTo(FixtureOrigin.x + 0.5f).Within(1e-3f));

            // A client that has just become the authority must not keep easing toward its predecessor.
            InvokePrivate(ball, "StepCorrection", true);
            Assert.That(ball.HasPendingCorrection, Is.False);
            Assert.That(Vector3.Distance(ball.Position, FixtureOrigin), Is.LessThan(1e-3f));

            // Anywhere else one step hands over a fifth of it.
            ball.ApplyCorrectionDelta(new Vector3(0.5f, 0f, 0f), Quaternion.identity, Vector3.zero, Vector3.zero);
            InvokePrivate(ball, "StepCorrection", false);
            Assert.That(ball.Position.x - FixtureOrigin.x, Is.EqualTo(0.1f).Within(1e-3f));
            Assert.That(ball.PendingCorrectionPosition.x, Is.EqualTo(0.4f).Within(1e-4f));

            // A teleport drops the rest instead of dragging the body back toward the old place.
            Vector3 respawn = FixtureOrigin + new Vector3(0f, 3f, 0f);
            ball.Position = respawn;
            Assert.That(ball.PendingCorrectionPosition, Is.EqualTo(Vector3.zero));
            Assert.That(Vector3.Distance(ball.CorrectedPosition, respawn), Is.LessThan(1e-3f));
        }

        // ------------------------------------------------------------------------------------------
        // Scheduling
        // ------------------------------------------------------------------------------------------

        [Test]
        public void PriorityDebt_OneMoreEntityThanAPacketHolds_IsSentWithinABoundedWait()
        {
            // Eight busy entities and one slow car far from the authority, eight to a packet: ranking
            // by priority alone never sent the ninth at all.
            var priorities = new float[9];
            for (int i = 0; i < 8; i++)
                priorities[i] = 1f;
            priorities[8] = 0.05f;

            var debt = new float[9];
            var selection = new int[9];
            var prioritizer = new SyncPrioritizer();

            int firstSent = -1;
            for (int packet = 0; packet < 100 && firstSent < 0; packet++)
            {
                Assert.That(prioritizer.SelectForPacket(priorities, debt, 9, 8, selection), Is.EqualTo(8));
                for (int k = 0; k < 8; k++)
                {
                    if (selection[k] == 8)
                        firstSent = packet;
                }
            }

            Assert.That(firstSent, Is.InRange(0, 21), "The slow car's wait is bounded by how its priority compares, not by luck.");
        }

        // ------------------------------------------------------------------------------------------
        // Input
        // ------------------------------------------------------------------------------------------

        [Test]
        public void InputBuffer_UnknownOrInvalidPlayers_AreNeutralInsteadOfOutOfRange()
        {
            var buffer = new InputBuffer();

            Assert.That(buffer.DequeueInput(-1).Throttle, Is.EqualTo(0));
            Assert.That(buffer.DequeueInput(500).FrameNumber, Is.EqualTo(0));
            Assert.That(buffer.ConsumeInput(-1, 10).Throttle, Is.EqualTo(0));
        }

        [Test]
        public void InputPredictor_HoldsTheLastInputAcrossAnOrdinaryPacketGap()
        {
            var predictor = new InputPredictor();
            predictor.UpdateLastKnown(7, new InputFrame { FrameNumber = 10, Throttle = 127, Steering = -100 }, 1f);

            // A tenth of a second later the next packet is not even late, and the car was already being
            // driven at two thirds of the throttle its driver was holding.
            InputFrame held = predictor.Predict(7, 16, 1.1f);
            Assert.That(held.Throttle, Is.EqualTo(127));
            Assert.That(held.Steering, Is.EqualTo(-100));

            // Two seconds of silence is input gone missing.
            Assert.That(predictor.Predict(7, 130, 3f).Throttle, Is.LessThan(5));

            // A sender throttled to a packet every 0.4 s is held for one and a half of its intervals:
            // half a second after its last packet, the fixed hold alone would have it at a quarter.
            predictor.UpdateLastKnown(7, new InputFrame { FrameNumber = 160, Throttle = 127 }, 3.0f);
            predictor.UpdateLastKnown(7, new InputFrame { FrameNumber = 184, Throttle = 127 }, 3.4f);
            Assert.That(predictor.GetHoldSeconds(7), Is.EqualTo(0.6f).Within(1e-3f));
            Assert.That(predictor.Predict(7, 214, 3.9f).Throttle, Is.EqualTo(127));
        }

        [Test]
        public void InputPackets_CarryTwoSendIntervalsOfFrames()
        {
            // At the 5 Hz floor with a frame recorded every 20 ms, ten frames arrive between packets.
            // A fixed ten-frame packet overlapped nothing, so any one lost packet was a hole.
            Assert.That(InputRecorder.CalculatePacketFrameCount(0.2f, 0.02f, 10, 255), Is.GreaterThanOrEqualTo(20));

            // At the full 15 Hz the configured minimum already covers two intervals.
            Assert.That(InputRecorder.CalculatePacketFrameCount(1f / 15f, 0.02f, 10, 255), Is.EqualTo(10));

            // Never more than the recorder keeps.
            Assert.That(InputRecorder.CalculatePacketFrameCount(10f, 0.02f, 10, 64), Is.EqualTo(64));
        }

        [Test]
        public void InputRecorder_Disabled_PublishesNeutralFramesStampedAsUsual()
        {
            InputRecorder recorder = CreateComponent<InputRecorder>("NeutralRecorder");
            InvokePrivate(recorder, "Start");
            SetField(recorder, "_moveVertical", 1f);
            SetField(recorder, "_jumpHeld", true);
            SetField(recorder, "_boostHeld", true);

            recorder.InputEnabled = false;
            InvokePrivate(recorder, "ComposeInput", 321, 4f);
            InputFrame neutral = recorder.CurrentInput;
            Assert.That(neutral.FrameNumber, Is.EqualTo(321), "A disabled stream still has to keep flowing, frame by frame.");
            Assert.That(neutral.Throttle, Is.EqualTo(0));
            Assert.That(neutral.Buttons, Is.EqualTo(0));
            Assert.That(neutral.Boost, Is.EqualTo(0));

            recorder.InputEnabled = true;
            InvokePrivate(recorder, "ComposeInput", 322, 4.1f);
            Assert.That(recorder.CurrentInput.FrameNumber, Is.EqualTo(322));
            Assert.That(recorder.CurrentInput.Throttle, Is.EqualTo(127));
            Assert.That(recorder.CurrentInput.Buttons & InputFrame.BUTTON_BOOST, Is.Not.EqualTo(0));
        }

        // ------------------------------------------------------------------------------------------
        // Vehicle
        // ------------------------------------------------------------------------------------------

        [Test]
        public void VehicleClock_ABackwardResync_ReanchorsInsteadOfFreezingBoostAndTheDodgeWindow()
        {
            NetPhysicsWorld world = CreateComponent<NetPhysicsWorld>("ResyncWorld");
            world.AutoSimulate = false;
            NetVehicle car = CreateComponent<NetVehicle>("ResyncCar");
            car.World = world;

            SetField(world, "_currentFrame", 1000);
            InvokePrivate(car, "BeginSimulationStep");
            SetField(world, "_currentFrame", 1001);
            InvokePrivate(car, "BeginSimulationStep");

            // The world re-anchored its counter 31 frames back. That used to read as a replay, and every
            // tick until the counter climbed back to 1001 integrated nothing: free boost, a frozen timer.
            SetField(world, "_currentFrame", 970);
            InvokePrivate(car, "BeginSimulationStep");
            Assert.That(GetField<float>(car, "_integrationSeconds"), Is.EqualTo(1f / 60f).Within(1e-6f));

            SetField(world, "_currentFrame", 971);
            InvokePrivate(car, "BeginSimulationStep");
            Assert.That(GetField<float>(car, "_integrationSeconds"), Is.EqualTo(1f / 60f).Within(1e-6f));
        }

        [Test]
        public void VehicleStart_DerivesTheAirStateFromFlagsThatLandedBeforeIt()
        {
            // VRChat writes a late joiner's received values into the program before Start, but runs
            // OnDeserialization only once Start has. Start used to clear the air state outright, so a
            // late joiner's copy of a car that had just jumped lost the double jump its driver held.
            NetVehicle jumped = CreateComponent<NetVehicle>("LateJoinJumpedCar");
            SetField(jumped, "_airStateFlags", (byte)1); // jumped, double jump still held
            InvokePrivate(jumped, "Start");
            Assert.That(jumped.HasDoubleJump, Is.True);
            Assert.That(GetField<bool>(jumped, "_dodgeUsed"), Is.False);

            NetVehicle dodged = CreateComponent<NetVehicle>("LateJoinDodgedCar");
            SetField(dodged, "_airStateFlags", (byte)2); // dodge already spent
            InvokePrivate(dodged, "Start");
            Assert.That(dodged.HasDoubleJump, Is.False);
            Assert.That(GetField<bool>(dodged, "_dodgeUsed"), Is.True);
        }

        [Test]
        public void GroundProbe_FindsTheFloorUnderTheShippedCar_AndIgnoresTriggers()
        {
            BoxCollider floor = CreateFloor();
            NetVehicle car = CreateShippedCar(FixtureOrigin + new Vector3(0f, 0.25f, 0f));
            Rigidbody body = car.GetComponent<Rigidbody>();
            AssumePhysicsQueries();

            // The old probe started 0.1 m above the pivot and ran 0.2 m: it ended inside this car's own
            // collider, a quarter metre short of the floor, and the car was never grounded.
            InvokePrivate(car, "UpdateGroundedState", body);
            Assert.That(car.IsGrounded, Is.True);

            floor.isTrigger = true;
            Physics.SyncTransforms();
            InvokePrivate(car, "UpdateGroundedState", body);
            Assert.That(car.IsGrounded, Is.False, "A trigger is not ground.");
        }

        [Test]
        public void GroundProbe_ACarThatJustJumpedKeepsItsDoubleJumpWhileRising()
        {
            CreateFloor();
            NetVehicle car = CreateShippedCar(FixtureOrigin + new Vector3(0f, 0.25f, 0f));
            Rigidbody body = car.GetComponent<Rigidbody>();
            AssumePhysicsQueries();

            var jump = new InputFrame { Buttons = InputFrame.BUTTON_JUMP };
            car.TickInput(jump);
            Assert.That(car.HasDoubleJump, Is.True, "The jump did not fire from the ground.");

            // A second tick in the same physics step: the impulse has not moved anything yet.
            car.TickInput(jump);
            Assert.That(car.IsGrounded, Is.False);
            Assert.That(car.HasDoubleJump, Is.True);

            // A step later the car is rising but still within reach of the floor. Counting it as
            // grounded here is what used to wipe the double jump and the dodge the jump had granted.
            car.ApplyForces();
            body.velocity = new Vector3(0f, 2.5f, 0f);
            car.TickInput(jump);
            Assert.That(car.IsGrounded, Is.False);
            Assert.That(car.HasDoubleJump, Is.True);

            // Once it stops rising it has landed.
            car.ApplyForces();
            body.velocity = Vector3.zero;
            car.TickInput(new InputFrame());
            Assert.That(car.IsGrounded, Is.True);
            Assert.That(car.HasDoubleJump, Is.False);
        }

        [Test]
        public void VehicleForces_BoostIsNewtons_AppliedOncePerPhysicsStep()
        {
            // Airborne and far from anything, so only the boost acts.
            NetVehicle car = CreateShippedCar(FixtureOrigin + new Vector3(0f, 50f, 0f));
            Rigidbody body = car.GetComponent<Rigidbody>();
            AssumeAccumulatedForces();

            SetField(car, "_boostRemaining", 2f);
            var boost = new InputFrame { Buttons = InputFrame.BUTTON_BOOST, Boost = 255 };

            // Two ticks landing in one physics step. Boost used to be an acceleration added once per
            // tick: 5000 m/s^2 on a 180 kg car, twice.
            car.TickInput(boost);
            car.TickInput(boost);
            car.ApplyForces();

            Vector3 force = body.GetAccumulatedForce(Time.fixedDeltaTime);
            Assert.That(force.z, Is.EqualTo(car.MaxBoost).Within(1f));
            Assert.That(force.x, Is.EqualTo(0f).Within(1f));
            Assert.That(force.y, Is.EqualTo(0f).Within(1f));
        }

        [Test]
        public void VehicleForces_ThrottleIsNewtons_AppliedOncePerPhysicsStep()
        {
            CreateFloor();
            NetVehicle car = CreateShippedCar(FixtureOrigin + new Vector3(0f, 0.25f, 0f));
            Rigidbody body = car.GetComponent<Rigidbody>();
            AssumePhysicsQueries();
            AssumeAccumulatedForces();

            // Full throttle from rest, two ticks landing in one physics step. The drive force used to be
            // an acceleration added per tick: 2000 m/s^2, twice, where 11 m/s^2 was meant.
            var throttle = new InputFrame { Throttle = 127 };
            car.TickInput(throttle);
            car.TickInput(throttle);
            Assert.That(car.IsGrounded, Is.True, "Throttle only drives a grounded car.");
            car.ApplyForces();

            Vector3 force = body.GetAccumulatedForce(Time.fixedDeltaTime);
            Assert.That(force.z, Is.EqualTo(car.MaxThrottle).Within(1f));

            // A physics step with no tick still drives: the driver is still holding the throttle.
            car.ApplyForces();
            Assert.That(body.GetAccumulatedForce(Time.fixedDeltaTime).z, Is.EqualTo(2f * car.MaxThrottle).Within(2f));
        }

        // ------------------------------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------------------------------

        private static PhysicsSnapshot Snapshot(int frame, int entityId, Vector3 position, Vector3 velocity)
        {
            var state = new PhysicsSnapshot(1) { Frame = frame, EntityCount = 1 };
            state.EntityIds[0] = entityId;
            state.Positions[0] = position;
            state.Rotations[0] = Quaternion.identity;
            state.Velocities[0] = velocity;
            return state;
        }

        /// <summary>
        /// Under a fresh root: A, A's child C, then B and E, with E pinned to id 1. Registers them with a
        /// new world in the given order (indices into A, C, B, E) and returns their ids as A, C, B, E.
        /// </summary>
        private int[] RegisterIdFixture(string rootName, int[] order)
        {
            GameObject root = CreateObject(rootName);
            NetBall a = CreateChildEntity(root.transform, "A");
            NetBall c = CreateChildEntity(a.transform, "C");
            NetBall b = CreateChildEntity(root.transform, "B");
            NetBall e = CreateChildEntity(root.transform, "E");
            SetField(e, "_entityId", 1);

            var entities = new NetPhysicsEntity[] { a, c, b, e };
            NetPhysicsWorld world = CreateComponent<NetPhysicsWorld>(rootName + "World");
            for (int i = 0; i < order.Length; i++)
                world.RegisterEntity(entities[order[i]]);

            Assert.That(world.GetEntity(0), Is.SameAs(a), "A lookup by the agreed id finds the entity.");
            return new[] { a.EntityId, c.EntityId, b.EntityId, e.EntityId };
        }

        private NetBall CreateChildEntity(Transform parent, string name)
        {
            GameObject child = CreateObject(name);
            child.transform.SetParent(parent, false);
            return child.AddComponent<NetBall>();
        }

        private T CreateEntity<T>(string name, int entityId) where T : NetPhysicsEntity
        {
            T entity = CreateComponent<T>(name);
            SetField(entity, "_entityId", entityId);
            return entity;
        }

        /// <summary>
        /// The shipped Vehicle prefab's body: a unit box scaled to 1.8 x 0.5 x 3.5 on a 180 kg body.
        /// </summary>
        private NetVehicle CreateShippedCar(Vector3 position)
        {
            GameObject root = CreateObject("ShippedCar");
            root.transform.position = position;
            root.transform.localScale = new Vector3(1.8f, 0.5f, 3.5f);
            root.AddComponent<BoxCollider>();

            Rigidbody body = root.AddComponent<Rigidbody>();
            body.mass = 180f;
            body.useGravity = false;

            NetVehicle car = root.AddComponent<NetVehicle>();
            SetField(car, "_rb", body);
            Physics.SyncTransforms();
            return car;
        }

        /// <summary>
        /// A 20 m square slab whose top face is at the fixture origin.
        /// </summary>
        private BoxCollider CreateFloor()
        {
            GameObject floor = CreateObject("ProbeFloor");
            floor.transform.position = FixtureOrigin + new Vector3(0f, -0.5f, 0f);
            BoxCollider box = floor.AddComponent<BoxCollider>();
            box.size = new Vector3(20f, 1f, 20f);
            Physics.SyncTransforms();
            return box;
        }

        private static void AssumePhysicsQueries()
        {
            Physics.SyncTransforms();
            Assume.That(Physics.Raycast(FixtureOrigin + new Vector3(5f, 5f, 0f), Vector3.down, 10f), Is.True,
                "Physics queries do not see edit-mode colliders in this editor.");
        }

        private void AssumeAccumulatedForces()
        {
            GameObject host = CreateObject("ForceControl");
            host.transform.position = FixtureOrigin + new Vector3(0f, 200f, 0f);
            Rigidbody control = host.AddComponent<Rigidbody>();
            control.useGravity = false;
            control.AddForce(Vector3.up * 100f, ForceMode.Force);
            Assume.That(control.GetAccumulatedForce(Time.fixedDeltaTime).y, Is.EqualTo(100f).Within(0.5f),
                "Accumulated forces cannot be read in this editor's physics scene.");
        }

        private T CreateComponent<T>(string name) where T : Component
        {
            return CreateObject(name).AddComponent<T>();
        }

        private GameObject CreateObject(string name)
        {
            var go = new GameObject(name);
            _createdObjects.Add(go);
            return go;
        }

        private static void InvokePrivate(object instance, string methodName, params object[] args)
        {
            MethodInfo method = FindMethod(instance.GetType(), methodName);
            if (method == null)
                throw new MissingMethodException(instance.GetType().Name, methodName);

            method.Invoke(instance, args);
        }

        private static T GetField<T>(object instance, string fieldName)
        {
            return (T)FindField(instance.GetType(), fieldName).GetValue(instance);
        }

        private static void SetField(object instance, string fieldName, object value)
        {
            FindField(instance.GetType(), fieldName).SetValue(instance, value);
        }

        // Private members of a base class are not visible through the derived type, so walk up.
        private static FieldInfo FindField(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }

            throw new MissingFieldException(type.Name, name);
        }

        private static MethodInfo FindMethod(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                MethodInfo method = current.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (method != null)
                    return method;
            }

            return null;
        }
    }
}
