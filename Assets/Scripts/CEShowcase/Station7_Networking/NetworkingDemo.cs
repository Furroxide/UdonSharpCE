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
    /// - [Sync(Authority...)] attribute for variable synchronization with declared write authority
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
        // All three are Authority.Owner: this station exists to demonstrate owner-based authority, and
        // every mutator below refuses to run unless _isOwner. Declaring that at the field is what keeps
        // CE0014 checking it, so a future mutator that forgets the guard is reported instead of quietly
        // writing a value only the local client will ever see.
        [Header("Synced State")]
        [Sync(Authority.Owner)]
        private int _syncedScore;
        
        // No interpolation: VRChat only supports Linear/Smooth on Continuous-sync behaviours, and this
        // behaviour must be Manual because it also syncs a string and a [SyncOnJoin] payload. Declaring
        // Smooth here is a hard compile error that aborts emit for the entire project.
        [Sync(Authority.Owner)]
        private Vector3 _syncedPosition;
        
        [Sync(Authority.Owner)]
        private string _syncedMessage;
        
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
        // The log is replicated whole from the owner, so the owner is its only writer -- see AddMessage.
        [SyncOnJoin(Priority = 20)]
        private string[] _messageLog = new string[MAX_MESSAGES];

        [SyncOnJoin(Priority = 21)]
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

        // True once a serialization has arrived on this client. See HasSyncedPosition.
        private bool _syncReceived;

        // Ownership tint, eased in Update via a cached property block
        private MaterialPropertyBlock _objectPropertyBlock;
        private Color _objectColor = new Color(0.9f, 0.3f, 0.25f);
        private Color _objectTargetColor = new Color(0.9f, 0.3f, 0.25f);
        
        void Start()
        {
            InitializeRateLimiters();
            UpdateOwnerStatus();
            
            // Only the owner seeds synced state. This ran on every client, and on a late joiner the
            // owner's position and message log can already be in place when Start runs -- VRChat delivers
            // the values but drops the OnDeserialization for a behaviour that has not started -- so the
            // seed overwrote them with scene defaults and nothing ever put them back.
            if (_isOwner)
            {
                InitializeMessageLog();

                if (_movableObject != null)
                {
                    _syncedPosition = _movableObject.localPosition;
                }
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
            if (_movableObject != null && HasSyncedPosition())
            {
                _movableObject.localPosition = Vector3.Lerp(
                    _movableObject.localPosition,
                    _syncedPosition,
                    Time.deltaTime * 10f
                );
            }

            // Ease the ownership tint instead of snapping when ownership transfers
            if (_objectRenderer != null)
            {
                if (_objectPropertyBlock == null) _objectPropertyBlock = new MaterialPropertyBlock();
                _objectColor = Color.Lerp(_objectColor, _objectTargetColor, Time.deltaTime * 8f);
                _objectRenderer.GetPropertyBlock(_objectPropertyBlock);
                _objectPropertyBlock.SetColor("_Color", _objectColor);
                _objectRenderer.SetPropertyBlock(_objectPropertyBlock);
            }

            UpdateDisplay();
            UpdateMessagesText();
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
            _syncReceived = true;
            
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
        /// Whether _syncedPosition holds a real value yet. Only the owner seeds it in Start, so on anyone
        /// else it is the compiled default until the owner's value is in hand, and easing toward that
        /// would sink the object to its parent's origin. Non-zero covers a value delivered before Start,
        /// which never raises OnDeserialization.
        /// </summary>
        private bool HasSyncedPosition()
        {
            return _isOwner || _syncReceived || _syncedPosition != Vector3.zero;
        }

        /// <summary>
        /// Updates the object position (owner only).
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

            // Sender and text travel on the event itself. They used to be read back on arrival -- the
            // text from a synced field, the sender from the object's owner -- and neither a serialization
            // nor an ownership transfer is ordered against a network event, so a client that heard the
            // event first printed the previous message under whoever had sent that one.
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(ReceiveChatMessage), _localPlayerName, message);
            
            _rpcSentCount++;
            CELogger.Info("Networking", $"Sent chat: {message}");
        }
        
        /// <summary>
        /// Network callback for chat messages.
        /// </summary>
        [Rpc(RpcTarget.All, RateLimit = 2f)]
        public void ReceiveChatMessage(string sender, string message)
        {
            _rpcReceivedCount++;
            
            // The owner keeps the last message as synced state for whoever arrives later.
            if (_isOwner)
            {
                _syncedMessage = message;
            }
            
            AddMessage($"<color=#00FF00>{sender}</color>: {message}");
            
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
            
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(ReceivePlaySound));
            
            _rpcSentCount++;
        }
        
        /// <summary>
        /// Network callback for sound effects.
        /// </summary>
        [Rpc(RpcTarget.All, RateLimit = 5f)]
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
        
        [Rpc(RpcTarget.All, RateLimit = 2f, OwnerOnly = true)]
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
            // Update() eases the actual tint toward this target with a cached property block
            _objectTargetColor = _isOwner ? new Color(0.2f, 0.85f, 0.45f) : new Color(0.9f, 0.3f, 0.25f);
        }
        
        /// <summary>
        /// Appends a line to the shared log. Only the owner writes it; everybody else shows the copy
        /// [SyncOnJoin] delivers.
        ///
        /// Every client used to append here as well as receiving the owner's log whole, so each event
        /// reached a non-owner twice -- once as its own append and once inside the next delivery -- and
        /// whichever landed second either doubled the line or wiped out the first.
        /// </summary>
        private void AddMessage(string message)
        {
            if (!_isOwner)
                return;

            EnsureMessageLog();

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

            // Publish the new line now. Left alone, capture only runs when somebody joins.
            SendCustomEvent("_CE_SyncOnJoin_RequestCapture");
        }

        /// <summary>
        /// A client that inherits the log writes into whatever the last delivery left behind, so its
        /// shape is checked rather than assumed: an index past the end here halts the behaviour.
        /// </summary>
        private void EnsureMessageLog()
        {
            if (_messageLog == null || _messageLog.Length != MAX_MESSAGES)
            {
                string[] resized = new string[MAX_MESSAGES];
                int kept = 0;

                if (_messageLog != null)
                {
                    kept = Mathf.Min(_messageLog.Length, MAX_MESSAGES);
                    for (int i = 0; i < kept; i++)
                    {
                        resized[i] = _messageLog[i];
                    }
                }

                _messageLog = resized;
                _messageCount = Mathf.Min(_messageCount, kept);
            }

            _messageCount = Mathf.Clamp(_messageCount, 0, MAX_MESSAGES);
        }
        
        private void UpdateMessagesText()
        {
            if (_messagesText == null) return;
            
            string text = "<b>NETWORK LOG</b>\n\n";
            
            int count = _messageLog == null ? 0 : Mathf.Min(_messageCount, _messageLog.Length);
            for (int i = 0; i < count; i++)
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
            
            // Refresh ownership first: if it was the owner who left, the client that inherits the log is
            // the one that has to record it.
            UpdateOwnerStatus();
            AddMessage($"<color=#888888>{player.displayName} left</color>");
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
