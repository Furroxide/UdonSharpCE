using JetBrains.Annotations;
using UdonSharp;
using UdonSharp.CE.NetPhysics;
using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon.Common;

namespace RocketLeague
{
    /// <summary>
    /// Game state enumeration for the Rocket League match.
    /// </summary>
    public enum GameState
    {
        Lobby,
        Countdown,
        Playing,
        GoalScored,
        Overtime,
        GameOver
    }

    /// <summary>
    /// Central game controller handling match state, scoring, and game flow.
    /// </summary>
    [PublicAPI]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class RocketLeagueManager : UdonSharpBehaviour
    {
        [Header("Game Settings")]
        public float MatchDuration = 300f;
        public float CountdownDuration = 3f;
        public float GoalCelebrationDuration = 3f;
        public int WinScore = 0;

        [Header("References")]
        public NetPhysicsWorld PhysicsWorld;
        public NetBall Ball;
        public ArenaGoal BlueGoal;
        public ArenaGoal OrangeGoal;
        public Transform BallSpawnPoint;
        public PlayerSpawner Spawner;

        [Header("UI")]
        public RocketLeagueHUD HUD;

        [Header("Audio")]
        public AudioSource GoalHorn;
        public AudioSource CountdownBeep;
        public AudioSource WhistleBlow;

        [UdonSynced] private int _blueScore;
        [UdonSynced] private int _orangeScore;
        [UdonSynced] private float _matchTimeRemaining;
        [UdonSynced] private int _state;
        [UdonSynced] private float _stateTimer;
        [UdonSynced] private int _lastScoringTeam;

        // What ApplyStateToHud last drew, so a call that changes nothing costs a few comparisons.
        private int _hudState = -1;
        private int _hudBlueScore = -1;
        private int _hudOrangeScore = -1;
        private int _hudSeconds = -1;

        public int BlueScore => _blueScore;
        public int OrangeScore => _orangeScore;
        public float MatchTimeRemaining => _matchTimeRemaining;
        public GameState State => (GameState)_state;
        public int LastScoringTeam => _lastScoringTeam;

        // Match state changes are meaningful at a few Hz, not per frame.
        private const float SerializeInterval = 0.25f;
        private float _lastSerializeTime;

        private void Start()
        {
            if (BlueGoal != null) BlueGoal.Manager = this;
            if (OrangeGoal != null) OrangeGoal.Manager = this;

            // Only the owner may seed the match state. This previously ran on every client, so every
            // non-owner overwrote its local copy of synced fields with lobby defaults on join -- which
            // then flickered back as soon as the real state arrived.
            if (EnsureOwnership())
            {
                _matchTimeRemaining = MatchDuration;
                _state = (int)GameState.Lobby;
                RequestMatchSerialization();
            }

            // Everyone draws what they hold. Match state can be delivered before Start, and VRChat drops
            // the OnDeserialization for a behaviour that has not started yet, so for a late joiner this
            // is where the first picture of the match comes from.
            ApplyStateToHud();
        }

        private void Update()
        {
            // Gate on ownership, not on IsMaster. VRChat discards writes to synced variables from
            // non-owners, so a master that does not own this object was silently writing into the void
            // while the real owner never wrote at all.
            if (!Networking.IsOwner(Networking.LocalPlayer, gameObject))
                return;

            UpdateGameState();
        }

        /// <summary>
        /// The match state is instance-wide, so the master claims it. Ownership -- not master status --
        /// is what actually grants the right to write synced variables.
        /// </summary>
        private bool EnsureOwnership()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null)
                return false;

            if (Networking.IsOwner(localPlayer, gameObject))
                return true;

            if (!Networking.IsMaster)
                return false;

            Networking.SetOwner(localPlayer, gameObject);
            return Networking.IsOwner(localPlayer, gameObject);
        }

        public override void OnMasterTransferred(VRCPlayerApi newMaster)
        {
            // Without this, a master handover leaves the match clock owned by a player who has left.
            if (newMaster != null && newMaster.isLocal)
                EnsureOwnership();
        }

        private void UpdateGameState()
        {
            GameState currentState = (GameState)_state;

            switch (currentState)
            {
                case GameState.Lobby:
                    // Wait for players to be ready and start the game
                    break;

                case GameState.Countdown:
                    _stateTimer -= Time.deltaTime;
                    if (_stateTimer <= 0f)
                    {
                        StartPlaying();
                    }
                    break;

                case GameState.Playing:
                    _matchTimeRemaining -= Time.deltaTime;
                    if (_matchTimeRemaining <= 0f)
                    {
                        CheckMatchEnd();
                    }
                    break;

                case GameState.GoalScored:
                    _stateTimer -= Time.deltaTime;
                    if (_stateTimer <= 0f)
                    {
                        ResetForKickoff();
                    }
                    break;

                case GameState.Overtime:
                    // In overtime, first to score wins
                    break;

                case GameState.GameOver:
                    // Wait for reset
                    break;
            }

            // Rate-gated. This used to run unconditionally at the end of a per-frame Update, i.e. ~90
            // serialization requests/second of six fields even while sitting in the lobby, against a
            // world-wide ~11 KB/s Udon budget shared with every other synced object.
            if (Time.time - _lastSerializeTime < SerializeInterval)
                return;

            RequestMatchSerialization();
        }

        /// <summary>
        /// Single funnel for every synced write in this behaviour. Stamping the clock here -- rather than
        /// only in Update -- means the event-driven paths (goal, countdown, kickoff, reset) count against
        /// the same budget instead of slipping past the rate gate that Update carefully respects.
        ///
        /// It is also where the owner draws its own HUD. OnDeserialization never runs on the client that
        /// did the writing, so a HUD driven only from there left the owner looking at 0-0 and a frozen
        /// clock for the whole match.
        /// </summary>
        private void RequestMatchSerialization()
        {
            _lastSerializeTime = Time.time;
            RequestSerialization();
            ApplyStateToHud();
        }

        public override void OnPostSerialization(SerializationResult result)
        {
            // Failures were previously invisible: no OnPostSerialization meant a clogged network silently
            // stopped the scoreboard updating with nothing in the log.
            if (!result.success)
            {
                _lastSerializeTime = 0f;
                Debug.LogWarning($"[RocketLeague] Match state serialization failed ({result.byteCount} bytes); will retry.");
            }
        }

        /// <summary>
        /// Called by the host/admin to start the match.
        /// </summary>
        public void StartMatch()
        {
            // EnsureOwnership, not IsMaster. Master status grants nothing on the wire: if any client owns
            // this object, a master's writes below are silently discarded and the scoreboard never moves.
            if (!EnsureOwnership())
                return;

            _blueScore = 0;
            _orangeScore = 0;
            _matchTimeRemaining = MatchDuration;

            BeginCountdown();
        }

        /// <summary>
        /// Called when a goal is scored.
        /// </summary>
        public void OnGoalScored(int scoringTeam)
        {
            if (!EnsureOwnership())
                return;

            if ((GameState)_state != GameState.Playing && (GameState)_state != GameState.Overtime)
                return;

            _lastScoringTeam = scoringTeam;

            // Team 0 = Blue goal (Orange scores), Team 1 = Orange goal (Blue scores)
            if (scoringTeam == 0)
            {
                _orangeScore++;
            }
            else
            {
                _blueScore++;
            }

            _state = (int)GameState.GoalScored;
            _stateTimer = GoalCelebrationDuration;

            // Check for win in overtime
            if (_matchTimeRemaining <= 0f)
            {
                _state = (int)GameState.GameOver;
            }
            else if (WinScore > 0 && (_blueScore >= WinScore || _orangeScore >= WinScore))
            {
                _state = (int)GameState.GameOver;
            }

            RequestMatchSerialization();

            // The team rides on the event. The handler used to read _lastScoringTeam, which travels in
            // the serialization above -- and that is not ordered against a network event, so a client
            // that heard the event first celebrated whoever had scored the goal before this one.
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(OnGoalScoredRemote), scoringTeam);
        }

        [NetworkCallable]
        public void OnGoalScoredRemote(int scoringTeam)
        {
            if (GoalHorn != null)
                GoalHorn.Play();

            if (Ball != null)
            {
                // Stop the ball
                var rb = Ball.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }

            if (HUD != null)
                HUD.ShowGoalScored(scoringTeam);
        }

        private void BeginCountdown()
        {
            _state = (int)GameState.Countdown;
            _stateTimer = CountdownDuration;

            ResetBallPosition();
            ResetPlayerPositions();

            RequestMatchSerialization();
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(OnCountdownStartRemote));
        }

        public void OnCountdownStartRemote()
        {
            // Sound only. What the HUD shows follows the synced state -- see ApplyStateToHud.
            if (CountdownBeep != null)
                CountdownBeep.Play();
        }

        private void StartPlaying()
        {
            _state = (int)GameState.Playing;
            RequestMatchSerialization();

            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(OnPlayingStartRemote));
        }

        public void OnPlayingStartRemote()
        {
            if (WhistleBlow != null)
                WhistleBlow.Play();
        }

        private void ResetForKickoff()
        {
            ResetBallPosition();
            ResetPlayerPositions();

            BeginCountdown();
        }

        private void CheckMatchEnd()
        {
            if (_blueScore == _orangeScore)
            {
                // Overtime
                _state = (int)GameState.Overtime;
                _matchTimeRemaining = 0f;
            }
            else
            {
                _state = (int)GameState.GameOver;
            }

            RequestMatchSerialization();
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(OnMatchEndRemote));
        }

        public void OnMatchEndRemote()
        {
            // The whistle for the end of regulation, whichever way it goes. This used to raise the Game
            // Over panel as well, and it is sent for a tie going to overtime too -- so overtime was played
            // under a panel announcing that orange had won.
            if (WhistleBlow != null)
                WhistleBlow.Play();
        }

        private void ResetBallPosition()
        {
            if (Ball == null || BallSpawnPoint == null)
                return;

            Ball.Position = BallSpawnPoint.position;
            Ball.Rotation = BallSpawnPoint.rotation;
            Ball.Velocity = Vector3.zero;
            Ball.AngularVelocity = Vector3.zero;
        }

        private void ResetPlayerPositions()
        {
            if (Spawner != null)
                Spawner.RespawnAllPlayers();
        }

        /// <summary>
        /// Resets the match to lobby state.
        /// </summary>
        public void ResetMatch()
        {
            if (!EnsureOwnership())
                return;

            _blueScore = 0;
            _orangeScore = 0;
            _matchTimeRemaining = MatchDuration;
            _state = (int)GameState.Lobby;

            ResetBallPosition();

            RequestMatchSerialization();
        }

        public override void OnDeserialization()
        {
            ApplyStateToHud();
        }

        /// <summary>
        /// Draws the synced match state on the local HUD. Every client comes through here -- remotes from
        /// OnDeserialization, the owner from RequestMatchSerialization -- and it is the only thing that
        /// decides which panels are up.
        ///
        /// The panels used to be raised by network events and never lowered: Game Over and Overtime
        /// stayed on screen through the next match, a golden goal ended the game with no Game Over at
        /// all, and a late joiner (who is never sent an event that has already fired) saw none of it.
        /// State is synced, so drawing from state is right for all of them.
        /// </summary>
        private void ApplyStateToHud()
        {
            if (HUD == null)
                return;

            if (_blueScore != _hudBlueScore || _orangeScore != _hudOrangeScore)
            {
                _hudBlueScore = _blueScore;
                _hudOrangeScore = _orangeScore;
                HUD.UpdateScores(_blueScore, _orangeScore);
            }

            bool stateChanged = _state != _hudState;

            // The clock reads in whole seconds, so that is how often it needs redrawing. A state change
            // redraws it too: entering overtime swaps the readout without the time moving.
            int seconds = Mathf.FloorToInt(Mathf.Max(0f, _matchTimeRemaining));
            if (stateChanged || seconds != _hudSeconds)
            {
                _hudSeconds = seconds;
                HUD.UpdateTimer(_matchTimeRemaining);
            }

            if (!stateChanged)
                return;

            _hudState = _state;

            // Scores arrive in the same serialization as the state, so the winner read here is the one
            // that goes with this Game Over.
            HUD.ApplyMatchState((GameState)_state, _stateTimer, _blueScore > _orangeScore ? 1 : 0);
        }

        /// <summary>
        /// Redraws the HUD from nothing. The HUD asks for this from its own Start, which hides every
        /// overlay and may well run after ours has drawn them.
        /// </summary>
        public void RefreshHud()
        {
            _hudState = -1;
            _hudBlueScore = -1;
            _hudOrangeScore = -1;
            _hudSeconds = -1;

            ApplyStateToHud();
        }

        /// <summary>
        /// Format time as MM:SS.
        /// </summary>
        public static string FormatTime(float seconds)
        {
            int mins = Mathf.FloorToInt(seconds / 60f);
            int secs = Mathf.FloorToInt(seconds % 60f);
            return $"{mins}:{secs:00}";
        }
    }
}

