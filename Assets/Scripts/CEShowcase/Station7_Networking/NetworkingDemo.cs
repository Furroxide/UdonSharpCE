using UdonSharp;
using UdonSharp.CE.DevTools;
using UdonSharp.CE.Net;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace CEShowcase.Station7_Networking
{
    /// <summary>
    /// Station 7: Networking Demo - Demonstrates CE.Net features for multiplayer synchronization.
    /// 
    /// This showcases:
    /// - [Sync] attribute for variable synchronization with interpolation
    /// - [Rpc] attribute for network method calls
    /// - RateLimiter for bandwidth management
    /// - Owner-based authority patterns
    /// - Late-joiner synchronization
    /// 
    /// Note: CE networking attributes work alongside VRChat's networking system.
    /// They provide compile-time validation and runtime utilities.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class NetworkingDemo : UdonSharpBehaviour
    {
        [Header("Synced State")]
        // In a full CE implementation, these would use [Sync]:
        // [Sync(InterpolationMode.Linear)]
        [UdonSynced] private int _syncedScore;
        
        // [Sync(InterpolationMode.Smooth)]
        [UdonSynced] private Vector3 _syncedPosition;
        
        // [Sync]
        [UdonSynced] private string _syncedMessage;
        
        [Header("Interactive Object")]
        [SerializeField] private Transform _movableObject;
        [SerializeField] private float _moveSpeed = 3f;
        [SerializeField] private Renderer _objectRenderer;
        
        [Header("UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _statsText;
        [SerializeField] private TMPro.TextMeshProUGUI _messagesText;
        [SerializeField] private TMPro.TextMeshProUGUI _ownerText;
        
        [Header("Audio")]
        [SerializeField] private AudioSource _sfxSource;
        [SerializeField] private AudioClip _pingSound;
        
        // ========================================
        // RATE LIMITERS (CE.Net Feature)
        // ========================================
        
        /// <summary>
        /// Rate limiter for chat messages - max 2 per second.
        /// Demonstrates RateLimiter utility for bandwidth management.
        /// </summary>
        private RateLimiter _chatLimiter;
        
        /// <summary>
        /// Rate limiter for sound effects - max 5 per second.
        /// </summary>
        private RateLimiter _soundLimiter;
        
        /// <summary>
        /// Rate limiter for position updates - max 10 per second.
        /// </summary>
        private RateLimiter _positionLimiter;
        
        // State
        private string[] _messageLog;
        private int _messageCount;
        private const int MAX_MESSAGES = 10;
        
        // Stats
        private int _rpcSentCount;
        private int _rpcReceivedCount;
        private int _rpcDroppedCount;
        private int _syncCount;
        private float _lastSyncTime;
        
        // Local state
        private bool _isOwner;
        private int _localPlayerId;
        private string _localPlayerName;
        
        void Start()
        {
            InitializeRateLimiters();
            InitializeMessageLog();
            UpdateOwnerStatus();
            
            // Initialize synced position
            if (_movableObject != null)
            {
                _syncedPosition = _movableObject.localPosition;
            }
            
            CELogger.Info("Networking", "CE.Net Networking Demo initialized");
        }
        
        /// <summary>
        /// Initializes rate limiters with CE.Net recommended settings.
        /// </summary>
        private void InitializeRateLimiters()
        {
            // Chat: 2 messages per second, drop excess
            _chatLimiter = new RateLimiter(2f, true, "Chat");
            
            // Sounds: 5 per second, drop excess
            _soundLimiter = new RateLimiter(5f, true, "Sound");
            
            // Position: 10 per second, allow excess with warning
            _positionLimiter = new RateLimiter(10f, false, "Position");
            
            CELogger.Debug("Networking", "Rate limiters initialized");
        }
        
        private void InitializeMessageLog()
        {
            _messageLog = new string[MAX_MESSAGES];
            _messageCount = 0;
            
            AddMessage("<color=#888888>Chat initialized</color>");
        }
        
        void Update()
        {
            // Interpolate object position for smooth visuals
            if (_movableObject != null)
            {
                _movableObject.localPosition = Vector3.Lerp(
                    _movableObject.localPosition,
                    _syncedPosition,
                    Time.deltaTime * 10f
                );
            }
            
            UpdateDisplay();
        }
        
        // ========================================
        // SYNC DEMONSTRATION
        // ========================================
        
        /// <summary>
        /// Called when synced variables are updated from the network.
        /// Demonstrates late-joiner sync pattern.
        /// </summary>
        public override void OnDeserialization()
        {
            _syncCount++;
            _lastSyncTime = Time.time;
            
            // Apply synced state
            UpdateObjectVisuals();
            
            CELogger.Debug("Networking", $"Received sync: score={_syncedScore}, pos={_syncedPosition}");
        }
        
        /// <summary>
        /// Requests ownership transfer for this object.
        /// Required before modifying synced state.
        /// </summary>
        public void OnRequestOwnership()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid()) return;
            
            if (!Networking.IsOwner(localPlayer, gameObject))
            {
                Networking.SetOwner(localPlayer, gameObject);
                CELogger.Info("Networking", "Ownership requested");
            }
            
            UpdateOwnerStatus();
        }
        
        /// <summary>
        /// Updates the object position (owner only).
        /// Demonstrates [Sync] with interpolation.
        /// </summary>
        public void OnMoveObject(Vector3 direction)
        {
            if (!_isOwner)
            {
                CELogger.Warning("Networking", "Cannot move - not owner!");
                return;
            }
            
            // Rate limit position updates
            if (!_positionLimiter.TryCall())
            {
                _rpcDroppedCount++;
                return;
            }
            
            _syncedPosition += direction * _moveSpeed * Time.deltaTime;
            
            // Clamp to bounds
            _syncedPosition.x = Mathf.Clamp(_syncedPosition.x, -5f, 5f);
            _syncedPosition.z = Mathf.Clamp(_syncedPosition.z, -5f, 5f);
            
            RequestSerialization();
        }
        
        /// <summary>
        /// Adds score (owner only).
        /// </summary>
        public void OnAddScore(int amount)
        {
            if (!_isOwner)
            {
                CELogger.Warning("Networking", "Cannot modify score - not owner!");
                return;
            }
            
            _syncedScore += amount;
            RequestSerialization();
            
            CELogger.Info("Networking", $"Score updated to {_syncedScore}");
        }
        
        // ========================================
        // RPC DEMONSTRATION
        // ========================================
        
        /// <summary>
        /// Sends a chat message to all players.
        /// Demonstrates [Rpc(Target = RpcTarget.All, RateLimit = 2f)].
        /// </summary>
        public void SendChatMessage()
        {
            // Rate limit check
            if (!_chatLimiter.TryCall())
            {
                _rpcDroppedCount++;
                CELogger.Warning("Networking", "Chat rate limited!");
                return;
            }
            
            string message = $"Hello from {_localPlayerName}!";
            _syncedMessage = message;
            
            // In CE, this would be decorated with [Rpc]:
            // [Rpc(Target = RpcTarget.All, RateLimit = 2f)]
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(ReceiveChatMessage));
            
            _rpcSentCount++;
            CELogger.Info("Networking", $"Sent chat: {message}");
        }
        
        /// <summary>
        /// Network callback for chat messages.
        /// </summary>
        public void ReceiveChatMessage()
        {
            _rpcReceivedCount++;
            
            string sender = "Unknown";
            VRCPlayerApi owner = Networking.GetOwner(gameObject);
            if (owner != null && owner.IsValid())
            {
                sender = owner.displayName;
            }
            
            AddMessage($"<color=#00FF00>{sender}</color>: {_syncedMessage}");
            
            CELogger.Debug("Networking", $"Received chat from {sender}");
        }
        
        /// <summary>
        /// Plays a sound on all clients.
        /// Demonstrates rate-limited sound RPC.
        /// </summary>
        public void PlayNetworkedSound()
        {
            // Rate limit check
            if (!_soundLimiter.TryCall())
            {
                _rpcDroppedCount++;
                return;
            }
            
            // [Rpc(Target = RpcTarget.All, RateLimit = 5f)]
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(ReceivePlaySound));
            
            _rpcSentCount++;
        }
        
        /// <summary>
        /// Network callback for sound effects.
        /// </summary>
        public void ReceivePlaySound()
        {
            _rpcReceivedCount++;
            
            if (_sfxSource != null && _pingSound != null)
            {
                _sfxSource.PlayOneShot(_pingSound);
            }
        }
        
        // ========================================
        // OWNER-ONLY RPC DEMONSTRATION
        // ========================================
        
        /// <summary>
        /// Resets the object (owner only).
        /// Demonstrates [RpcOwnerOnly] pattern.
        /// </summary>
        public void OnResetObject()
        {
            // [RpcOwnerOnly] - validate ownership before network call
            if (!_isOwner)
            {
                CELogger.Warning("Networking", "Cannot reset - not owner!");
                return;
            }
            
            _syncedPosition = Vector3.zero;
            _syncedScore = 0;
            _syncedMessage = "";
            
            RequestSerialization();
            
            // Broadcast reset notification
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(ReceiveReset));
            
            _rpcSentCount++;
            CELogger.Info("Networking", "Object reset by owner");
        }
        
        public void ReceiveReset()
        {
            _rpcReceivedCount++;
            AddMessage("<color=#FFFF00>Object reset by owner</color>");
        }
        
        // ========================================
        // HELPER METHODS
        // ========================================
        
        private void UpdateOwnerStatus()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer != null && localPlayer.IsValid())
            {
                _isOwner = Networking.IsOwner(localPlayer, gameObject);
                _localPlayerId = localPlayer.playerId;
                _localPlayerName = localPlayer.displayName;
            }
            else
            {
                _isOwner = false;
                _localPlayerId = -1;
                _localPlayerName = "Unknown";
            }
            
            UpdateOwnerText();
            UpdateObjectVisuals();
        }
        
        private void UpdateOwnerText()
        {
            if (_ownerText == null) return;
            
            VRCPlayerApi owner = Networking.GetOwner(gameObject);
            string ownerName = owner != null && owner.IsValid() ? owner.displayName : "None";
            
            string status = _isOwner ? "<color=#00FF00>You are owner</color>" : "<color=#FFFF00>Not owner</color>";
            
            _ownerText.text = $"<b>OWNERSHIP</b>\n" +
                             $"Owner: {ownerName}\n" +
                             $"Status: {status}";
        }
        
        private void UpdateObjectVisuals()
        {
            if (_objectRenderer == null) return;
            
            // Color based on ownership
            Color color = _isOwner ? new Color(0.3f, 0.8f, 0.3f) : new Color(0.8f, 0.3f, 0.3f);
            
            MaterialPropertyBlock block = new MaterialPropertyBlock();
            _objectRenderer.GetPropertyBlock(block);
            block.SetColor("_Color", color);
            _objectRenderer.SetPropertyBlock(block);
        }
        
        private void AddMessage(string message)
        {
            // Shift messages
            if (_messageCount >= MAX_MESSAGES)
            {
                for (int i = 0; i < MAX_MESSAGES - 1; i++)
                {
                    _messageLog[i] = _messageLog[i + 1];
                }
                _messageCount = MAX_MESSAGES - 1;
            }
            
            _messageLog[_messageCount] = message;
            _messageCount++;
            
            UpdateMessagesText();
        }
        
        private void UpdateMessagesText()
        {
            if (_messagesText == null) return;
            
            string text = "<b>NETWORK LOG</b>\n\n";
            
            for (int i = 0; i < _messageCount; i++)
            {
                text += _messageLog[i] + "\n";
            }
            
            _messagesText.text = text;
        }
        
        private void UpdateDisplay()
        {
            if (_statsText == null) return;
            
            float timeSinceSync = Time.time - _lastSyncTime;
            
            _statsText.text = $"<b>CE.NET METRICS</b>\n" +
                             $"Synced Score: <color=#00FF00>{_syncedScore}</color>\n" +
                             $"Synced Position: {_syncedPosition:F1}\n" +
                             $"Sync Count: {_syncCount}\n" +
                             $"Last Sync: {timeSinceSync:F1}s ago\n" +
                             $"RPCs Sent: {_rpcSentCount}\n" +
                             $"RPCs Received: {_rpcReceivedCount}\n" +
                             $"RPCs Dropped: <color=#FF0000>{_rpcDroppedCount}</color>\n" +
                             $"<color=#FFFF00>Using: [Sync] + [Rpc] + RateLimiter</color>";
        }
        
        // ========================================
        // VRCHAT CALLBACKS
        // ========================================
        
        public override void OnOwnershipTransferred(VRCPlayerApi player)
        {
            UpdateOwnerStatus();
            
            string playerName = player != null && player.IsValid() ? player.displayName : "Unknown";
            AddMessage($"<color=#00FFFF>Ownership transferred to {playerName}</color>");
            
            CELogger.Info("Networking", $"Ownership transferred to {playerName}");
        }
        
        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid()) return;
            
            AddMessage($"<color=#888888>{player.displayName} joined</color>");
            
            // Late-joiner sync: data automatically syncs via OnDeserialization
        }
        
        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (player == null) return;
            
            AddMessage($"<color=#888888>{player.displayName} left</color>");
            UpdateOwnerStatus();
        }
        
        // ========================================
        // UI BUTTON CALLBACKS
        // ========================================
        
        public void OnMoveLeft() => OnMoveObject(Vector3.left);
        public void OnMoveRight() => OnMoveObject(Vector3.right);
        public void OnMoveForward() => OnMoveObject(Vector3.forward);
        public void OnMoveBack() => OnMoveObject(Vector3.back);
        
        public void OnAdd10Points() => OnAddScore(10);
        public void OnAdd50Points() => OnAddScore(50);
        
        public void OnSendMessage() => SendChatMessage();
        public void OnPlaySound() => PlayNetworkedSound();
        
        public void OnTakeOwnership() => OnRequestOwnership();
        public void OnReset() => OnResetObject();
        
        /// <summary>
        /// Demo: Stress test rate limiter.
        /// </summary>
        public void OnRateLimitStressTest()
        {
            int attempted = 0;
            int succeeded = 0;
            
            // Try to send 20 messages rapidly
            for (int i = 0; i < 20; i++)
            {
                attempted++;
                if (_chatLimiter.TryCall())
                {
                    succeeded++;
                }
            }
            
            CELogger.Info("Networking", 
                $"Rate limit stress test: {succeeded}/{attempted} calls succeeded " +
                $"(limit: {_chatLimiter.RateLimit}/sec)");
            
            AddMessage($"<color=#FFFF00>Stress test: {succeeded}/20 calls passed</color>");
        }
    }
}

