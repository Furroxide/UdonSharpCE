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
    /// Main HUD controller for the Rocket League game.
    /// Displays scores, timer, boost meter, and game state overlays.
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

        [Header("Boost Meter")]
        public Slider BoostMeter;
        public Image BoostFill;
        public TextMeshProUGUI BoostText;
        public Color BoostFullColor = new Color(1f, 0.8f, 0f);
        public Color BoostLowColor = new Color(0.3f, 0.3f, 0.3f);

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

        private NetVehicle _localVehicle;
        private float _goalDisplayTimer;
        private float _countdownTimer;
        private bool _showingCountdown;

        private void Start()
        {
            HideAllOverlays();

            if (LobbyPanel != null)
                LobbyPanel.SetActive(true);
        }

        private void Update()
        {
            UpdateBoostMeter();
            UpdateCountdown();
            UpdateGoalDisplay();
            UpdateLobbyUI();
        }

        private void UpdateBoostMeter()
        {
            // Find local player's vehicle if we don't have it
            if (_localVehicle == null && Spawner != null)
            {
                VRCPlayerApi local = Networking.LocalPlayer;
                if (local != null && local.IsValid())
                {
                    _localVehicle = Spawner.GetPlayerVehicle(local);
                }
            }

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
                BoostText.text = Mathf.RoundToInt(boostAmount * 100f).ToString();
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

            // Update player count
            if (PlayerCountText != null && inLobby)
            {
                VRCPlayerApi[] players = new VRCPlayerApi[80];
                int count = VRCPlayerApi.GetPlayerCount();
                PlayerCountText.text = $"Players: {count}";
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
            }
        }
    }
}

