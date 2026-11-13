using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CEShowcase.Core;
using CEShowcase.Station1_BulletHell;
using CEShowcase.Station2_Flocking;
using CEShowcase.Station3_Leaderboard;
using CEShowcase.Station4_Cutscene;
using CEShowcase.Station5_Persistence;
using CEShowcase.Station6_Procgen;
using CEShowcase.Station7_Networking;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;
using UdonSharp.CE.Data;
using UdonSharp.CE.NetPhysics;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.UdonNetworkCalling;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// Multiplayer and logic defects a visitor to the showcase world would hit, each pinned at the level
    /// edit mode can reach. There is no ClientSim here, so <c>Networking.LocalPlayer</c> is null: every
    /// test drives the logic underneath a networked entry point and stays clear of anything that would
    /// ask VRChat who owns what.
    ///
    /// Rocket League lives in its own assembly, which this test assembly does not reference, so those
    /// behaviours are reached by type name. That is deliberate rather than lazy: a reference would make
    /// every showcase test depend on the arena compiling.
    /// </summary>
    public class CEShowcaseGameplayRegressionTests
    {
        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null)
                    UnityEngine.Object.DestroyImmediate(_created[i]);
            }

            _created.Clear();
        }

        // -----------------------------------------------------------------------------------------
        // Station 3: one writer for the leaderboard
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// A ScoreButton used to call AddScore on the presser's own client. Only the board's owner can
        /// publish, so for everyone else the points moved their own screen, went nowhere, and were wiped
        /// by the owner's next publish. Scoring has to reach the owner as a request it credits to the
        /// sender. The request itself needs a live network, so what is pinned is its shape: the button
        /// asks, and the owner's entry point is network-callable with the amount as its only argument.
        /// </summary>
        [Test]
        public void ScoreButton_AsksTheBoardOwnerToScore_InsteadOfWritingTheBoardItself()
        {
            MethodInfo ownerEntry = typeof(LeaderboardManager).GetMethod(
                "OwnerAddScore", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(int) }, null);

            Assert.That(ownerEntry, Is.Not.Null,
                "The board needs an entry point that runs on its owner and takes nothing but the amount; the " +
                "owner credits whoever sent it rather than a player id from the payload.");
            Assert.That(ownerEntry.GetCustomAttribute<NetworkCallableAttribute>(), Is.Not.Null,
                "A network event with a parameter only reaches a method marked [NetworkCallable].");

            MethodDeclarationSyntax interact = ParseMethod(
                "Assets/Scripts/CEShowcase/Station3_Leaderboard/ScoreButton.cs", "Interact");
            List<string> called = InvokedNames(interact);

            Assert.That(called, Does.Contain("RequestScore"),
                "The button must route through the board's single-writer request.");
            Assert.That(called, Does.Not.Contain("AddScore"),
                "AddScore is the owner's half of scoring. Called from the button it only ever worked for the " +
                "player who happened to own the board.");
        }

        /// <summary>
        /// The JSON demo loaded its sample over the live index, and the sample's id 1 is a real player
        /// id -- the first player into every instance -- so that player was handed 9999 points on the
        /// next publish. The reset demo zeroed the live index the same way and wiped everyone's score.
        /// Both now run on a scratch roster; the stress test's own isolation is pinned in
        /// CEShowcaseGameplayRegressionTests.LeaderboardStressTest_PreservesTheLiveRoster.
        /// </summary>
        [Test]
        public void LeaderboardDemos_JsonLoadAndResetPreview_LeaveTheLiveRosterAlone()
        {
            LeaderboardManager board = Create<LeaderboardManager>("LeaderboardUnderTest");
            InvokePrivate(board, "InitializeCollections");
            InvokePrivate(board, "AddPlayer", 1, "FirstJoiner");

            var scores = GetField<CEDictionary<int, int>>(board, "_playerScores");
            scores[1] = 40;

            board.OnLoadFromJson();

            Assert.That(GetField<CEDictionary<int, int>>(board, "_playerScores"), Is.SameAs(scores),
                "The live index must be back in place once the demo is done.");
            Assert.That(board.GetPlayerScore(1), Is.EqualTo(40),
                "The sample JSON names player 1; loading it must not rewrite a real player's score.");
            Assert.That(board.GetPlayerName(1), Is.EqualTo("FirstJoiner"));
            Assert.That(GetField<int>(board, "_playerCount"), Is.EqualTo(1));

            board.OnResetScores();

            Assert.That(board.GetPlayerScore(1), Is.EqualTo(40),
                "The reset demo is a local preview. Zeroing the live index wiped the instance's scores on " +
                "this client's next publish.");
            Assert.That(GetField<int>(board, "_playerCount"), Is.EqualTo(1));
        }

        // -----------------------------------------------------------------------------------------
        // Station 1: the dormant pool
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The other half of the lazy pool (CEShowcaseGameplayRegressionTests.UnusedBulletPool_
        /// DoesNotAllocateOnStart covers an untouched one): a pool that is used still has to build itself,
        /// and its bookkeeping must not report the empty free stack of an unbuilt pool as bullets in use.
        /// </summary>
        [Test]
        public void BulletPool_BuildsOnFirstAcquire_AndCountsWhatIsHandedOut()
        {
            BulletPool pool = Create<BulletPool>("BulletPoolUnderTest");
            GameObject template = CreateObject("BulletTemplate");
            SetField(pool, "_bulletPrefab", template);
            SetField(pool, "_poolSize", 4);

            Assert.That(pool.GetActiveCount(), Is.EqualTo(0),
                "An untouched pool has handed nothing out, whatever its free stack says.");

            int index = pool.AcquireBullet(Vector3.one);

            Assert.That(index, Is.InRange(0, 3), "The first acquire must build the pool and hand out a bullet.");
            Assert.That(pool.transform.childCount, Is.EqualTo(4));
            Assert.That(pool.GetActiveCount(), Is.EqualTo(1));

            pool.ReleaseBullet(index);
            Assert.That(pool.GetActiveCount(), Is.EqualTo(0));
        }

        // -----------------------------------------------------------------------------------------
        // Station 4: stage lights and the cutscene buttons
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The stage lights used to be kept off by disabling them, and DemoManager owns that flag: showing
        /// a station switches on every Light under it. Hiding and showing the theater -- or DemoManager
        /// simply starting after TheaterEffects -- left both lights burning at full intensity over an idle
        /// stage. Dark has to be an intensity.
        /// </summary>
        [Test]
        public void StageLights_StayDarkWhenTheStationIsShown_AndOnlyThePulseRaisesThem()
        {
            GameObject stationRoot = CreateObject("Station4Root");
            TheaterEffects effects = Create<TheaterEffects>("TheaterEffectsUnderTest");
            effects.transform.SetParent(stationRoot.transform, false);

            Light stageLight = CreateObject("StageLight").AddComponent<Light>();
            stageLight.transform.SetParent(stationRoot.transform, false);
            stageLight.intensity = 3f;
            stageLight.enabled = false; // as authored in CEShowcase.unity

            SetField(effects, "_stageLights", new[] { stageLight });
            SetField(effects, "_lightIntensity", 3f);

            InvokePrivate(effects, "Start");

            DemoManager demoManager = Create<DemoManager>("DemoManagerUnderTest");
            InvokePrivate(demoManager, "SetStationVisible", stationRoot, false);
            InvokePrivate(demoManager, "SetStationVisible", stationRoot, true);

            Assert.That(stageLight.enabled, Is.True, "DemoManager's sweep owns the enabled flag.");
            Assert.That(stageLight.intensity, Is.EqualTo(0f),
                "Showing the station must not light an idle stage.");

            effects.StartStageLights();
            InvokePrivate(effects, "UpdateStageLights");
            Assert.That(stageLight.intensity, Is.GreaterThan(0f), "The spotlight sequence has to bring them up.");

            effects.StopStageLights();
            Assert.That(stageLight.intensity, Is.EqualTo(0f));
            Assert.That(stageLight.enabled, Is.True,
                "Stopping must not touch the flag DemoManager owns, or a hidden station's lights come back on.");
        }

        /// <summary>
        /// A demo button pressed while another sequence was running cancelled it and started its own
        /// sequence in the same press. A cancelled state machine only unwinds when it next resumes, so the
        /// generated guard refused the new start -- the old sequence died, nothing replaced it, and the
        /// stage was left half-set. While a sequence is running every button is busy.
        /// </summary>
        [Test]
        public void CutsceneButtons_WhileASequenceIsRunning_NeitherCancelItNorStartAnother()
        {
            CutsceneController cutscene = Create<CutsceneController>("CutsceneUnderTest");
            var running = new UdonSharp.CE.Async.CancellationTokenSource();
            SetField(cutscene, "_cancellation", running);
            SetField(cutscene, "_sequenceRunning", true);

            cutscene.OnDemoFade();
            cutscene.OnDemoCurtains();
            cutscene.OnDemoSpotlight();
            cutscene.OnPlayCutscene();

            Assert.That(running.IsCancellationRequested, Is.False,
                "A button pressed mid-sequence must not cancel the sequence that is running.");
            Assert.That(GetField<object>(cutscene, "_cancellation"), Is.SameAs(running),
                "Nothing new may start until the running sequence has let go of its state machine.");
        }

        /// <summary>
        /// The busy state is only safe if it ends: the wrapper every button starts has to release it once
        /// the sequence's state machine completes, and a reset issued while one is still unwinding must
        /// not show a play button whose press would be refused. With nothing wired, every step of a demo
        /// is a plain delay, which edit-mode C# resumes immediately, so the demo runs to completion here.
        /// </summary>
        [Test]
        public void CutsceneSequence_ReleasesTheButtonsOnlyOnceItHasFinished()
        {
            CutsceneController cutscene = Create<CutsceneController>("CutsceneUnderTest");
            GameObject playButton = CreateObject("PlayButton");
            SetField(cutscene, "_playButton", playButton);

            cutscene.OnDemoFade();

            Assert.That(GetField<bool>(cutscene, "_sequenceRunning"), Is.False,
                "A finished sequence must release the buttons, or the station is dead for the session.");
            Assert.That(playButton.activeSelf, Is.True);

            SetField(cutscene, "_sequenceRunning", true);
            cutscene.OnResetCutscene();

            Assert.That(playButton.activeSelf, Is.False,
                "A cancelled sequence is still unwinding; its completion shows the button, not the reset.");
        }

        // -----------------------------------------------------------------------------------------
        // Station 5: achievements
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The achievement checks were one else-if chain, so a pickup could grant at most one. A first
        /// pickup that was also a rare earned "First Discovery", and "First Rare Find!" turned up on the
        /// next pickup -- or never, and then it was never saved either. Two unlocks share one popup, so
        /// the second has to wait its turn instead of overwriting the first.
        /// </summary>
        [Test]
        public void Achievements_OnePickupCanUnlockSeveral_AndTheirPopupsQueue()
        {
            InventorySystem inventory = Create<InventorySystem>("InventoryUnderTest");
            SetField(inventory, "_maxSlots", 4);
            InvokePrivate(inventory, "InitializeInventory");

            GameObject popup = CreateObject("AchievementPopup");
            popup.SetActive(false);
            var popupText = CreateObject("AchievementPopupText").AddComponent<TMPro.TextMeshProUGUI>();
            SetField(inventory, "_achievementPopup", popup);
            SetField(inventory, "_achievementPopupText", popupText);

            Assert.That(inventory.AddItem(2), Is.True, "Guard: item 2 is the Ruby, a rare.");

            Assert.That(GetField<bool>(inventory, "_achieveFirstItem"), Is.True);
            Assert.That(GetField<bool>(inventory, "_achieveFirstRare"), Is.True,
                "The first pickup was also the first rare; both are earned by it.");

            Assert.That(popup.activeSelf, Is.True);
            Assert.That(popupText.text, Does.Contain("First Discovery"));
            Assert.That(GetField<int>(inventory, "_achievementPopupQueueCount"), Is.EqualTo(1),
                "The second unlock waits behind the popup on screen.");

            InvokePrivate(inventory, "AdvanceAchievementPopup", 3.5f);
            Assert.That(popup.activeSelf, Is.True, "The queued popup takes over rather than the popup closing.");
            Assert.That(popupText.text, Does.Contain("First Rare Find"));
            Assert.That(GetField<int>(inventory, "_achievementPopupQueueCount"), Is.EqualTo(0));

            InvokePrivate(inventory, "AdvanceAchievementPopup", 3.5f);
            Assert.That(popup.activeSelf, Is.False, "With nothing left waiting, the popup goes away.");
        }

        // -----------------------------------------------------------------------------------------
        // Late joiners: Start must not overwrite a delivery it cannot see arrive
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Synced values can be delivered before Start, and VRChat drops the OnDeserialization for a
        /// behaviour that has not started -- so a late joiner's dug site never armed its respawn timer.
        /// Fine until that client inherits the site from a departing owner: nobody else is allowed to
        /// bring it back, so it stayed dug for the rest of the instance.
        /// </summary>
        [Test]
        public void DigSite_StartedAlreadyDug_ArmsItsRespawnTimer()
        {
            DigSite site = Create<DigSite>("DigSiteUnderTest");
            SetField(site, "_isDug", true);
            SetField(site, "_respawnTime", 30f);

            InvokePrivate(site, "Start");

            Assert.That(GetField<float>(site, "_respawnTimer"), Is.EqualTo(30f));
        }

        /// <summary>
        /// NetworkingDemo.Start seeded its synced position and its [SyncOnJoin] message log on every
        /// client. On a late joiner the owner's values were already in place, and with the
        /// OnDeserialization dropped there was nothing to put them back: the moved object snapped home and
        /// the log restarted at "Chat initialized". Only the owner seeds.
        /// </summary>
        [Test]
        public void NetworkingDemo_StartOnANonOwner_KeepsWhatTheOwnerAlreadyDelivered()
        {
            NetworkingDemo demo = Create<NetworkingDemo>("NetworkingDemoUnderTest");
            Transform movable = CreateObject("MovableObject").transform;
            movable.SetParent(demo.transform, false);
            movable.localPosition = new Vector3(0f, 1f, 0f);

            var deliveredLog = new string[10];
            deliveredLog[0] = "delivered by the owner";

            SetField(demo, "_movableObject", movable);
            SetField(demo, "_syncedPosition", new Vector3(2f, 1f, -3f));
            SetField(demo, "_messageLog", deliveredLog);
            SetField(demo, "_messageCount", 1);

            // No local player here, so this client is not the owner.
            InvokePrivate(demo, "Start");

            Assert.That(GetField<Vector3>(demo, "_syncedPosition"), Is.EqualTo(new Vector3(2f, 1f, -3f)));
            Assert.That(GetField<int>(demo, "_messageCount"), Is.EqualTo(1));
            Assert.That(GetField<string[]>(demo, "_messageLog")[0], Is.EqualTo("delivered by the owner"));
        }

        /// <summary>
        /// The chat event used to carry nothing: receivers read the text from a synced field and the
        /// sender from the object's owner, and neither a serialization nor an ownership transfer is
        /// ordered against a network event -- a client that heard the event first printed the previous
        /// message under whoever had sent that one. And every client appended to a log that [SyncOnJoin]
        /// also replicated whole from the owner, so lines doubled or vanished. Now both travel on the
        /// event and only the owner writes the log.
        /// </summary>
        [Test]
        public void NetworkingDemoChat_CarriesSenderAndText_AndOnlyTheOwnerWritesTheLog()
        {
            NetworkingDemo demo = Create<NetworkingDemo>("NetworkingDemoUnderTest");
            MethodInfo receive = typeof(NetworkingDemo).GetMethod(
                "ReceiveChatMessage", BindingFlags.Instance | BindingFlags.Public, null,
                new[] { typeof(string), typeof(string) }, null);

            Assert.That(receive, Is.Not.Null, "Sender and text must travel on the event itself.");

            SetField(demo, "_syncedMessage", "an older message");

            receive.Invoke(demo, new object[] { "Alice", "hi" });
            Assert.That(GetField<int>(demo, "_messageCount"), Is.EqualTo(0),
                "A non-owner shows the log the owner delivers; appending its own copy doubled every line.");

            SetField(demo, "_isOwner", true);
            receive.Invoke(demo, new object[] { "Alice", "hi" });

            string line = GetField<string[]>(demo, "_messageLog")[0];
            Assert.That(GetField<int>(demo, "_messageCount"), Is.EqualTo(1));
            Assert.That(line, Does.Contain("Alice").And.Contain("hi"));
            Assert.That(line, Does.Not.Contain("an older message"));
            Assert.That(GetField<string>(demo, "_syncedMessage"), Is.EqualTo("hi"),
                "The owner keeps the last message as synced state for later arrivals.");
        }

        // -----------------------------------------------------------------------------------------
        // Rocket League: the roster
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// With AutoAssignTeams off, a joiner was put on blue without being counted, so the spawn lookup
        /// asked for blue spawn point "count - 1" = -1. C#'s % keeps the sign of its left operand, so
        /// against two or more spawn points that is an out-of-range read -- which halts the spawner, and
        /// with it every later join and leave.
        /// </summary>
        [Test]
        public void Spawner_WithTeamsNotAutoAssigned_CountsTheJoiner_AndNeverIndexesBelowZero()
        {
            Component spawner = CreateRocketLeague("PlayerSpawner");
            Transform first = CreateObject("BlueSpawnA").transform;
            Transform second = CreateObject("BlueSpawnB").transform;
            SetField(spawner, "BlueSpawnPoints", new[] { first, second });
            SetField(spawner, "AutoAssignTeams", false);

            Assert.That(InvokePrivate(spawner, "GetSpawnPointForTeam", 0, -1), Is.SameAs(first),
                "A negative spawn index must clamp, not read out of range.");

            int team = (int)InvokePrivate(spawner, "AssignTeamToSlot", 0);

            Assert.That(team, Is.EqualTo(0), "Without auto-assignment everyone defaults to blue.");
            Assert.That(GetField<int>(spawner, "_bluePlayerCount"), Is.EqualTo(1),
                "The joiner has to be counted, or the next spawn lookup asks for index -1.");
            Assert.That(InvokePrivate(spawner, "GetSpawnIndexForTeam", 0), Is.EqualTo(0));
        }

        /// <summary>
        /// Vehicles were only handed out inside OnPlayerJoined, so a player who arrived to an empty pool
        /// stayed on foot for good: the car a departing driver left behind sat hidden until somebody new
        /// joined and was given it instead. Handing it over needs a live player lookup; what is pinned here
        /// is the bookkeeping that finds who is waiting.
        /// </summary>
        [Test]
        public void Spawner_FindsThePlayersStillWaitingForAVehicle()
        {
            Component spawner = CreateRocketLeague("PlayerSpawner");

            int[] slotPlayerIds = Filled(16, -1);
            int[] vehicleIndices = Filled(16, -1);
            slotPlayerIds[0] = 7;
            vehicleIndices[0] = 0;   // driving
            slotPlayerIds[2] = 9;    // waiting
            slotPlayerIds[5] = 11;   // waiting

            SetField(spawner, "_slotPlayerIds", slotPlayerIds);
            SetField(spawner, "_playerVehicleIndices", vehicleIndices);

            Assert.That(InvokePrivate(spawner, "FindSlotWaitingForVehicle", 0), Is.EqualTo(2));
            Assert.That(InvokePrivate(spawner, "FindSlotWaitingForVehicle", 3), Is.EqualTo(5),
                "Skipping a waiting player who cannot be resolved must move on to the next one.");
            Assert.That(InvokePrivate(spawner, "FindSlotWaitingForVehicle", 6), Is.EqualTo(-1));
        }

        /// <summary>
        /// A driver is named when they join the instance, and the recorder on their car reads VRChat's
        /// ordinary movement callbacks wherever the player is standing. So the first player in drove the
        /// car from the hub: walking to a station steered it and every button click spent its boost. The
        /// roster gates every pool car's input on the local player being in the arena, and the gate starts
        /// closed.
        /// </summary>
        [Test]
        public void Spawner_KeepsCarInputOff_UntilTheLocalPlayerIsInTheArena()
        {
            GameObject car = CreateObject("PoolCar");
            NetVehicle vehicle = car.AddComponent<NetVehicle>();
            InputRecorder recorder = car.AddComponent<InputRecorder>();
            Assume.That(recorder.InputEnabled, Is.True, "Guard: the library's default is to listen.");

            Component spawner = CreateRocketLeague("PlayerSpawner");
            SetField(spawner, "VehiclePool", new[] { vehicle });

            // Every client applies the roster at Start; with no local player this one owns nothing.
            InvokePrivate(spawner, "Start");
            Assert.That(recorder.InputEnabled, Is.False,
                "A car must not listen to a driver who has not come to the arena.");

            InvokePrivate(spawner, "SetLocalPlayerInArena", true);
            Assert.That(recorder.InputEnabled, Is.True);

            InvokePrivate(spawner, "SetLocalPlayerInArena", false);
            Assert.That(recorder.InputEnabled, Is.False, "Leaving for the hub takes the player's hands off it.");
        }

        /// <summary>
        /// The return pad is what closes that gate again, and CEShowcase.unity does not wire it to the
        /// roster: it finds the spawner that sits beside it under the arena's gameplay root. Without that
        /// a player who went back to the hub kept steering the car from there.
        /// </summary>
        [Test]
        public void HubPad_WithNothingWired_FindsTheRosterBesideIt()
        {
            GameObject gameplay = CreateObject("Gameplay");
            Component spawner = CreateRocketLeague("PlayerSpawner");
            spawner.transform.SetParent(gameplay.transform, false);
            Component pad = CreateRocketLeague("HubTeleporter");
            pad.transform.SetParent(gameplay.transform, false);

            Assert.That(InvokePrivate(pad, "ResolveSpawner"), Is.SameAs(spawner));
        }

        // -----------------------------------------------------------------------------------------
        // Rocket League: the HUD follows the synced state
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The match overlays used to be raised by network events and never lowered: Game Over and
        /// Overtime stayed up through the next match, a golden goal ended the game without Game Over at
        /// all, the end-of-regulation whistle raised "ORANGE WINS!" over a tie going to overtime, and a
        /// late joiner -- who is never sent an event that already fired -- saw none of it. Every overlay
        /// is now raised and lowered by the state alone.
        /// </summary>
        [Test]
        public void Hud_PutsEveryOverlayWhereTheMatchStateSays()
        {
            Component hud = CreateRocketLeague("RocketLeagueHUD");
            GameObject countdown = Panel(hud, "CountdownPanel");
            GameObject overtime = Panel(hud, "OvertimePanel");
            GameObject gameOver = Panel(hud, "GameOverPanel");
            GameObject goal = Panel(hud, "GoalScoredPanel");

            ApplyMatchState(hud, "Countdown");
            AssertOverlays("Countdown", new[] { countdown }, new[] { overtime, gameOver });

            goal.SetActive(true);
            ApplyMatchState(hud, "Overtime");
            AssertOverlays("Overtime", new[] { overtime }, new[] { countdown, gameOver });
            Assert.That(goal.activeSelf, Is.False, "The goal banner comes down once play has moved on.");

            ApplyMatchState(hud, "GameOver");
            AssertOverlays("GameOver", new[] { gameOver }, new[] { countdown, overtime });

            ApplyMatchState(hud, "Lobby");
            AssertOverlays("Lobby", new GameObject[0], new[] { countdown, overtime, gameOver });
        }

        [Test]
        public void Hud_WithNothingWired_TakesEveryMatchStateQuietly()
        {
            Component hud = CreateRocketLeague("RocketLeagueHUD");
            Type stateType = RocketLeagueType("GameState");

            foreach (string state in Enum.GetNames(stateType))
            {
                Assert.DoesNotThrow(() => ApplyMatchState(hud, state),
                    "An unwired HUD panel is optional; a null here would halt the HUD for the session.");
            }

            Component manager = CreateRocketLeague("RocketLeagueManager");
            SetField(manager, "_state", StateValue("GameOver"));
            Assert.DoesNotThrow(() => InvokePrivate(manager, "OnDeserialization"),
                "A manager with no HUD wired has to accept state without touching one.");
        }

        /// <summary>
        /// The HUD was drawn only from OnDeserialization, which never runs on the client that wrote the
        /// state -- so the owner watched 0-0 and a frozen clock for the whole match. Every client draws
        /// from the synced state: remotes on delivery, the owner on its own writes.
        /// </summary>
        [Test]
        public void MatchManager_DrawsTheHudFromState_OnDeliveryAndOnTheOwnersOwnWrites()
        {
            Component manager = CreateRocketLeague("RocketLeagueManager");
            Component hud = CreateRocketLeague("RocketLeagueHUD");
            GameObject gameOver = Panel(hud, "GameOverPanel");
            TMPro.TextMeshProUGUI winner = Text(hud, "WinnerText");
            SetField(manager, "HUD", hud);

            // A remote client receiving the end of the match.
            SetField(manager, "_blueScore", 2);
            SetField(manager, "_orangeScore", 1);
            SetField(manager, "_state", StateValue("GameOver"));
            InvokePrivate(manager, "OnDeserialization");

            Assert.That(gameOver.activeSelf, Is.True);
            Assert.That(winner.text, Does.Contain("BLUE"));

            // The owner resetting for a rematch: no deserialization ever arrives on this client.
            SetField(manager, "_state", StateValue("Lobby"));
            InvokePrivate(manager, "RequestMatchSerialization");

            Assert.That(gameOver.activeSelf, Is.False,
                "The owner's own write has to redraw its HUD, or Game Over stays up through the next match.");
        }

        /// <summary>
        /// Start order between the HUD and the manager is undefined. If the manager drew first, the HUD's
        /// own Start hid every overlay over the top of it, and a player joining mid-overtime saw no
        /// overtime until the state next changed. The HUD asks to be redrawn after hiding.
        /// </summary>
        [Test]
        public void Hud_StartingAfterTheManager_DoesNotWipeTheMatchState()
        {
            Component manager = CreateRocketLeague("RocketLeagueManager");
            Component hud = CreateRocketLeague("RocketLeagueHUD");
            GameObject overtime = Panel(hud, "OvertimePanel");
            SetField(manager, "HUD", hud);
            SetField(hud, "Manager", manager);

            SetField(manager, "_state", StateValue("Overtime"));
            InvokePrivate(manager, "OnDeserialization");
            Assert.That(overtime.activeSelf, Is.True, "Guard: the manager has drawn overtime.");

            InvokePrivate(hud, "Start");

            Assert.That(overtime.activeSelf, Is.True);
        }

        /// <summary>
        /// The goal handler read _lastScoringTeam, which travels in the serialization -- and that is not
        /// ordered against the goal event, so a client that heard the event first celebrated whoever
        /// scored the goal before. The team rides on the event.
        /// </summary>
        [Test]
        public void GoalEvent_CelebratesTheTeamItCarries_NotTheLastSyncedOne()
        {
            Component manager = CreateRocketLeague("RocketLeagueManager");
            Component hud = CreateRocketLeague("RocketLeagueHUD");
            Panel(hud, "GoalScoredPanel");
            TMPro.TextMeshProUGUI scorer = Text(hud, "GoalScorerText");
            SetField(manager, "HUD", hud);

            // Stale: the previous goal went in the blue goal, i.e. orange scored it.
            SetField(manager, "_lastScoringTeam", 0);

            MethodInfo goal = manager.GetType().GetMethod(
                "OnGoalScoredRemote", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(int) }, null);
            Assert.That(goal, Is.Not.Null, "The scoring team has to travel as the event's parameter.");
            Assert.That(goal.GetCustomAttribute<NetworkCallableAttribute>(), Is.Not.Null);

            goal.Invoke(manager, new object[] { 1 }); // the orange goal: blue scored

            Assert.That(scorer.text, Is.EqualTo("BLUE TEAM"));
        }

        // -----------------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------------

        private GameObject CreateObject(string name)
        {
            var go = new GameObject(name);
            _created.Add(go);
            return go;
        }

        private T Create<T>(string name) where T : Component
        {
            return CreateObject(name).AddComponent<T>();
        }

        private Component CreateRocketLeague(string typeName)
        {
            return CreateObject(typeName + "UnderTest").AddComponent(RocketLeagueType(typeName));
        }

        private GameObject Panel(Component hud, string fieldName)
        {
            GameObject panel = CreateObject(fieldName);
            panel.SetActive(false);
            SetField(hud, fieldName, panel);
            return panel;
        }

        private TMPro.TextMeshProUGUI Text(Component hud, string fieldName)
        {
            var text = CreateObject(fieldName).AddComponent<TMPro.TextMeshProUGUI>();
            SetField(hud, fieldName, text);
            return text;
        }

        internal static Type RocketLeagueType(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != "RocketLeague")
                    continue;

                Type type = assembly.GetType("RocketLeague." + name);
                if (type != null)
                    return type;
            }

            Assert.Fail("RocketLeague." + name + " is not loaded. The RocketLeague assembly has to compile for " +
                        "the arena's regression tests to mean anything.");
            return null;
        }

        private static object StateValue(string state)
        {
            return (int)Enum.Parse(RocketLeagueType("GameState"), state);
        }

        private static void ApplyMatchState(Component hud, string state)
        {
            object value = Enum.Parse(RocketLeagueType("GameState"), state);
            InvokePrivate(hud, "ApplyMatchState", value, 3f, 1);
        }

        private static void AssertOverlays(string state, GameObject[] up, GameObject[] down)
        {
            foreach (GameObject panel in up)
                Assert.That(panel.activeSelf, Is.True, state + ": " + panel.name + " should be up");

            foreach (GameObject panel in down)
                Assert.That(panel.activeSelf, Is.False, state + ": " + panel.name + " should be down");
        }

        private static int[] Filled(int length, int value)
        {
            var array = new int[length];
            for (int i = 0; i < length; i++)
                array[i] = value;
            return array;
        }

        private static MethodDeclarationSyntax ParseMethod(string path, string methodName)
        {
            Assert.That(File.Exists(path), Is.True, "Source not found at " + path);

            return CSharpSyntaxTree.ParseText(File.ReadAllText(path))
                .GetRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Single(method => method.Identifier.ValueText == methodName);
        }

        /// <summary>The simple names of everything the method calls.</summary>
        private static List<string> InvokedNames(MethodDeclarationSyntax method)
        {
            return method
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Select(invocation => invocation.Expression is MemberAccessExpressionSyntax access
                    ? access.Name.Identifier.ValueText
                    : invocation.Expression.ToString())
                .ToList();
        }

        internal static FieldInfo FindField(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }

            throw new MissingFieldException(type.Name, name);
        }

        internal static T GetField<T>(object instance, string name)
        {
            return (T)FindField(instance.GetType(), name).GetValue(instance);
        }

        internal static void SetField(object instance, string name, object value)
        {
            FindField(instance.GetType(), name).SetValue(instance, value);
        }

        internal static object InvokePrivate(object instance, string name, params object[] args)
        {
            args = args ?? Array.Empty<object>();

            for (Type current = instance.GetType(); current != null; current = current.BaseType)
            {
                foreach (MethodInfo method in current.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != name || !Accepts(method.GetParameters(), args))
                        continue;

                    try
                    {
                        return method.Invoke(instance, args);
                    }
                    catch (TargetInvocationException exception) when (exception.InnerException != null)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                        throw;
                    }
                }
            }

            throw new MissingMethodException(instance.GetType().Name, name);
        }

        private static bool Accepts(ParameterInfo[] parameters, object[] args)
        {
            if (parameters.Length != args.Length)
                return false;

            for (int i = 0; i < parameters.Length; i++)
            {
                if (args[i] == null)
                {
                    if (parameters[i].ParameterType.IsValueType)
                        return false;
                }
                else if (!parameters[i].ParameterType.IsInstanceOfType(args[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Station bounds checked against where the stations actually stand in CEShowcase.unity. Both were
    /// world-space boxes left behind when their stations moved: Station 1's bullets spawned 23 m outside
    /// their box, spent their one bounce on the spawn frame and flew across Station 6, and Station 2's
    /// flock swarmed the hub's spawn point while its walled arena stood empty. Nothing errors when this
    /// happens, so the scene is the thing to check.
    /// </summary>
    public class CEShowcaseSceneLayoutRegressionTests
    {
        private const string ShowcaseScenePath = "Assets/Scenes/CEShowcase.unity/CEShowcase.unity";

        // Footprints are compared to the floor's own bounds; anything inside this is the same edge.
        private const float FootprintTolerance = 0.05f;

        private Scene _showcaseScene;
        private Scene _priorActiveScene;
        private int _priorSceneCount;

        [OneTimeSetUp]
        public void LoadShowcaseForHeadlessRuns()
        {
            // Interactive runs use whatever the author has open. A headless run has nothing loaded, so the
            // world is opened alongside the runner's own scene and closed again afterwards.
            if (!Application.isBatchMode || SceneManager.GetSceneByPath(ShowcaseScenePath).isLoaded)
                return;

            _priorSceneCount = SceneManager.sceneCount;
            _priorActiveScene = SceneManager.GetActiveScene();
            _showcaseScene = EditorSceneManager.OpenScene(ShowcaseScenePath, OpenSceneMode.Additive);
        }

        [OneTimeTearDown]
        public void RestoreHeadlessSceneSetup()
        {
            if (!_showcaseScene.IsValid())
                return;

            if (_priorSceneCount == 0)
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);

            EditorSceneManager.CloseScene(_showcaseScene, true);

            if (_priorActiveScene.IsValid() && _priorActiveScene.isLoaded)
                SceneManager.SetActiveScene(_priorActiveScene);

            _showcaseScene = default;
        }

        [Test]
        public void BulletStorm_BoxHoldsItsSpawnPoints_AndCoversStationOnesFloor()
        {
            BulletHellDemo demo = FindInShowcase<BulletHellDemo>();

            Vector3 center = CEShowcaseGameplayRegressionTests.GetField<Vector3>(demo, "_stationCenter");
            Vector3 halfExtents = CEShowcaseGameplayRegressionTests.GetField<Vector3>(demo, "_stationHalfExtents");
            var box = new Bounds(center, halfExtents * 2f);

            Transform[] spawnPoints = CEShowcaseGameplayRegressionTests.GetField<Transform[]>(demo, "_spawnPoints");
            Assert.That(spawnPoints, Is.Not.Null.And.Not.Empty, "Station 1 has no spawn points wired.");

            foreach (Transform spawnPoint in spawnPoints)
            {
                Assert.That(spawnPoint, Is.Not.Null);
                Assert.That(box.Contains(spawnPoint.position), Is.True,
                    $"Spawn point '{spawnPoint.name}' at {spawnPoint.position} is outside the bullet box " +
                    $"{box.min}..{box.max}. A bullet spawned outside is clamped onto the box on its first frame, " +
                    "which spends its only bounce, and half of them are despawned a frame later.");
            }

            AssertFootprint(box, StationFloor(demo.transform, "Station1_Floor"), "Station 1's bullet box");
        }

        [Test]
        public void Flock_ArenaIsStationTwosFloor_AndHoldsTheAgentContainer()
        {
            FlockingDemo flock = FindInShowcase<FlockingDemo>();

            var arena = new Bounds();
            arena.SetMinMax(
                CEShowcaseGameplayRegressionTests.GetField<Vector3>(flock, "_arenaMin"),
                CEShowcaseGameplayRegressionTests.GetField<Vector3>(flock, "_arenaMax"));

            AssertFootprint(arena, StationFloor(flock.transform, "Station2_Arena"), "Station 2's flock arena");

            Transform container = CEShowcaseGameplayRegressionTests.GetField<Transform>(flock, "_agentParent");
            Assert.That(container, Is.Not.Null);
            Vector3 home = container.position;
            Assert.That(home.x, Is.InRange(arena.min.x, arena.max.x));
            Assert.That(home.z, Is.InRange(arena.min.z, arena.max.z),
                "The agents' own container sits outside the box they are simulated in.");
        }

        /// <summary>
        /// A car only listens to its driver while the driver is in the arena, and the two teleporters are
        /// what say so. The arena pad reaches the roster through the match manager; the return pad is not
        /// wired and finds it beside itself. Read-only: the pad's own lookup caches into its field, and a
        /// test must not leave that in an author's open scene.
        /// </summary>
        [Test]
        public void ArenaTeleporters_BothReachTheRosterThatGatesCarInput()
        {
            Type spawnerType = CEShowcaseGameplayRegressionTests.RocketLeagueType("PlayerSpawner");

            // Unity's own null test throughout: the editor hands an unassigned serialized reference a stand-in
            // object that is not C# null.
            Component arenaPad = FindInShowcase(CEShowcaseGameplayRegressionTests.RocketLeagueType("ArenaTeleporter"));
            var manager = CEShowcaseGameplayRegressionTests.GetField<UnityEngine.Object>(arenaPad, "Manager");
            Assert.That(manager != null, Is.True, "The arena pad has no match manager to reach the roster through.");
            var spawner = CEShowcaseGameplayRegressionTests.GetField<UnityEngine.Object>(manager, "Spawner");
            Assert.That(spawner != null, Is.True, "The match manager has no roster wired.");

            Component hubPad = FindInShowcase(CEShowcaseGameplayRegressionTests.RocketLeagueType("HubTeleporter"));
            var wired = CEShowcaseGameplayRegressionTests.GetField<UnityEngine.Object>(hubPad, "Spawner");
            UnityEngine.Object reached = wired != null
                ? wired
                : hubPad.transform.parent != null
                    ? hubPad.transform.parent.GetComponentInChildren(spawnerType, true)
                    : null;

            Assert.That(reached, Is.SameAs(spawner),
                "The return pad must be wired to the roster or sit beside it, or a player back in the hub keeps " +
                "driving the car from there.");
        }

        private T FindInShowcase<T>() where T : Component
        {
            return (T)FindInShowcase(typeof(T));
        }

        private Component FindInShowcase(Type type)
        {
            Scene scene = SceneManager.GetSceneByPath(ShowcaseScenePath);
            if (!scene.isLoaded)
                Assert.Ignore("CEShowcase is not open. Open it, or run headless, to check the station layout.");

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Component found = root.GetComponentInChildren(type, true);
                if (found != null)
                    return found;
            }

            Assert.Fail(type.Name + " is missing from CEShowcase.");
            return null;
        }

        private static Bounds StationFloor(Transform behaviour, string floorName)
        {
            Transform station = behaviour.parent;
            Assert.That(station, Is.Not.Null, behaviour.name + " is expected to sit under its station root.");

            Transform floor = station.Find(floorName);
            Assert.That(floor, Is.Not.Null, $"'{floorName}' not found under '{station.name}'.");

            Renderer renderer = floor.GetComponent<Renderer>();
            Assert.That(renderer, Is.Not.Null, $"'{floorName}' has no renderer to measure.");
            return renderer.bounds;
        }

        private static void AssertFootprint(Bounds box, Bounds floor, string what)
        {
            string message = $"{what} spans x {box.min.x}..{box.max.x}, z {box.min.z}..{box.max.z}, but its floor " +
                             $"spans x {floor.min.x}..{floor.max.x}, z {floor.min.z}..{floor.max.z}. These are " +
                             "world-space bounds, so they have to move with the station.";

            Assert.That(box.min.x, Is.EqualTo(floor.min.x).Within(FootprintTolerance), message);
            Assert.That(box.max.x, Is.EqualTo(floor.max.x).Within(FootprintTolerance), message);
            Assert.That(box.min.z, Is.EqualTo(floor.min.z).Within(FootprintTolerance), message);
            Assert.That(box.max.z, Is.EqualTo(floor.max.z).Within(FootprintTolerance), message);
        }
    }
}
