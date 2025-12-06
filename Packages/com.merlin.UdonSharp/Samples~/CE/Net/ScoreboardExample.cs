using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;
using VRC.SDKBase;

namespace UdonSharp.CE.Samples.Net
{
    /// <summary>
    /// Example scoreboard demonstrating CE.Net attributes.
    ///
    /// This example shows:
    /// - [Sync] with interpolation and delta encoding
    /// - [Rpc] with rate limiting and owner-only restrictions
    /// - [LocalOnly] for methods that shouldn't be network-called
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class ScoreboardExample : UdonSharpBehaviour
    {
        #region Synced Variables

        /// <summary>
        /// Red team score with linear interpolation for smooth display.
        /// </summary>
        [Sync(InterpolationMode.Linear)]
        [UdonSynced]
        public int redScore;

        /// <summary>
        /// Blue team score with linear interpolation.
        /// </summary>
        [Sync(InterpolationMode.Linear)]
        [UdonSynced]
        public int blueScore;

        /// <summary>
        /// Per-player scores using delta encoding for bandwidth efficiency.
        /// Delta encoding hints that only a few elements change per sync.
        /// </summary>
        [Sync(DeltaEncode = true)]
        [UdonSynced]
        public int[] playerScores = new int[32];

        /// <summary>
        /// Ball position with smooth interpolation and quantization.
        /// Quantize = 0.01f means ~1cm precision, reducing bandwidth.
        /// </summary>
        [Sync(InterpolationMode.Smooth, Quantize = 0.01f)]
        [UdonSynced]
        public Vector3 ballPosition;

        #endregion

        #region UI References

        [SerializeField] private UnityEngine.UI.Text redScoreText;
        [SerializeField] private UnityEngine.UI.Text blueScoreText;
        [SerializeField] private AudioSource goalAudioSource;
        [SerializeField] private ParticleSystem goalVFX;

        #endregion

        #region Private State

        private RateLimiter _goalAnnounceLimiter;

        #endregion

        #region Unity Events

        private void Start()
        {
            // Initialize rate limiter: max 5 goal announcements per second
            _goalAnnounceLimiter = new RateLimiter(5f, true, "GoalAnnounce");

            UpdateScoreUI();
        }

        #endregion

        #region Network Events

        public override void OnDeserialization()
        {
            UpdateScoreUI();
        }

        #endregion

        #region RPCs

        /// <summary>
        /// Announces a goal to all players.
        /// Rate limited to prevent spam (max 5 calls/second).
        /// </summary>
        /// <param name="team">0 = red, 1 = blue</param>
        /// <param name="scorerId">Player ID of the scorer</param>
        [Rpc(Target = RpcTarget.All, RateLimit = 5f)]
        public void AnnounceGoal(int team, int scorerId)
        {
            // Rate limit check (in addition to compile-time validation)
            if (_goalAnnounceLimiter != null && !_goalAnnounceLimiter.TryCall())
            {
                return;
            }

            PlayGoalEffects(team);
            Debug.Log($"[Scoreboard] Goal! Team {team}, Scorer ID: {scorerId}");
        }

        /// <summary>
        /// Resets all scores. Only the owner can call this.
        /// </summary>
        [RpcOwnerOnly]
        public void ResetScores()
        {
            if (!Networking.IsOwner(gameObject))
            {
                Debug.LogWarning("[Scoreboard] Only owner can reset scores");
                return;
            }

            redScore = 0;
            blueScore = 0;

            for (int i = 0; i < playerScores.Length; i++)
            {
                playerScores[i] = 0;
            }

            RequestSerialization();
            Debug.Log("[Scoreboard] Scores reset by owner");
        }

        /// <summary>
        /// Adds score for a player. Owner-only to prevent cheating.
        /// </summary>
        /// <param name="playerId">The player index (0-31)</param>
        /// <param name="team">0 = red, 1 = blue</param>
        /// <param name="points">Points to add</param>
        [Rpc(Target = RpcTarget.Owner, OwnerOnly = true, RateLimit = 10f)]
        public void AddScore(int playerId, int team, int points)
        {
            if (!Networking.IsOwner(gameObject))
            {
                return;
            }

            // Validate player ID
            if (playerId < 0 || playerId >= playerScores.Length)
            {
                Debug.LogWarning($"[Scoreboard] Invalid player ID: {playerId}");
                return;
            }

            // Update scores
            playerScores[playerId] += points;

            if (team == 0)
            {
                redScore += points;
            }
            else
            {
                blueScore += points;
            }

            RequestSerialization();
        }

        #endregion

        #region Local-Only Methods

        /// <summary>
        /// Plays goal visual and audio effects locally.
        /// This should never be called over the network - effects run on each client.
        /// </summary>
        [LocalOnly("Visual effects should only render locally")]
        private void PlayGoalEffects(int team)
        {
            // Play audio
            if (goalAudioSource != null)
            {
                goalAudioSource.Play();
            }

            // Play VFX
            if (goalVFX != null)
            {
                goalVFX.Play();
            }
        }

        /// <summary>
        /// Updates the score display UI locally.
        /// </summary>
        [LocalOnly]
        private void UpdateScoreUI()
        {
            if (redScoreText != null)
            {
                redScoreText.text = redScore.ToString();
            }

            if (blueScoreText != null)
            {
                blueScoreText.text = blueScore.ToString();
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Called when a goal is scored in gameplay.
        /// Handles ownership, score update, and network announcement.
        /// </summary>
        public void OnGoalScored(int team, VRCPlayerApi scorer)
        {
            // Take ownership if needed
            if (!Networking.IsOwner(gameObject))
            {
                Networking.SetOwner(Networking.LocalPlayer, gameObject);
            }

            // Get player index (simplified - real implementation would map player IDs)
            int playerIndex = scorer.playerId % playerScores.Length;

            // Update score locally (owner)
            AddScore(playerIndex, team, 1);

            // Announce to all players
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(AnnounceGoal), team, playerIndex);
        }

        #endregion
    }
}
