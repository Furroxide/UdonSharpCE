using JetBrains.Annotations;
using UdonSharp;
using UdonSharp.CE.NetPhysics;
using UnityEngine;
using VRC.SDKBase;

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

        private void Start()
        {
            _initialized = true;
            _matchTimeRemaining = MatchDuration;
            _state = (int)GameState.Lobby;

            if (BlueGoal != null) BlueGoal.Manager = this;
            if (OrangeGoal != null) OrangeGoal.Manager = this;
        }

        private void Update()
        {
            if (!Networking.IsMaster)
                return;

            UpdateGameState();
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

            RequestSerialization();
        }

        /// <summary>
        /// Called by the host/admin to start the match.
        /// </summary>
        public void StartMatch()
        {
            if (!Networking.IsMaster)
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
            if (!Networking.IsMaster)
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

            RequestSerialization();
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

            RequestSerialization();
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
            RequestSerialization();

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

            RequestSerialization();
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
            if (!Networking.IsMaster)
                return;

            _blueScore = 0;
            _orangeScore = 0;
            _matchTimeRemaining = MatchDuration;
            _state = (int)GameState.Lobby;

            ResetBallPosition();

            RequestSerialization();
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

