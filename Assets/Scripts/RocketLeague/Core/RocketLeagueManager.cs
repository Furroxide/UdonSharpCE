using JetBrains.Annotations;
using UdonSharp;
using UdonSharp.CE.NetPhysics;
using UnityEngine;
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

        private GameState _localState = GameState.Lobby;
        private bool _initialized;

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
            _initialized = true;

            if (BlueGoal != null) BlueGoal.Manager = this;
            if (OrangeGoal != null) OrangeGoal.Manager = this;

            // Only the owner may seed the match state. This previously ran on every client, so every
            // non-owner overwrote its local copy of synced fields with lobby defaults on join -- which
            // then flickered back as soon as the real state arrived.
            if (!EnsureOwnership())
                return;

            _matchTimeRemaining = MatchDuration;
            _state = (int)GameState.Lobby;
            RequestMatchSerialization();
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
        /// </summary>
        private void RequestMatchSerialization()
        {
            _lastSerializeTime = Time.time;
            RequestSerialization();
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
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(OnGoalScoredRemote));
        }

        public void OnGoalScoredRemote()
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
                HUD.ShowGoalScored(_lastScoringTeam);
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
            if (CountdownBeep != null)
                CountdownBeep.Play();

            if (HUD != null)
                HUD.ShowCountdown(CountdownDuration);
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

            if (HUD != null)
                HUD.HideCountdown();
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
            if (WhistleBlow != null)
                WhistleBlow.Play();

            if (HUD != null)
                HUD.ShowGameOver(_blueScore > _orangeScore ? 1 : 0);
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
            if (!_initialized)
                return;

            GameState newState = (GameState)_state;
            if (newState != _localState)
            {
                _localState = newState;
                OnStateChanged(newState);
            }

            if (HUD != null)
            {
                HUD.UpdateScores(_blueScore, _orangeScore);
                HUD.UpdateTimer(_matchTimeRemaining);
            }
        }

        private void OnStateChanged(GameState newState)
        {
            switch (newState)
            {
                case GameState.Overtime:
                    if (HUD != null)
                        HUD.ShowOvertime();
                    break;
            }
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

