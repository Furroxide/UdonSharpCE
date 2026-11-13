using JetBrains.Annotations;
using TMPro;
using UdonSharp;
using UdonSharp.CE.NetPhysics;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

namespace RocketLeague
{
    /// <summary>
    /// Main HUD controller for the Rocket League game: the arena scoreboard (scores, timer and the match
    /// overlays), the boost gauge over the local player's car, and the local player's status line.
    ///
    /// Everything here is world-space -- VRChat draws nothing over a player's own view -- and every
    /// reference is optional, so a scene can wire as much or as little of it as it likes.
    /// </summary>
    [PublicAPI]
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class RocketLeagueHUD : UdonSharpBehaviour
    {
        [Header("References")]
        public RocketLeagueManager Manager;
        public PlayerSpawner Spawner;

        [Header("Score Display")]
        public TextMeshProUGUI BlueScoreText;
        public TextMeshProUGUI OrangeScoreText;
        public TextMeshProUGUI TimerText;

        [Header("Scoreboard")]
        [Tooltip("Optional. Turned about the vertical axis to face the local player, so one board reads from " +
                 "anywhere in the arena.")]
        public Transform Scoreboard;

        [Header("Boost Meter")]
        public Slider BoostMeter;
        public Image BoostFill;
        public TextMeshProUGUI BoostText;
        public Color BoostFullColor = new Color(1f, 0.8f, 0f);
        public Color BoostLowColor = new Color(0.3f, 0.3f, 0.3f);

        [Tooltip("Optional. Rides above the local player's own car, turned to face them, and hides while they " +
                 "have no car. Each client places its own, so nobody sees anybody else's.")]
        public Transform BoostGauge;

        [Tooltip("Height of the boost gauge above the car, in metres.")]
        public float BoostGaugeHeight = 2.2f;

        [Header("Local Player")]
        [Tooltip("Optional. Tells the local player which team they drive for, or that they are waiting for a car.")]
        public TextMeshProUGUI LocalStatusText;

        [Header("Goal Scored Overlay")]
        public GameObject GoalScoredPanel;
        public TextMeshProUGUI GoalScoredText;
        public TextMeshProUGUI GoalScorerText;
        public float GoalDisplayDuration = 3f;

        [Header("Countdown Overlay")]
        public GameObject CountdownPanel;
        public TextMeshProUGUI CountdownText;

        [Header("Game Over Overlay")]
        public GameObject GameOverPanel;
        public TextMeshProUGUI WinnerText;

        [Header("Overtime Overlay")]
        public GameObject OvertimePanel;

        [Header("Lobby UI")]
        public GameObject LobbyPanel;
        public Button StartMatchButton;
        public TextMeshProUGUI PlayerCountText;

        [Header("Team Colors")]
        public Color BlueTeamColor = new Color(0.2f, 0.4f, 1f);
        public Color OrangeTeamColor = new Color(1f, 0.5f, 0.1f);

        // Local status codes, and the "nothing shown yet" marker for the caches below.
        private const int StatusNotSeated = -2;
        private const int StatusWaitingForCar = -1;
        private const int NothingShown = -99;

        private NetVehicle _localVehicle;
        private float _goalDisplayTimer;
        private float _countdownTimer;
        private bool _showingCountdown;

        // What the per-frame readouts last wrote. A TextMeshPro text assignment rebuilds the mesh and the
        // string formatting allocates, so a value that has not changed is not written again.
        private int _shownBoostPercent = NothingShown;
        private int _shownPlayerCount = NothingShown;
        private int _shownLocalStatus = NothingShown;

        private void Start()
        {
            HideAllOverlays();

            if (LobbyPanel != null)
                LobbyPanel.SetActive(true);

            // Start order between this and the manager is undefined. If the manager went first it has
            // already drawn the match state, and the blanket hide above has just wiped it -- which for a
            // player joining mid-overtime or at Game Over would stay wiped until the state next changed.
            if (Manager != null)
                Manager.RefreshHud();
        }

        private void Update()
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            bool hasLocal = local != null && local.IsValid();

            // Looked up every frame rather than cached. A car goes back to the pool when its driver
            // leaves the arena and can be driven by somebody else a moment later; a cached reference went
            // on showing that car's boost, and would have carried the gauge along after it.
            _localVehicle = hasLocal && Spawner != null ? Spawner.GetPlayerVehicle(local) : null;

            UpdateBoostMeter();
            UpdateBoostGauge(local);
            UpdateScoreboardFacing(local, hasLocal);
            UpdateLocalStatus(local, hasLocal);
            UpdateCountdown();
            UpdateGoalDisplay();
            UpdateLobbyUI();
        }

        private void UpdateBoostMeter()
        {
            if (_localVehicle == null)
            {
                if (BoostMeter != null)
                    BoostMeter.value = 0f;
                return;
            }

            float boostAmount = _localVehicle.BoostAmount;

            if (BoostMeter != null)
                BoostMeter.value = boostAmount;

            if (BoostFill != null)
                BoostFill.color = Color.Lerp(BoostLowColor, BoostFullColor, boostAmount);

            if (BoostText != null)
            {
                int percent = Mathf.RoundToInt(boostAmount * 100f);
                if (percent != _shownBoostPercent)
                {
                    _shownBoostPercent = percent;
                    BoostText.text = percent.ToString();
                }
            }
        }

        /// <summary>
        /// Keeps the boost gauge over the local player's car, turned towards their eyes. The player drives
        /// the car from wherever they stand, so the gauge goes where they are already looking.
        /// </summary>
        private void UpdateBoostGauge(VRCPlayerApi local)
        {
            if (BoostGauge == null)
                return;

            bool show = _localVehicle != null;
            GameObject gaugeObject = BoostGauge.gameObject;
            if (gaugeObject.activeSelf != show)
                gaugeObject.SetActive(show);

            if (!show)
                return;

            Vector3 anchor = _localVehicle.transform.position + Vector3.up * BoostGaugeHeight;
            BoostGauge.position = anchor;

            // A world-space canvas reads correctly from the side its forward axis points away from.
            Vector3 away = anchor - local.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
            if (away.sqrMagnitude > 0.0001f)
                BoostGauge.rotation = Quaternion.LookRotation(away);
        }

        /// <summary>
        /// Turns the scoreboard about the vertical axis to face the local player. Each client turns its
        /// own copy, so a single board hanging over the field reads from either end.
        /// </summary>
        private void UpdateScoreboardFacing(VRCPlayerApi local, bool hasLocal)
        {
            if (Scoreboard == null || !hasLocal)
                return;

            Vector3 away = Scoreboard.position - local.GetPosition();
            away.y = 0f;

            if (away.sqrMagnitude > 0.01f)
                Scoreboard.rotation = Quaternion.LookRotation(away);
        }

        /// <summary>
        /// Tells the local player where they stand: on which team, or waiting for a car to come free.
        /// Blank for a player who holds no seat, which is everyone outside the arena.
        /// </summary>
        private void UpdateLocalStatus(VRCPlayerApi local, bool hasLocal)
        {
            if (LocalStatusText == null)
                return;

            int status = StatusNotSeated;
            if (hasLocal && Spawner != null && Spawner.IsSeated(local))
                status = _localVehicle != null ? Spawner.GetPlayerTeam(local) : StatusWaitingForCar;

            if (status == _shownLocalStatus)
                return;

            _shownLocalStatus = status;

            if (status == 0)
            {
                LocalStatusText.text = "YOU DRIVE FOR BLUE";
                LocalStatusText.color = BlueTeamColor;
            }
            else if (status == 1)
            {
                LocalStatusText.text = "YOU DRIVE FOR ORANGE";
                LocalStatusText.color = OrangeTeamColor;
            }
            else if (status == StatusWaitingForCar)
            {
                LocalStatusText.text = "ALL CARS ARE TAKEN - YOU GET THE NEXT ONE TO COME FREE";
                LocalStatusText.color = Color.white;
            }
            else
            {
                LocalStatusText.text = "";
            }
        }

        private void UpdateCountdown()
        {
            if (!_showingCountdown)
                return;

            _countdownTimer -= Time.deltaTime;

            if (_countdownTimer <= 0f)
            {
                HideCountdown();
            }
            else
            {
                int displayNumber = Mathf.CeilToInt(_countdownTimer);
                if (CountdownText != null)
                {
                    if (displayNumber <= 0)
                        CountdownText.text = "GO!";
                    else
                        CountdownText.text = displayNumber.ToString();
                }
            }
        }

        private void UpdateGoalDisplay()
        {
            if (_goalDisplayTimer > 0f)
            {
                _goalDisplayTimer -= Time.deltaTime;
                if (_goalDisplayTimer <= 0f && GoalScoredPanel != null)
                {
                    GoalScoredPanel.SetActive(false);
                }
            }
        }

        private void UpdateLobbyUI()
        {
            if (Manager == null)
                return;

            bool inLobby = Manager.State == GameState.Lobby;

            if (LobbyPanel != null && LobbyPanel.activeSelf != inLobby)
                LobbyPanel.SetActive(inLobby);

            // The players a match would be played by: the ones in the arena, when there is a roster to
            // ask. This allocated an unused 80-element player array every frame as well.
            if (PlayerCountText != null && inLobby)
            {
                int count = Spawner != null ? Spawner.SeatedPlayerCount : VRCPlayerApi.GetPlayerCount();
                if (count != _shownPlayerCount)
                {
                    _shownPlayerCount = count;
                    PlayerCountText.text = Spawner != null ? $"Players in the arena: {count}" : $"Players: {count}";
                }
            }

            // Only master can start match
            if (StartMatchButton != null)
                StartMatchButton.interactable = Networking.IsMaster;
        }

        /// <summary>
        /// Updates the score display.
        /// </summary>
        public void UpdateScores(int blueScore, int orangeScore)
        {
            if (BlueScoreText != null)
                BlueScoreText.text = blueScore.ToString();

            if (OrangeScoreText != null)
                OrangeScoreText.text = orangeScore.ToString();
        }

        /// <summary>
        /// Updates the match timer display.
        /// </summary>
        public void UpdateTimer(float timeRemaining)
        {
            if (TimerText == null)
                return;

            if (timeRemaining <= 0f && Manager != null && Manager.State == GameState.Overtime)
            {
                TimerText.text = "+0:00";
                TimerText.color = OrangeTeamColor;
            }
            else
            {
                TimerText.text = RocketLeagueManager.FormatTime(Mathf.Max(0f, timeRemaining));
                TimerText.color = timeRemaining <= 30f ? OrangeTeamColor : Color.white;
            }
        }

        /// <summary>
        /// Shows the goal scored overlay.
        /// </summary>
        public void ShowGoalScored(int scoringTeam)
        {
            if (GoalScoredPanel == null)
                return;

            GoalScoredPanel.SetActive(true);
            _goalDisplayTimer = GoalDisplayDuration;

            if (GoalScoredText != null)
            {
                // Opposite team scored
                bool blueScored = scoringTeam == 1;
                GoalScoredText.text = "GOAL!";
                GoalScoredText.color = blueScored ? BlueTeamColor : OrangeTeamColor;
            }

            if (GoalScorerText != null)
            {
                bool blueScored = scoringTeam == 1;
                GoalScorerText.text = blueScored ? "BLUE TEAM" : "ORANGE TEAM";
                GoalScorerText.color = blueScored ? BlueTeamColor : OrangeTeamColor;
            }
        }

        /// <summary>
        /// Shows the countdown overlay.
        /// </summary>
        public void ShowCountdown(float duration)
        {
            if (CountdownPanel == null)
                return;

            CountdownPanel.SetActive(true);
            _countdownTimer = duration;
            _showingCountdown = true;

            if (CountdownText != null)
                CountdownText.text = Mathf.CeilToInt(duration).ToString();
        }

        /// <summary>
        /// Hides the countdown overlay.
        /// </summary>
        public void HideCountdown()
        {
            _showingCountdown = false;

            if (CountdownPanel != null)
                CountdownPanel.SetActive(false);
        }

        /// <summary>
        /// Shows the overtime indicator.
        /// </summary>
        public void ShowOvertime()
        {
            if (OvertimePanel != null)
                OvertimePanel.SetActive(true);
        }

        /// <summary>
        /// Puts every overlay that belongs to a match state where that state says it should be: raises
        /// the ones the state calls for and lowers the rest. Driven by RocketLeagueManager whenever the
        /// synced state changes, on the owner and on everyone else alike.
        ///
        /// The goal banner is the exception. It is raised by the goal event, which carries the scoring
        /// team, and runs on its own timer; this only takes it down once play has moved on.
        /// </summary>
        /// <param name="countdownRemaining">Seconds left on the kickoff countdown, when that is the state.</param>
        /// <param name="winningTeam">1 = Blue, 0 = Orange. Only read at Game Over.</param>
        public void ApplyMatchState(GameState state, float countdownRemaining, int winningTeam)
        {
            if (state == GameState.Countdown)
                ShowCountdown(countdownRemaining);
            else
                HideCountdown();

            if (OvertimePanel != null)
                OvertimePanel.SetActive(state == GameState.Overtime);

            if (state == GameState.GameOver)
                ShowGameOver(winningTeam);
            else if (GameOverPanel != null)
                GameOverPanel.SetActive(false);

            if (state != GameState.GoalScored && state != GameState.GameOver)
            {
                _goalDisplayTimer = 0f;

                if (GoalScoredPanel != null)
                    GoalScoredPanel.SetActive(false);
            }
        }

        /// <summary>
        /// Shows the game over screen.
        /// </summary>
        public void ShowGameOver(int winningTeam)
        {
            if (GameOverPanel == null)
                return;

            GameOverPanel.SetActive(true);

            if (WinnerText != null)
            {
                bool blueWon = winningTeam == 1;
                WinnerText.text = blueWon ? "BLUE WINS!" : "ORANGE WINS!";
                WinnerText.color = blueWon ? BlueTeamColor : OrangeTeamColor;
            }
        }

        /// <summary>
        /// Hides all overlay panels.
        /// </summary>
        public void HideAllOverlays()
        {
            if (GoalScoredPanel != null)
                GoalScoredPanel.SetActive(false);

            if (CountdownPanel != null)
                CountdownPanel.SetActive(false);

            if (GameOverPanel != null)
                GameOverPanel.SetActive(false);

            if (OvertimePanel != null)
                OvertimePanel.SetActive(false);

            if (LobbyPanel != null)
                LobbyPanel.SetActive(false);

            _showingCountdown = false;
            _goalDisplayTimer = 0f;
        }

        /// <summary>
        /// Called when start match button is pressed.
        /// </summary>
        public void OnStartMatchPressed()
        {
            if (Manager != null)
                Manager.StartMatch();
        }

        /// <summary>
        /// Called when rematch button is pressed.
        /// </summary>
        public void OnRematchPressed()
        {
            if (Manager != null)
            {
                Manager.ResetMatch();
                HideAllOverlays();

                if (LobbyPanel != null)
                    LobbyPanel.SetActive(true);

                // Only the master's reset goes through. For anyone else the match is still wherever it
                // was, so put back whatever the hide above took down.
                Manager.RefreshHud();
            }
        }
    }
}
