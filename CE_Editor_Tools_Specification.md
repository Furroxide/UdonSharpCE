# CE Editor Tools — Technical Specification

*Development tools that make UdonSharpCE practical to build with*

---

## Overview

These tools address the core pain points of VRChat world development:

| Tool | Pain Point | Impact |
|------|------------|--------|
| Network Simulator | Can't test multiplayer without launching VRChat | Iteration time: minutes → seconds |
| Bandwidth Analyzer | Don't know network usage until runtime failure | Prevents production failures |
| Persistence Schema Manager | Can't see storage usage, no migration path | Prevents player data loss |
| World Validator | Bugs discovered in published worlds | Catches issues before publish |
| Async Visualizer | Can't debug compiled state machines | Makes async debuggable |
| ECS Browser | Can't inspect struct-based entities | Makes CE.Perf usable |

**Status:** Core implementation complete — Network Simulator, Bandwidth Analyzer, World Validator, and Late-Join Simulator are live; advanced tools (Async Visualizer, ECS Browser, Persistence Schema Manager) remain in design.

### Tool Status

| Tool | Status | Notes |
|------|--------|-------|
| Network Simulator | ✅ Implemented | Latency, packet loss, jitter, bandwidth simulation (`CE Tools/Network Simulator`) |
| Late-Join Simulator | ✅ Implemented | State capture and reconstruction testing (`CE Tools/Late-Join Simulator`) |
| Bandwidth Analyzer | ✅ Implemented | Editor window + analyzer runner in CE.DevTools |
| World Validator | ✅ Implemented | Editor window + validators (sync, bandwidth, persistence) |
| Graph Node Browser | ✅ Implemented | Browse, search, inspect all `[GraphNode]` methods (`CE Tools/Graph Node Browser`) |
| Persistence Schema Manager | Planned | Size analyzers exist; UI/schema workflow pending |
| Async Visualizer | Planned | Awaiting compiler debug hooks |
| ECS Browser | Planned | Awaiting CE.Perf runtime hookup + editor UI |

---

# 1. Network Simulator ✅ IMPLEMENTED

**Location:** `Editor/CE/DevTools/NetworkSim/`

**Menu:** `CE Tools/Network Simulator`, `CE Tools/Late-Join Simulator`

**Implementation includes:**
- `NetworkConditions.cs` — Network condition parameters and predefined profiles
- `NetworkSimulator.cs` — Core simulation engine with latency/packet loss/jitter
- `NetworkSimulatorWindow.cs` — Editor UI for configuring and monitoring simulation
- `LateJoinSimulator.cs` — Late-join scenario testing with state capture/reconstruction

## Problem Statement

Testing networked VRChat worlds currently requires:

1. Build and upload to VRChat (2-5 minutes)
2. Launch multiple VRChat clients (1-2 minutes each)
3. Join the same instance
4. Manually coordinate test actions
5. Can't set breakpoints across clients
6. Can't inspect state side-by-side
7. Bugs are non-deterministic (timing, network conditions)
8. No way to simulate edge cases (late join, ownership transfer, packet loss)

This makes multiplayer development 10-50x slower than single-player.

## Solution

An in-editor multiplayer simulator that:

- Runs entirely in Unity Editor play mode
- Simulates multiple VRCPlayerApi instances
- Intercepts and routes UdonSharp sync/RPC calls
- Provides deterministic control over network conditions
- Enables debugging with full Unity tooling

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           Unity Editor                                  │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │                    Network Simulator Core                        │   │
│  │  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────────┐  │   │
│  │  │ Virtual     │  │ Sync        │  │ Condition               │  │   │
│  │  │ Player      │  │ Router      │  │ Simulator               │  │   │
│  │  │ Manager     │  │             │  │ (latency, loss, etc)    │  │   │
│  │  └─────────────┘  └─────────────┘  └─────────────────────────┘  │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│         │                    │                     │                    │
│         ▼                    ▼                     ▼                    │
│  ┌─────────────┐      ┌─────────────┐      ┌─────────────┐             │
│  │ VRCPlayer   │      │ UdonSharp   │      │ Sync/RPC    │             │
│  │ API Shim    │      │ Behaviours  │      │ Interceptor │             │
│  └─────────────┘      └─────────────┘      └─────────────┘             │
│         │                    │                     │                    │
│         └────────────────────┴─────────────────────┘                    │
│                              │                                          │
│                              ▼                                          │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │                     Simulator UI Panel                           │   │
│  │  ┌─────────┐  ┌─────────┐  ┌─────────┐  ┌─────────────────────┐ │   │
│  │  │ Player  │  │ Network │  │ Event   │  │ Sync Log /          │ │   │
│  │  │ Views   │  │ Controls│  │ Triggers│  │ Timeline            │ │   │
│  │  └─────────┘  └─────────┘  └─────────┘  └─────────────────────┘ │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

## Component Specifications

### 1.1 Virtual Player Manager

Maintains simulated player instances without requiring actual VRChat clients.

```csharp
namespace UdonSharpCE.Editor.NetworkSim
{
    public class VirtualPlayer
    {
        public int PlayerId { get; }
        public string DisplayName { get; set; }
        public bool IsMaster { get; internal set; }
        public bool IsLocal { get; }  // One player is "local" for testing perspective
        
        // Simulated player state
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public bool IsGrounded { get; set; }
        public VRCPlayerApi.TrackingData HeadTracking { get; set; }
        
        // Ownership
        public List<UdonSharpBehaviour> OwnedBehaviours { get; }
        
        // Network simulation state
        public float SimulatedLatency { get; set; }  // Per-player latency
        public float PacketLossRate { get; set; }    // Per-player loss
        public bool IsConnected { get; set; }        // Can simulate disconnect
        
        // Pending sync data (for latency simulation)
        internal Queue<PendingSync> InboundSyncQueue { get; }
        internal Queue<PendingRpc> InboundRpcQueue { get; }
    }
    
    public class VirtualPlayerManager
    {
        public VirtualPlayer LocalPlayer { get; private set; }
        public VirtualPlayer MasterPlayer { get; private set; }
        public IReadOnlyList<VirtualPlayer> AllPlayers => _players;
        
        private List<VirtualPlayer> _players = new List<VirtualPlayer>();
        
        // Player management
        public VirtualPlayer AddPlayer(string displayName = null)
        {
            var player = new VirtualPlayer
            {
                PlayerId = _nextPlayerId++,
                DisplayName = displayName ?? $"Player {_nextPlayerId}",
                IsLocal = _players.Count == 0,  // First player is local
                IsMaster = _players.Count == 0, // First player is master
                SimulatedLatency = _defaultLatency,
                Position = GetSpawnPosition(_players.Count)
            };
            
            _players.Add(player);
            
            if (player.IsLocal) LocalPlayer = player;
            if (player.IsMaster) MasterPlayer = player;
            
            // Trigger OnPlayerJoined on all behaviours
            BroadcastPlayerEvent(PlayerEventType.Joined, player);
            
            return player;
        }
        
        public void RemovePlayer(VirtualPlayer player)
        {
            if (player.IsMaster && _players.Count > 1)
            {
                // Transfer master to next player
                var newMaster = _players.First(p => p != player);
                TransferMaster(newMaster);
            }
            
            // Trigger OnPlayerLeft on all behaviours
            BroadcastPlayerEvent(PlayerEventType.Left, player);
            
            // Transfer ownership of all objects to master
            foreach (var behaviour in player.OwnedBehaviours.ToList())
            {
                TransferOwnership(behaviour, MasterPlayer);
            }
            
            _players.Remove(player);
        }
        
        public void TransferMaster(VirtualPlayer newMaster)
        {
            var oldMaster = MasterPlayer;
            oldMaster.IsMaster = false;
            newMaster.IsMaster = true;
            MasterPlayer = newMaster;
            
            BroadcastMasterTransfer(oldMaster, newMaster);
        }
        
        public void SimulateDisconnect(VirtualPlayer player)
        {
            player.IsConnected = false;
            // Stop processing their syncs/RPCs
            // After timeout, trigger RemovePlayer
        }
        
        public void SimulateReconnect(VirtualPlayer player)
        {
            player.IsConnected = true;
            // Trigger late-join sync for this player
            TriggerLateJoinSync(player);
        }
    }
}
```

### 1.2 VRCPlayerApi Shim

Intercepts VRCPlayerApi calls and routes to virtual players.

```csharp
namespace UdonSharpCE.Editor.NetworkSim
{
    /// <summary>
    /// Replaces VRCPlayerApi during simulation.
    /// UdonSharp compilation redirects VRCPlayerApi calls to this shim.
    /// </summary>
    public class VRCPlayerApiShim
    {
        private VirtualPlayer _virtualPlayer;
        
        // Identity
        public int playerId => _virtualPlayer.PlayerId;
        public string displayName => _virtualPlayer.DisplayName;
        public bool isLocal => _virtualPlayer.IsLocal;
        public bool isMaster => _virtualPlayer.IsMaster;
        
        // Position/Movement
        public Vector3 GetPosition() => _virtualPlayer.Position;
        public Quaternion GetRotation() => _virtualPlayer.Rotation;
        public void TeleportTo(Vector3 pos, Quaternion rot)
        {
            _virtualPlayer.Position = pos;
            _virtualPlayer.Rotation = rot;
        }
        
        // Tracking
        public TrackingData GetTrackingData(TrackingDataType type)
        {
            return type switch
            {
                TrackingDataType.Head => _virtualPlayer.HeadTracking,
                // ... other tracking points
            };
        }
        
        // Static methods
        public static VRCPlayerApiShim GetPlayerById(int id)
        {
            var player = VirtualPlayerManager.Instance.GetPlayerById(id);
            return player?.ApiShim;
        }
        
        public static VRCPlayerApiShim[] GetPlayers(VRCPlayerApiShim[] buffer)
        {
            var players = VirtualPlayerManager.Instance.AllPlayers;
            for (int i = 0; i < players.Count && i < buffer.Length; i++)
            {
                buffer[i] = players[i].ApiShim;
            }
            return buffer;
        }
        
        public static int GetPlayerCount()
        {
            return VirtualPlayerManager.Instance.AllPlayers.Count;
        }
    }
}
```

### 1.3 Sync Router

Intercepts `[UdonSynced]` field changes and routes through the simulator.

```csharp
namespace UdonSharpCE.Editor.NetworkSim
{
    public class SyncRouter
    {
        private Dictionary<UdonSharpBehaviour, BehaviourSyncState> _syncStates;
        private ConditionSimulator _conditions;
        
        /// <summary>
        /// Called when RequestSerialization() is invoked on a behaviour.
        /// </summary>
        public void OnRequestSerialization(UdonSharpBehaviour behaviour)
        {
            var owner = GetOwner(behaviour);
            if (owner != VirtualPlayerManager.Instance.LocalPlayer)
            {
                // Only owner can serialize
                Debug.LogWarning($"RequestSerialization called on {behaviour} but local player is not owner");
                return;
            }
            
            var syncData = CaptureSyncState(behaviour);
            var syncMode = GetSyncMode(behaviour);
            
            // Route to all other players with simulated conditions
            foreach (var player in VirtualPlayerManager.Instance.AllPlayers)
            {
                if (player == owner) continue;
                if (!player.IsConnected) continue;
                
                // Apply network conditions
                if (_conditions.ShouldDropPacket(owner, player))
                {
                    LogPacketDrop(behaviour, owner, player);
                    continue;
                }
                
                var latency = _conditions.GetLatency(owner, player);
                
                // Queue sync for delivery after latency
                player.InboundSyncQueue.Enqueue(new PendingSync
                {
                    Behaviour = behaviour,
                    Data = syncData,
                    DeliveryTime = Time.realtimeSinceStartup + latency,
                    FromPlayer = owner
                });
            }
            
            LogSync(behaviour, syncData, owner);
        }
        
        /// <summary>
        /// Called every frame to process pending syncs.
        /// </summary>
        public void ProcessPendingSyncs()
        {
            var now = Time.realtimeSinceStartup;
            
            foreach (var player in VirtualPlayerManager.Instance.AllPlayers)
            {
                while (player.InboundSyncQueue.Count > 0 &&
                       player.InboundSyncQueue.Peek().DeliveryTime <= now)
                {
                    var sync = player.InboundSyncQueue.Dequeue();
                    ApplySyncState(sync.Behaviour, sync.Data, player);
                    
                    // Trigger OnDeserialization if this is the local player's view
                    if (player.IsLocal)
                    {
                        InvokeOnDeserialization(sync.Behaviour);
                    }
                }
            }
        }
        
        /// <summary>
        /// Captures current state of all [UdonSynced] fields.
        /// </summary>
        private SyncData CaptureSyncState(UdonSharpBehaviour behaviour)
        {
            var data = new SyncData();
            var type = behaviour.GetType();
            
            foreach (var field in GetSyncedFields(type))
            {
                var value = field.GetValue(behaviour);
                data.Fields[field.Name] = CloneValue(value);
                data.FieldSizes[field.Name] = EstimateByteSize(value);
            }
            
            data.TotalBytes = data.FieldSizes.Values.Sum();
            data.Timestamp = Time.realtimeSinceStartup;
            
            return data;
        }
        
        /// <summary>
        /// Applies sync state from another player's perspective.
        /// </summary>
        private void ApplySyncState(UdonSharpBehaviour behaviour, SyncData data, VirtualPlayer perspective)
        {
            // In simulation, we track state per-player
            // The "local" player sees the actual Unity state
            // Other players have their own view stored in _syncStates
            
            if (perspective.IsLocal)
            {
                // Apply to actual behaviour
                var type = behaviour.GetType();
                foreach (var field in GetSyncedFields(type))
                {
                    if (data.Fields.TryGetValue(field.Name, out var value))
                    {
                        field.SetValue(behaviour, value);
                    }
                }
            }
            else
            {
                // Store in per-player state view
                var key = (behaviour, perspective);
                _perPlayerViews[key] = data;
            }
        }
    }
    
    public class SyncData
    {
        public Dictionary<string, object> Fields { get; } = new();
        public Dictionary<string, int> FieldSizes { get; } = new();
        public int TotalBytes { get; set; }
        public float Timestamp { get; set; }
    }
}
```

### 1.4 RPC Router

Handles `SendCustomNetworkEvent` routing with type safety.

```csharp
namespace UdonSharpCE.Editor.NetworkSim
{
    public class RpcRouter
    {
        private ConditionSimulator _conditions;
        
        /// <summary>
        /// Intercepts SendCustomNetworkEvent calls.
        /// </summary>
        public void OnSendCustomNetworkEvent(
            UdonSharpBehaviour behaviour,
            NetworkEventTarget target,
            string eventName)
        {
            var sender = VirtualPlayerManager.Instance.LocalPlayer;
            
            // Validate the event can be sent
            if (!CanSendNetworkEvent(behaviour, eventName, out var reason))
            {
                Debug.LogError($"Cannot send network event '{eventName}': {reason}");
                return;
            }
            
            var targets = ResolveTargets(behaviour, target, sender);
            
            foreach (var targetPlayer in targets)
            {
                if (!targetPlayer.IsConnected) continue;
                
                if (_conditions.ShouldDropPacket(sender, targetPlayer))
                {
                    LogRpcDrop(behaviour, eventName, sender, targetPlayer);
                    continue;
                }
                
                var latency = _conditions.GetLatency(sender, targetPlayer);
                
                targetPlayer.InboundRpcQueue.Enqueue(new PendingRpc
                {
                    Behaviour = behaviour,
                    EventName = eventName,
                    Target = targetPlayer,
                    DeliveryTime = Time.realtimeSinceStartup + latency,
                    FromPlayer = sender
                });
            }
            
            LogRpc(behaviour, eventName, target, sender);
        }
        
        /// <summary>
        /// Called every frame to process pending RPCs.
        /// </summary>
        public void ProcessPendingRpcs()
        {
            var now = Time.realtimeSinceStartup;
            var localPlayer = VirtualPlayerManager.Instance.LocalPlayer;
            
            // Only process RPCs targeting the local player
            while (localPlayer.InboundRpcQueue.Count > 0 &&
                   localPlayer.InboundRpcQueue.Peek().DeliveryTime <= now)
            {
                var rpc = localPlayer.InboundRpcQueue.Dequeue();
                
                // Actually invoke the event
                rpc.Behaviour.SendCustomEvent(rpc.EventName);
                
                LogRpcDelivered(rpc);
            }
        }
        
        /// <summary>
        /// Validates event can be sent over network (CE.Net integration).
        /// </summary>
        private bool CanSendNetworkEvent(UdonSharpBehaviour behaviour, string eventName, out string reason)
        {
            // Check for [LocalOnly] attribute
            var method = behaviour.GetType().GetMethod(eventName);
            if (method == null)
            {
                reason = "Method not found";
                return false;
            }
            
            if (method.GetCustomAttribute<LocalOnlyAttribute>() != null)
            {
                reason = "Method marked [LocalOnly]";
                return false;
            }
            
            // Check visibility (CE.Net)
            if (!HasNetworkExportAttribute(method))
            {
                reason = "Method not marked for network export";
                return false;
            }
            
            reason = null;
            return true;
        }
        
        private IEnumerable<VirtualPlayer> ResolveTargets(
            UdonSharpBehaviour behaviour,
            NetworkEventTarget target,
            VirtualPlayer sender)
        {
            return target switch
            {
                NetworkEventTarget.All => VirtualPlayerManager.Instance.AllPlayers,
                NetworkEventTarget.Owner => new[] { GetOwner(behaviour) },
                _ => Enumerable.Empty<VirtualPlayer>()
            };
        }
    }
}
```

### 1.5 Condition Simulator

Simulates network conditions for realistic testing.

```csharp
namespace UdonSharpCE.Editor.NetworkSim
{
    public class ConditionSimulator
    {
        // Global settings
        public float BaseLatencyMs { get; set; } = 50f;
        public float LatencyVarianceMs { get; set; } = 20f;
        public float PacketLossPercent { get; set; } = 0f;
        public float BandwidthLimitKBps { get; set; } = 11f;
        
        // Per-player overrides
        private Dictionary<VirtualPlayer, PlayerConditions> _playerOverrides = new();
        
        // Bandwidth tracking
        private Dictionary<VirtualPlayer, BandwidthTracker> _bandwidthTrackers = new();
        
        public float GetLatency(VirtualPlayer from, VirtualPlayer to)
        {
            var baseLatency = GetEffectiveLatency(from) + GetEffectiveLatency(to);
            var variance = UnityEngine.Random.Range(-LatencyVarianceMs, LatencyVarianceMs);
            return (baseLatency + variance) / 1000f; // Convert to seconds
        }
        
        public bool ShouldDropPacket(VirtualPlayer from, VirtualPlayer to)
        {
            var lossRate = Mathf.Max(
                GetEffectiveLossRate(from),
                GetEffectiveLossRate(to)
            );
            return UnityEngine.Random.value < lossRate;
        }
        
        public bool WouldExceedBandwidth(VirtualPlayer player, int bytes)
        {
            var tracker = GetTracker(player);
            return tracker.CurrentUsageKBps + (bytes / 1000f) > BandwidthLimitKBps;
        }
        
        public void RecordBandwidthUsage(VirtualPlayer player, int bytes)
        {
            var tracker = GetTracker(player);
            tracker.AddUsage(bytes);
        }
        
        // Presets for quick testing
        public void ApplyPreset(NetworkPreset preset)
        {
            switch (preset)
            {
                case NetworkPreset.Perfect:
                    BaseLatencyMs = 0;
                    LatencyVarianceMs = 0;
                    PacketLossPercent = 0;
                    break;
                    
                case NetworkPreset.Good:
                    BaseLatencyMs = 30;
                    LatencyVarianceMs = 10;
                    PacketLossPercent = 0;
                    break;
                    
                case NetworkPreset.Average:
                    BaseLatencyMs = 80;
                    LatencyVarianceMs = 40;
                    PacketLossPercent = 1;
                    break;
                    
                case NetworkPreset.Poor:
                    BaseLatencyMs = 200;
                    LatencyVarianceMs = 100;
                    PacketLossPercent = 5;
                    break;
                    
                case NetworkPreset.Terrible:
                    BaseLatencyMs = 500;
                    LatencyVarianceMs = 300;
                    PacketLossPercent = 15;
                    break;
            }
        }
    }
    
    public enum NetworkPreset
    {
        Perfect,   // LAN testing
        Good,      // Typical same-region
        Average,   // Cross-region
        Poor,      // Bad connection
        Terrible   // Stress testing
    }
    
    public class BandwidthTracker
    {
        private Queue<(float time, int bytes)> _recentUsage = new();
        
        public float CurrentUsageKBps
        {
            get
            {
                PruneOldEntries();
                return _recentUsage.Sum(x => x.bytes) / 1000f;
            }
        }
        
        public void AddUsage(int bytes)
        {
            _recentUsage.Enqueue((Time.realtimeSinceStartup, bytes));
        }
        
        private void PruneOldEntries()
        {
            var cutoff = Time.realtimeSinceStartup - 1f; // 1 second window
            while (_recentUsage.Count > 0 && _recentUsage.Peek().time < cutoff)
            {
                _recentUsage.Dequeue();
            }
        }
    }
}
```

### 1.6 Editor UI

```csharp
namespace UdonSharpCE.Editor.NetworkSim
{
    public class NetworkSimulatorWindow : EditorWindow
    {
        [MenuItem("CE Tools/Network Simulator")]
        public static void ShowWindow()
        {
            GetWindow<NetworkSimulatorWindow>("CE Network Simulator");
        }
        
        // UI State
        private Vector2 _playerScrollPos;
        private Vector2 _logScrollPos;
        private VirtualPlayer _selectedPlayer;
        private bool _showAdvancedConditions;
        private NetworkPreset _selectedPreset = NetworkPreset.Good;
        
        // References
        private VirtualPlayerManager _playerManager;
        private SyncRouter _syncRouter;
        private RpcRouter _rpcRouter;
        private ConditionSimulator _conditions;
        private List<NetworkLogEntry> _log = new();
        
        private void OnGUI()
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to use the Network Simulator", MessageType.Info);
                return;
            }
            
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            {
                if (GUILayout.Button("Add Player", EditorStyles.toolbarButton))
                {
                    _playerManager.AddPlayer();
                }
                
                GUILayout.FlexibleSpace();
                
                EditorGUILayout.LabelField($"Players: {_playerManager.AllPlayers.Count}", GUILayout.Width(80));
            }
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.BeginHorizontal();
            {
                // Left panel: Players
                EditorGUILayout.BeginVertical(GUILayout.Width(250));
                {
                    DrawPlayersPanel();
                }
                EditorGUILayout.EndVertical();
                
                // Middle panel: Selected player details
                EditorGUILayout.BeginVertical(GUILayout.Width(300));
                {
                    DrawPlayerDetailsPanel();
                }
                EditorGUILayout.EndVertical();
                
                // Right panel: Network conditions & log
                EditorGUILayout.BeginVertical();
                {
                    DrawNetworkConditionsPanel();
                    DrawSyncLogPanel();
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawPlayersPanel()
        {
            EditorGUILayout.LabelField("Players", EditorStyles.boldLabel);
            
            _playerScrollPos = EditorGUILayout.BeginScrollView(_playerScrollPos, GUILayout.Height(200));
            {
                foreach (var player in _playerManager.AllPlayers)
                {
                    var style = player == _selectedPlayer ? "selectionRect" : "box";
                    EditorGUILayout.BeginHorizontal(style);
                    {
                        // Player icon/status
                        var statusColor = player.IsConnected ? Color.green : Color.red;
                        GUI.color = statusColor;
                        GUILayout.Label("●", GUILayout.Width(20));
                        GUI.color = Color.white;
                        
                        // Name and badges
                        var badges = "";
                        if (player.IsLocal) badges += " [LOCAL]";
                        if (player.IsMaster) badges += " [MASTER]";
                        
                        if (GUILayout.Button($"{player.DisplayName}{badges}", EditorStyles.label))
                        {
                            _selectedPlayer = player;
                        }
                        
                        GUILayout.FlexibleSpace();
                        
                        // Quick actions
                        if (!player.IsLocal && GUILayout.Button("×", GUILayout.Width(20)))
                        {
                            _playerManager.RemovePlayer(player);
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndScrollView();
            
            // Event triggers
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Simulate Events", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            {
                if (GUILayout.Button("Player Join"))
                {
                    _playerManager.AddPlayer();
                }
                if (GUILayout.Button("Player Leave") && _selectedPlayer != null && !_selectedPlayer.IsLocal)
                {
                    _playerManager.RemovePlayer(_selectedPlayer);
                }
            }
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.BeginHorizontal();
            {
                if (GUILayout.Button("Transfer Master") && _selectedPlayer != null)
                {
                    _playerManager.TransferMaster(_selectedPlayer);
                }
                if (GUILayout.Button("Force Desync"))
                {
                    SimulateDesync();
                }
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawPlayerDetailsPanel()
        {
            if (_selectedPlayer == null)
            {
                EditorGUILayout.HelpBox("Select a player to view details", MessageType.Info);
                return;
            }
            
            EditorGUILayout.LabelField($"Player: {_selectedPlayer.DisplayName}", EditorStyles.boldLabel);
            
            // Basic info
            EditorGUILayout.LabelField($"ID: {_selectedPlayer.PlayerId}");
            EditorGUILayout.LabelField($"Local: {_selectedPlayer.IsLocal}");
            EditorGUILayout.LabelField($"Master: {_selectedPlayer.IsMaster}");
            EditorGUILayout.LabelField($"Connected: {_selectedPlayer.IsConnected}");
            
            EditorGUILayout.Space();
            
            // Position (editable)
            EditorGUILayout.LabelField("Transform", EditorStyles.boldLabel);
            _selectedPlayer.Position = EditorGUILayout.Vector3Field("Position", _selectedPlayer.Position);
            var euler = _selectedPlayer.Rotation.eulerAngles;
            euler = EditorGUILayout.Vector3Field("Rotation", euler);
            _selectedPlayer.Rotation = Quaternion.Euler(euler);
            
            EditorGUILayout.Space();
            
            // Owned objects
            EditorGUILayout.LabelField($"Owned Objects ({_selectedPlayer.OwnedBehaviours.Count})", EditorStyles.boldLabel);
            foreach (var behaviour in _selectedPlayer.OwnedBehaviours.Take(10))
            {
                EditorGUILayout.BeginHorizontal();
                {
                    EditorGUILayout.ObjectField(behaviour, typeof(UdonSharpBehaviour), true);
                    if (GUILayout.Button("→", GUILayout.Width(25)))
                    {
                        // Transfer ownership dialog
                        ShowOwnershipTransferMenu(behaviour);
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            
            if (_selectedPlayer.OwnedBehaviours.Count > 10)
            {
                EditorGUILayout.LabelField($"... and {_selectedPlayer.OwnedBehaviours.Count - 10} more");
            }
            
            EditorGUILayout.Space();
            
            // Per-player network conditions
            EditorGUILayout.LabelField("Network Overrides", EditorStyles.boldLabel);
            _selectedPlayer.SimulatedLatency = EditorGUILayout.FloatField("Latency (ms)", _selectedPlayer.SimulatedLatency);
            _selectedPlayer.PacketLossRate = EditorGUILayout.Slider("Packet Loss %", _selectedPlayer.PacketLossRate * 100, 0, 50) / 100f;
            
            EditorGUILayout.Space();
            
            EditorGUILayout.BeginHorizontal();
            {
                if (GUILayout.Button(_selectedPlayer.IsConnected ? "Disconnect" : "Reconnect"))
                {
                    if (_selectedPlayer.IsConnected)
                        _playerManager.SimulateDisconnect(_selectedPlayer);
                    else
                        _playerManager.SimulateReconnect(_selectedPlayer);
                }
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawNetworkConditionsPanel()
        {
            EditorGUILayout.LabelField("Network Conditions", EditorStyles.boldLabel);
            
            // Presets
            EditorGUILayout.BeginHorizontal();
            {
                EditorGUILayout.LabelField("Preset:", GUILayout.Width(50));
                var newPreset = (NetworkPreset)EditorGUILayout.EnumPopup(_selectedPreset);
                if (newPreset != _selectedPreset)
                {
                    _selectedPreset = newPreset;
                    _conditions.ApplyPreset(newPreset);
                }
            }
            EditorGUILayout.EndHorizontal();
            
            // Manual controls
            _showAdvancedConditions = EditorGUILayout.Foldout(_showAdvancedConditions, "Advanced");
            if (_showAdvancedConditions)
            {
                EditorGUI.indentLevel++;
                _conditions.BaseLatencyMs = EditorGUILayout.FloatField("Base Latency (ms)", _conditions.BaseLatencyMs);
                _conditions.LatencyVarianceMs = EditorGUILayout.FloatField("Latency Variance (ms)", _conditions.LatencyVarianceMs);
                _conditions.PacketLossPercent = EditorGUILayout.Slider("Packet Loss %", _conditions.PacketLossPercent, 0, 50);
                _conditions.BandwidthLimitKBps = EditorGUILayout.FloatField("Bandwidth Limit (KB/s)", _conditions.BandwidthLimitKBps);
                EditorGUI.indentLevel--;
            }
            
            // Bandwidth usage display
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Bandwidth Usage", EditorStyles.boldLabel);
            
            foreach (var player in _playerManager.AllPlayers)
            {
                var usage = _conditions.GetBandwidthUsage(player);
                var ratio = usage / _conditions.BandwidthLimitKBps;
                var color = ratio < 0.7f ? Color.green : ratio < 0.9f ? Color.yellow : Color.red;
                
                EditorGUILayout.BeginHorizontal();
                {
                    EditorGUILayout.LabelField(player.DisplayName, GUILayout.Width(100));
                    
                    var rect = GUILayoutUtility.GetRect(150, 18);
                    EditorGUI.DrawRect(rect, Color.gray);
                    rect.width *= ratio;
                    EditorGUI.DrawRect(rect, color);
                    
                    EditorGUILayout.LabelField($"{usage:F1} / {_conditions.BandwidthLimitKBps:F0} KB/s");
                }
                EditorGUILayout.EndHorizontal();
            }
        }
        
        private void DrawSyncLogPanel()
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            {
                EditorGUILayout.LabelField("Network Log", EditorStyles.boldLabel);
                if (GUILayout.Button("Clear", GUILayout.Width(50)))
                {
                    _log.Clear();
                }
            }
            EditorGUILayout.EndHorizontal();
            
            _logScrollPos = EditorGUILayout.BeginScrollView(_logScrollPos, GUILayout.Height(200));
            {
                foreach (var entry in _log.TakeLast(100))
                {
                    var color = entry.Type switch
                    {
                        LogEntryType.Sync => Color.cyan,
                        LogEntryType.Rpc => Color.green,
                        LogEntryType.Drop => Color.red,
                        LogEntryType.Event => Color.yellow,
                        _ => Color.white
                    };
                    
                    var oldColor = GUI.color;
                    GUI.color = color;
                    EditorGUILayout.LabelField($"[{entry.Time:F3}] {entry.Message}");
                    GUI.color = oldColor;
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
```

### 1.7 Late-Join Simulation

Critical for testing state reconstruction.

```csharp
namespace UdonSharpCE.Editor.NetworkSim
{
    public class LateJoinSimulator
    {
        private SyncRouter _syncRouter;
        private VirtualPlayerManager _playerManager;
        
        /// <summary>
        /// Simulates a player joining an in-progress world.
        /// </summary>
        public void SimulateLateJoin(string displayName = null)
        {
            // Create player
            var player = _playerManager.AddPlayer(displayName);
            
            // Gather all current sync states
            var allBehaviours = FindAllSyncedBehaviours();
            
            // Build "world snapshot" as late joiner would receive
            var snapshot = new WorldSnapshot();
            
            foreach (var behaviour in allBehaviours)
            {
                var syncData = _syncRouter.CaptureSyncState(behaviour);
                snapshot.BehaviourStates[behaviour] = syncData;
            }
            
            // Deliver snapshot to new player (with latency)
            var latency = _conditions.GetLatency(_playerManager.MasterPlayer, player);
            
            StartCoroutine(DeliverSnapshotAfterDelay(player, snapshot, latency));
        }
        
        private IEnumerator DeliverSnapshotAfterDelay(VirtualPlayer player, WorldSnapshot snapshot, float delay)
        {
            yield return new WaitForSeconds(delay);
            
            // Apply all sync states
            foreach (var (behaviour, data) in snapshot.BehaviourStates)
            {
                _syncRouter.ApplySyncState(behaviour, data, player);
            }
            
            // Trigger OnDeserialization for all behaviours (from player's perspective)
            foreach (var behaviour in snapshot.BehaviourStates.Keys)
            {
                // This would happen on the "new player's client"
                behaviour.SendCustomEvent("OnDeserialization");
            }
            
            LogLateJoinComplete(player, snapshot.BehaviourStates.Count);
        }
    }
}
```

---

# 2. Bandwidth Analyzer

## Problem Statement

Developers exceed VRChat's network limits unknowingly:
- 11 KB/s total bandwidth
- 200-byte continuous sync limit per behaviour
- 280KB manual sync limit

They discover this when players report lag or desync in published worlds.

## Solution

Static analysis tool that calculates network usage at compile time.

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                      Bandwidth Analyzer                             │
├─────────────────────────────────────────────────────────────────────┤
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐ │
│  │  UdonSharp      │    │  Field Size     │    │  Update Rate    │ │
│  │  AST Walker     │───▶│  Calculator     │───▶│  Estimator      │ │
│  └─────────────────┘    └─────────────────┘    └─────────────────┘ │
│           │                     │                      │            │
│           ▼                     ▼                      ▼            │
│  ┌─────────────────────────────────────────────────────────────────┐│
│  │                    Analysis Results                              ││
│  │  - Per-field byte sizes                                         ││
│  │  - Per-behaviour bandwidth                                      ││
│  │  - Sync mode appropriateness                                    ││
│  │  - Limit violations                                             ││
│  │  - Optimization suggestions                                     ││
│  └─────────────────────────────────────────────────────────────────┘│
│           │                                                         │
│           ▼                                                         │
│  ┌─────────────────────────────────────────────────────────────────┐│
│  │                    Editor Window / Report                        ││
│  └─────────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────────┘
```

## Implementation

### 2.1 Field Size Calculator

```csharp
namespace UdonSharpCE.Editor.Analysis
{
    public class FieldSizeCalculator
    {
        /// <summary>
        /// Calculates serialized byte size for Udon sync.
        /// Based on actual VRChat serialization (not .NET).
        /// </summary>
        public SizeResult CalculateSize(FieldInfo field, object defaultValue = null)
        {
            var type = field.FieldType;
            
            // Primitives
            if (type == typeof(bool)) return Fixed(1, "bool");
            if (type == typeof(byte)) return Fixed(1, "byte");
            if (type == typeof(sbyte)) return Fixed(1, "sbyte");
            if (type == typeof(short)) return Fixed(2, "short");
            if (type == typeof(ushort)) return Fixed(2, "ushort");
            if (type == typeof(int)) return Fixed(4, "int");
            if (type == typeof(uint)) return Fixed(4, "uint");
            if (type == typeof(long)) return Fixed(8, "long");
            if (type == typeof(ulong)) return Fixed(8, "ulong");
            if (type == typeof(float)) return Fixed(4, "float");
            if (type == typeof(double)) return Fixed(8, "double");
            if (type == typeof(char)) return Fixed(2, "char");
            
            // Unity types
            if (type == typeof(Vector2)) return Fixed(8, "Vector2 (2×float)");
            if (type == typeof(Vector3)) return Fixed(12, "Vector3 (3×float)");
            if (type == typeof(Vector4)) return Fixed(16, "Vector4 (4×float)");
            if (type == typeof(Quaternion)) return Fixed(16, "Quaternion (4×float)");
            if (type == typeof(Color)) return Fixed(16, "Color (4×float)");
            if (type == typeof(Color32)) return Fixed(4, "Color32 (4×byte)");
            
            // Strings - variable length
            if (type == typeof(string))
            {
                var attr = field.GetCustomAttribute<MaxLengthAttribute>();
                var maxLen = attr?.Length ?? 256;  // Assume 256 if not specified
                return Variable(4, maxLen * 2 + 4, $"string (2 bytes/char + 4 byte header, max {maxLen})");
            }
            
            // Arrays
            if (type.IsArray)
            {
                var elementType = type.GetElementType();
                var elementSize = CalculateSize(elementType);
                
                // Try to get array length from field initialization or attribute
                var length = GetArrayLength(field, defaultValue);
                
                if (length.HasValue)
                {
                    var total = elementSize.MaxBytes * length.Value + 4; // +4 for length header
                    return Fixed(total, $"{elementType.Name}[{length}] ({elementSize.MaxBytes}×{length} + 4)");
                }
                else
                {
                    return Variable(4, elementSize.MaxBytes * 1024 + 4, 
                        $"{elementType.Name}[] (unknown length, assuming max 1024)");
                }
            }
            
            // VRChat types
            if (type == typeof(VRCUrl)) return Fixed(512, "VRCUrl (max 512)");
            
            // Unknown
            return Unknown(type.Name);
        }
        
        private SizeResult Fixed(int bytes, string description)
        {
            return new SizeResult
            {
                MinBytes = bytes,
                MaxBytes = bytes,
                IsFixed = true,
                Description = description
            };
        }
        
        private SizeResult Variable(int min, int max, string description)
        {
            return new SizeResult
            {
                MinBytes = min,
                MaxBytes = max,
                IsFixed = false,
                Description = description
            };
        }
    }
    
    public class SizeResult
    {
        public int MinBytes { get; set; }
        public int MaxBytes { get; set; }
        public bool IsFixed { get; set; }
        public string Description { get; set; }
        public bool IsUnknown { get; set; }
    }
}
```

### 2.2 Behaviour Analyzer

```csharp
namespace UdonSharpCE.Editor.Analysis
{
    public class BehaviourAnalyzer
    {
        private FieldSizeCalculator _sizeCalculator = new();
        
        public BehaviourAnalysisResult Analyze(Type behaviourType)
        {
            var result = new BehaviourAnalysisResult
            {
                BehaviourType = behaviourType,
                SyncMode = GetSyncMode(behaviourType)
            };
            
            // Find all synced fields
            var syncedFields = behaviourType
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f => f.GetCustomAttribute<UdonSyncedAttribute>() != null)
                .ToList();
            
            foreach (var field in syncedFields)
            {
                var fieldResult = new FieldAnalysisResult
                {
                    Field = field,
                    Size = _sizeCalculator.CalculateSize(field),
                    SyncAttribute = field.GetCustomAttribute<UdonSyncedAttribute>()
                };
                
                result.Fields.Add(fieldResult);
            }
            
            // Calculate totals
            result.MinTotalBytes = result.Fields.Sum(f => f.Size.MinBytes);
            result.MaxTotalBytes = result.Fields.Sum(f => f.Size.MaxBytes);
            
            // Check limits
            if (result.SyncMode == BehaviourSyncMode.Continuous)
            {
                if (result.MaxTotalBytes > 200)
                {
                    result.Violations.Add(new LimitViolation
                    {
                        Severity = Severity.Error,
                        Message = $"Continuous sync exceeds 200-byte limit ({result.MaxTotalBytes} bytes)",
                        Recommendation = "Switch to Manual sync or reduce synced data"
                    });
                }
            }
            
            // Estimate bandwidth
            result.EstimatedBandwidthKBps = EstimateBandwidth(result);
            
            // Generate recommendations
            GenerateRecommendations(result);
            
            return result;
        }
        
        private float EstimateBandwidth(BehaviourAnalysisResult result)
        {
            // Continuous: ~5 updates per second
            // Manual: estimate based on typical usage (1 per second)
            
            var updatesPerSecond = result.SyncMode switch
            {
                BehaviourSyncMode.Continuous => 5f,
                BehaviourSyncMode.Manual => 1f,  // Conservative estimate
                _ => 0f
            };
            
            return (result.MaxTotalBytes * updatesPerSecond) / 1000f;
        }
        
        private void GenerateRecommendations(BehaviourAnalysisResult result)
        {
            // Check for large arrays in continuous sync
            foreach (var field in result.Fields)
            {
                if (field.Field.FieldType.IsArray && 
                    result.SyncMode == BehaviourSyncMode.Continuous &&
                    field.Size.MaxBytes > 50)
                {
                    result.Recommendations.Add(new Recommendation
                    {
                        Field = field.Field,
                        Message = "Large array in continuous sync",
                        Suggestion = "Move to Manual sync with delta encoding, or split into multiple behaviours"
                    });
                }
            }
            
            // Check for strings that could be IDs
            foreach (var field in result.Fields)
            {
                if (field.Field.FieldType == typeof(string) &&
                    field.Field.Name.ToLower().Contains("id"))
                {
                    result.Recommendations.Add(new Recommendation
                    {
                        Field = field.Field,
                        Message = "String field that might be an ID",
                        Suggestion = "Consider using int index instead of string (4 bytes vs ~50+ bytes)"
                    });
                }
            }
            
            // Check for float precision
            foreach (var field in result.Fields)
            {
                if (field.Field.FieldType == typeof(float) ||
                    field.Field.FieldType == typeof(Vector3))
                {
                    var hasCESync = field.Field.GetCustomAttribute<SyncAttribute>() != null;
                    if (!hasCESync)
                    {
                        result.Recommendations.Add(new Recommendation
                        {
                            Field = field.Field,
                            Message = "Float/Vector without quantization",
                            Suggestion = "Use [Sync(Quantize = 0.01f)] to reduce precision and bandwidth"
                        });
                    }
                }
            }
        }
    }
    
    public class BehaviourAnalysisResult
    {
        public Type BehaviourType { get; set; }
        public BehaviourSyncMode SyncMode { get; set; }
        public List<FieldAnalysisResult> Fields { get; } = new();
        public int MinTotalBytes { get; set; }
        public int MaxTotalBytes { get; set; }
        public float EstimatedBandwidthKBps { get; set; }
        public List<LimitViolation> Violations { get; } = new();
        public List<Recommendation> Recommendations { get; } = new();
    }
}
```

### 2.3 World Analyzer

Analyzes all behaviours in a scene.

```csharp
namespace UdonSharpCE.Editor.Analysis
{
    public class WorldAnalyzer
    {
        private BehaviourAnalyzer _behaviourAnalyzer = new();
        
        public WorldAnalysisResult AnalyzeScene()
        {
            var result = new WorldAnalysisResult();
            
            // Find all UdonSharpBehaviours in scene
            var behaviours = FindObjectsOfType<UdonSharpBehaviour>();
            
            // Group by type (multiple instances share analysis)
            var byType = behaviours.GroupBy(b => b.GetType());
            
            foreach (var group in byType)
            {
                var typeAnalysis = _behaviourAnalyzer.Analyze(group.Key);
                typeAnalysis.InstanceCount = group.Count();
                
                result.BehaviourResults.Add(typeAnalysis);
            }
            
            // Calculate world totals
            result.TotalMinBytes = result.BehaviourResults
                .Sum(b => b.MinTotalBytes * b.InstanceCount);
            result.TotalMaxBytes = result.BehaviourResults
                .Sum(b => b.MaxTotalBytes * b.InstanceCount);
            result.TotalEstimatedBandwidthKBps = result.BehaviourResults
                .Sum(b => b.EstimatedBandwidthKBps * b.InstanceCount);
            
            // Check world-level limits
            if (result.TotalEstimatedBandwidthKBps > 11)
            {
                result.Violations.Add(new LimitViolation
                {
                    Severity = Severity.Error,
                    Message = $"Total bandwidth exceeds 11 KB/s limit ({result.TotalEstimatedBandwidthKBps:F1} KB/s)",
                    Recommendation = "Reduce sync frequency, compress data, or reduce synced behaviours"
                });
            }
            else if (result.TotalEstimatedBandwidthKBps > 8)
            {
                result.Violations.Add(new LimitViolation
                {
                    Severity = Severity.Warning,
                    Message = $"Bandwidth usage high ({result.TotalEstimatedBandwidthKBps:F1} KB/s) - may degrade with more players",
                    Recommendation = "Consider optimization to leave headroom"
                });
            }
            
            return result;
        }
    }
}
```

### 2.4 Editor Window

```csharp
namespace UdonSharpCE.Editor.Analysis
{
    public class BandwidthAnalyzerWindow : EditorWindow
    {
        [MenuItem("CE Tools/Bandwidth Analyzer")]
        public static void ShowWindow()
        {
            GetWindow<BandwidthAnalyzerWindow>("CE Bandwidth Analyzer");
        }
        
        private WorldAnalysisResult _result;
        private Vector2 _scrollPos;
        private Dictionary<Type, bool> _foldouts = new();
        
        private void OnGUI()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            {
                if (GUILayout.Button("Analyze Scene", EditorStyles.toolbarButton))
                {
                    _result = new WorldAnalyzer().AnalyzeScene();
                }
                
                GUILayout.FlexibleSpace();
                
                if (_result != null)
                {
                    var color = _result.TotalEstimatedBandwidthKBps < 8 ? Color.green :
                                _result.TotalEstimatedBandwidthKBps < 11 ? Color.yellow : Color.red;
                    GUI.color = color;
                    EditorGUILayout.LabelField(
                        $"Total: {_result.TotalEstimatedBandwidthKBps:F1} / 11 KB/s", 
                        GUILayout.Width(150));
                    GUI.color = Color.white;
                }
            }
            EditorGUILayout.EndHorizontal();
            
            if (_result == null)
            {
                EditorGUILayout.HelpBox("Click 'Analyze Scene' to calculate bandwidth usage", MessageType.Info);
                return;
            }
            
            // World summary bar
            DrawBandwidthBar("World Total", _result.TotalEstimatedBandwidthKBps, 11f);
            
            EditorGUILayout.Space();
            
            // Violations
            if (_result.Violations.Count > 0)
            {
                EditorGUILayout.LabelField("Issues", EditorStyles.boldLabel);
                foreach (var violation in _result.Violations)
                {
                    var msgType = violation.Severity == Severity.Error ? MessageType.Error : MessageType.Warning;
                    EditorGUILayout.HelpBox(violation.Message + "\n" + violation.Recommendation, msgType);
                }
                EditorGUILayout.Space();
            }
            
            // Per-behaviour breakdown
            EditorGUILayout.LabelField("By Behaviour", EditorStyles.boldLabel);
            
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            {
                // Sort by bandwidth (highest first)
                var sorted = _result.BehaviourResults
                    .OrderByDescending(b => b.EstimatedBandwidthKBps * b.InstanceCount);
                
                foreach (var behaviour in sorted)
                {
                    DrawBehaviourSection(behaviour);
                }
            }
            EditorGUILayout.EndScrollView();
        }
        
        private void DrawBehaviourSection(BehaviourAnalysisResult behaviour)
        {
            var type = behaviour.BehaviourType;
            _foldouts.TryAdd(type, false);
            
            var totalBandwidth = behaviour.EstimatedBandwidthKBps * behaviour.InstanceCount;
            var header = $"{type.Name} (×{behaviour.InstanceCount}) — {totalBandwidth:F2} KB/s";
            
            // Color-code by relative contribution
            var ratio = totalBandwidth / _result.TotalEstimatedBandwidthKBps;
            if (ratio > 0.5f) GUI.color = Color.red;
            else if (ratio > 0.2f) GUI.color = Color.yellow;
            
            _foldouts[type] = EditorGUILayout.Foldout(_foldouts[type], header, true);
            GUI.color = Color.white;
            
            if (!_foldouts[type]) return;
            
            EditorGUI.indentLevel++;
            
            // Sync mode
            EditorGUILayout.LabelField($"Sync Mode: {behaviour.SyncMode}");
            EditorGUILayout.LabelField($"Size: {behaviour.MinTotalBytes}–{behaviour.MaxTotalBytes} bytes");
            
            // Fields
            EditorGUILayout.LabelField("Fields:", EditorStyles.miniLabel);
            foreach (var field in behaviour.Fields.OrderByDescending(f => f.Size.MaxBytes))
            {
                EditorGUILayout.BeginHorizontal();
                {
                    EditorGUILayout.LabelField($"  {field.Field.Name}", GUILayout.Width(150));
                    EditorGUILayout.LabelField(field.Size.Description, EditorStyles.miniLabel);
                    EditorGUILayout.LabelField($"{field.Size.MaxBytes} B", GUILayout.Width(60));
                }
                EditorGUILayout.EndHorizontal();
            }
            
            // Recommendations
            if (behaviour.Recommendations.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Recommendations:", EditorStyles.miniLabel);
                foreach (var rec in behaviour.Recommendations)
                {
                    EditorGUILayout.HelpBox($"{rec.Field?.Name}: {rec.Suggestion}", MessageType.Info);
                }
            }
            
            // Violations
            foreach (var violation in behaviour.Violations)
            {
                var msgType = violation.Severity == Severity.Error ? MessageType.Error : MessageType.Warning;
                EditorGUILayout.HelpBox(violation.Message, msgType);
            }
            
            EditorGUI.indentLevel--;
        }
        
        private void DrawBandwidthBar(string label, float current, float max)
        {
            EditorGUILayout.BeginHorizontal();
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(100));
                
                var rect = GUILayoutUtility.GetRect(200, 20);
                var ratio = Mathf.Clamp01(current / max);
                
                // Background
                EditorGUI.DrawRect(rect, new Color(0.2f, 0.2f, 0.2f));
                
                // Fill
                var fillRect = rect;
                fillRect.width *= ratio;
                var fillColor = ratio < 0.7f ? Color.green : ratio < 0.9f ? Color.yellow : Color.red;
                EditorGUI.DrawRect(fillRect, fillColor);
                
                // Text
                var text = $"{current:F1} / {max:F0} KB/s ({ratio * 100:F0}%)";
                EditorGUI.LabelField(rect, text, EditorStyles.centeredGreyMiniLabel);
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
```

---

# 3. Persistence Schema Manager

## Problem Statement

VRChat PlayerData has strict constraints:
- 100 KB per player limit
- Schema changes can break existing saves
- No built-in migration system
- Size violations cause silent data loss

## Solution

A schema management tool that tracks, visualizes, and migrates persistent data.

## Implementation

### 3.1 Schema Extraction

```csharp
namespace UdonSharpCE.Editor.Persistence
{
    public class SchemaExtractor
    {
        public SchemaDefinition ExtractFromType(Type type)
        {
            var schema = new SchemaDefinition
            {
                TypeName = type.FullName,
                Version = GetSchemaVersion(type)
            };
            
            // Find all [PlayerData] or [PersistKey] fields
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f => f.GetCustomAttribute<PlayerDataAttribute>() != null ||
                           f.GetCustomAttribute<PersistKeyAttribute>() != null);
            
            foreach (var field in fields)
            {
                var fieldDef = new FieldDefinition
                {
                    Name = field.Name,
                    Type = field.FieldType,
                    Key = GetPersistKey(field),
                    Size = CalculateSerializedSize(field),
                    AddedInVersion = GetFieldVersion(field),
                    DefaultValue = GetDefaultValue(field)
                };
                
                schema.Fields.Add(fieldDef);
            }
            
            schema.TotalMinSize = schema.Fields.Sum(f => f.Size.MinBytes);
            schema.TotalMaxSize = schema.Fields.Sum(f => f.Size.MaxBytes);
            
            return schema;
        }
        
        private SizeEstimate CalculateSerializedSize(FieldInfo field)
        {
            var type = field.FieldType;
            
            // VRChat uses JSON-like serialization for PlayerData
            // Sizes are larger than binary due to keys and formatting
            
            if (type == typeof(bool)) return new SizeEstimate(5, 6);  // "true" or "false"
            if (type == typeof(int)) return new SizeEstimate(1, 11);  // "-2147483648"
            if (type == typeof(float)) return new SizeEstimate(3, 20); // Scientific notation possible
            if (type == typeof(string))
            {
                var maxLen = field.GetCustomAttribute<MaxLengthAttribute>()?.Length ?? 256;
                return new SizeEstimate(2, maxLen + 2);  // Quotes + content
            }
            
            // Arrays
            if (type.IsArray)
            {
                var elementType = type.GetElementType();
                var elementSize = CalculateSerializedSize(elementType);
                var maxLength = GetMaxArrayLength(field);
                
                // Array overhead: [], commas, whitespace
                var overhead = 2 + maxLength;  // [] + commas
                return new SizeEstimate(
                    overhead + elementSize.MinBytes,
                    overhead + (elementSize.MaxBytes * maxLength)
                );
            }
            
            // Nested objects
            if (type.IsClass || type.IsValueType && !type.IsPrimitive)
            {
                var nested = ExtractFromType(type);
                var overhead = 2;  // {}
                return new SizeEstimate(
                    overhead + nested.TotalMinSize,
                    overhead + nested.TotalMaxSize
                );
            }
            
            return new SizeEstimate(0, 100);  // Unknown, conservative
        }
    }
    
    public class SchemaDefinition
    {
        public string TypeName { get; set; }
        public int Version { get; set; }
        public List<FieldDefinition> Fields { get; } = new();
        public int TotalMinSize { get; set; }
        public int TotalMaxSize { get; set; }
    }
    
    public class FieldDefinition
    {
        public string Name { get; set; }
        public Type Type { get; set; }
        public string Key { get; set; }
        public SizeEstimate Size { get; set; }
        public int AddedInVersion { get; set; }
        public object DefaultValue { get; set; }
    }
}
```

### 3.2 Schema History Tracking

```csharp
namespace UdonSharpCE.Editor.Persistence
{
    [Serializable]
    public class SchemaHistory
    {
        public List<SchemaSnapshot> Snapshots { get; set; } = new();
        
        public static SchemaHistory Load()
        {
            var path = "Assets/CE/Editor/SchemaHistory.json";
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonUtility.FromJson<SchemaHistory>(json);
            }
            return new SchemaHistory();
        }
        
        public void Save()
        {
            var path = "Assets/CE/Editor/SchemaHistory.json";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(this, true));
        }
        
        public void RecordSnapshot(SchemaDefinition schema)
        {
            var snapshot = new SchemaSnapshot
            {
                Version = schema.Version,
                Timestamp = DateTime.UtcNow,
                FieldNames = schema.Fields.Select(f => f.Name).ToList(),
                FieldTypes = schema.Fields.Select(f => f.Type.FullName).ToList()
            };
            
            Snapshots.Add(snapshot);
            Save();
        }
        
        public SchemaComparison Compare(int fromVersion, int toVersion)
        {
            var from = Snapshots.FirstOrDefault(s => s.Version == fromVersion);
            var to = Snapshots.FirstOrDefault(s => s.Version == toVersion);
            
            if (from == null || to == null) return null;
            
            var comparison = new SchemaComparison
            {
                FromVersion = fromVersion,
                ToVersion = toVersion
            };
            
            // Added fields
            comparison.AddedFields = to.FieldNames
                .Except(from.FieldNames)
                .ToList();
            
            // Removed fields
            comparison.RemovedFields = from.FieldNames
                .Except(to.FieldNames)
                .ToList();
            
            // Changed types
            var commonFields = from.FieldNames.Intersect(to.FieldNames);
            foreach (var field in commonFields)
            {
                var fromType = from.FieldTypes[from.FieldNames.IndexOf(field)];
                var toType = to.FieldTypes[to.FieldNames.IndexOf(field)];
                
                if (fromType != toType)
                {
                    comparison.ChangedTypes[field] = (fromType, toType);
                }
            }
            
            // Determine if backward compatible
            comparison.IsBackwardCompatible = 
                comparison.RemovedFields.Count == 0 &&
                comparison.ChangedTypes.Count == 0;
            
            return comparison;
        }
    }
    
    [Serializable]
    public class SchemaSnapshot
    {
        public int Version;
        public DateTime Timestamp;
        public List<string> FieldNames;
        public List<string> FieldTypes;
    }
    
    public class SchemaComparison
    {
        public int FromVersion { get; set; }
        public int ToVersion { get; set; }
        public List<string> AddedFields { get; set; } = new();
        public List<string> RemovedFields { get; set; } = new();
        public Dictionary<string, (string from, string to)> ChangedTypes { get; set; } = new();
        public bool IsBackwardCompatible { get; set; }
    }
}
```

### 3.3 Migration Generator

```csharp
namespace UdonSharpCE.Editor.Persistence
{
    public class MigrationGenerator
    {
        public string GenerateMigrationCode(SchemaComparison comparison, SchemaDefinition currentSchema)
        {
            var sb = new StringBuilder();
            
            sb.AppendLine($"// Auto-generated migration: v{comparison.FromVersion} → v{comparison.ToVersion}");
            sb.AppendLine($"// Generated: {DateTime.Now}");
            sb.AppendLine();
            sb.AppendLine("public static class SchemaMigration");
            sb.AppendLine("{");
            sb.AppendLine($"    public const int FROM_VERSION = {comparison.FromVersion};");
            sb.AppendLine($"    public const int TO_VERSION = {comparison.ToVersion};");
            sb.AppendLine();
            sb.AppendLine("    public static DataDictionary Migrate(DataDictionary oldData)");
            sb.AppendLine("    {");
            sb.AppendLine("        var newData = new DataDictionary();");
            sb.AppendLine();
            
            // Copy unchanged fields
            var unchangedFields = currentSchema.Fields
                .Where(f => !comparison.AddedFields.Contains(f.Name) &&
                           !comparison.ChangedTypes.ContainsKey(f.Name))
                .Select(f => f.Key);
            
            foreach (var key in unchangedFields)
            {
                sb.AppendLine($"        if (oldData.TryGetValue(\"{key}\", out var {SanitizeVarName(key)}))");
                sb.AppendLine($"            newData[\"{key}\"] = {SanitizeVarName(key)};");
            }
            
            sb.AppendLine();
            
            // Add new fields with defaults
            foreach (var addedField in comparison.AddedFields)
            {
                var field = currentSchema.Fields.First(f => f.Name == addedField);
                var defaultValue = GetDefaultValueLiteral(field);
                
                sb.AppendLine($"        // New field in v{comparison.ToVersion}");
                sb.AppendLine($"        newData[\"{field.Key}\"] = {defaultValue};");
            }
            
            sb.AppendLine();
            
            // Handle type changes (requires manual intervention)
            if (comparison.ChangedTypes.Count > 0)
            {
                sb.AppendLine("        // TYPE CHANGES - MANUAL REVIEW REQUIRED:");
                foreach (var (field, (from, to)) in comparison.ChangedTypes)
                {
                    sb.AppendLine($"        // {field}: {from} → {to}");
                    sb.AppendLine($"        // TODO: Convert oldData[\"{field}\"] from {from} to {to}");
                }
            }
            
            sb.AppendLine();
            sb.AppendLine("        return newData;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            
            return sb.ToString();
        }
        
        private string GetDefaultValueLiteral(FieldDefinition field)
        {
            if (field.DefaultValue != null)
            {
                return field.Type switch
                {
                    Type t when t == typeof(string) => $"\"{field.DefaultValue}\"",
                    Type t when t == typeof(bool) => field.DefaultValue.ToString().ToLower(),
                    _ => field.DefaultValue.ToString()
                };
            }
            
            return field.Type switch
            {
                Type t when t == typeof(int) => "0",
                Type t when t == typeof(float) => "0f",
                Type t when t == typeof(bool) => "false",
                Type t when t == typeof(string) => "\"\"",
                Type t when t.IsArray => $"new {t.GetElementType().Name}[0]",
                _ => "null"
            };
        }
    }
}
```

### 3.4 Editor Window

```csharp
namespace UdonSharpCE.Editor.Persistence
{
    public class SchemaManagerWindow : EditorWindow
    {
        [MenuItem("CE Tools/Persistence Schema Manager")]
        public static void ShowWindow()
        {
            GetWindow<SchemaManagerWindow>("CE Schema Manager");
        }
        
        private SchemaDefinition _currentSchema;
        private SchemaHistory _history;
        private SchemaComparison _pendingMigration;
        private Vector2 _scrollPos;
        private bool _showHistory;
        private string _generatedMigration;
        
        private void OnEnable()
        {
            _history = SchemaHistory.Load();
            RefreshSchema();
        }
        
        private void RefreshSchema()
        {
            // Find all types with [PersistentSchema] or similar marker
            var schemaTypes = TypeCache.GetTypesWithAttribute<PersistentSchemaAttribute>();
            
            if (schemaTypes.Count > 0)
            {
                _currentSchema = new SchemaExtractor().ExtractFromType(schemaTypes.First());
            }
        }
        
        private void OnGUI()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton))
                {
                    RefreshSchema();
                }
                
                GUILayout.FlexibleSpace();
                
                if (_currentSchema != null)
                {
                    EditorGUILayout.LabelField($"Version: {_currentSchema.Version}", GUILayout.Width(80));
                }
            }
            EditorGUILayout.EndHorizontal();
            
            if (_currentSchema == null)
            {
                EditorGUILayout.HelpBox("No persistent schema found. Add [PersistentSchema] to your data class.", MessageType.Info);
                return;
            }
            
            // Size summary
            DrawSizeBar();
            
            EditorGUILayout.Space();
            
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            {
                // Current schema fields
                DrawCurrentSchema();
                
                EditorGUILayout.Space();
                
                // Schema history
                _showHistory = EditorGUILayout.Foldout(_showHistory, "Schema History", true);
                if (_showHistory)
                {
                    DrawHistory();
                }
                
                EditorGUILayout.Space();
                
                // Migration
                if (_pendingMigration != null)
                {
                    DrawMigration();
                }
            }
            EditorGUILayout.EndScrollView();
            
            // Actions
            EditorGUILayout.Space();
            DrawActions();
        }
        
        private void DrawSizeBar()
        {
            var ratio = (float)_currentSchema.TotalMaxSize / (100 * 1024);
            var color = ratio < 0.5f ? Color.green : ratio < 0.8f ? Color.yellow : Color.red;
            
            EditorGUILayout.BeginHorizontal();
            {
                EditorGUILayout.LabelField("Storage Used:", GUILayout.Width(100));
                
                var rect = GUILayoutUtility.GetRect(200, 20);
                EditorGUI.DrawRect(rect, new Color(0.2f, 0.2f, 0.2f));
                
                var fillRect = rect;
                fillRect.width *= Mathf.Clamp01(ratio);
                EditorGUI.DrawRect(fillRect, color);
                
                var text = $"{_currentSchema.TotalMaxSize / 1024f:F1} / 100 KB ({ratio * 100:F0}%)";
                EditorGUI.LabelField(rect, text, EditorStyles.centeredGreyMiniLabel);
            }
            EditorGUILayout.EndHorizontal();
            
            // Warnings
            if (ratio > 0.8f)
            {
                EditorGUILayout.HelpBox(
                    "Schema approaching 100KB limit. Consider removing unused fields or compressing data.",
                    MessageType.Warning);
            }
        }
        
        private void DrawCurrentSchema()
        {
            EditorGUILayout.LabelField("Current Schema", EditorStyles.boldLabel);
            
            // Table header
            EditorGUILayout.BeginHorizontal();
            {
                EditorGUILayout.LabelField("Field", EditorStyles.miniLabel, GUILayout.Width(150));
                EditorGUILayout.LabelField("Type", EditorStyles.miniLabel, GUILayout.Width(100));
                EditorGUILayout.LabelField("Key", EditorStyles.miniLabel, GUILayout.Width(100));
                EditorGUILayout.LabelField("Size", EditorStyles.miniLabel, GUILayout.Width(80));
                EditorGUILayout.LabelField("Version", EditorStyles.miniLabel, GUILayout.Width(50));
            }
            EditorGUILayout.EndHorizontal();
            
            // Fields
            foreach (var field in _currentSchema.Fields.OrderByDescending(f => f.Size.MaxBytes))
            {
                EditorGUILayout.BeginHorizontal();
                {
                    EditorGUILayout.LabelField(field.Name, GUILayout.Width(150));
                    EditorGUILayout.LabelField(field.Type.Name, GUILayout.Width(100));
                    EditorGUILayout.LabelField(field.Key, GUILayout.Width(100));
                    
                    var sizeText = field.Size.MinBytes == field.Size.MaxBytes
                        ? $"{field.Size.MaxBytes} B"
                        : $"{field.Size.MinBytes}–{field.Size.MaxBytes} B";
                    EditorGUILayout.LabelField(sizeText, GUILayout.Width(80));
                    
                    EditorGUILayout.LabelField($"v{field.AddedInVersion}", GUILayout.Width(50));
                }
                EditorGUILayout.EndHorizontal();
            }
        }
        
        private void DrawHistory()
        {
            EditorGUI.indentLevel++;
            
            if (_history.Snapshots.Count == 0)
            {
                EditorGUILayout.LabelField("No history recorded yet.");
            }
            else
            {
                foreach (var snapshot in _history.Snapshots.OrderByDescending(s => s.Version))
                {
                    EditorGUILayout.BeginHorizontal();
                    {
                        EditorGUILayout.LabelField($"v{snapshot.Version}", GUILayout.Width(50));
                        EditorGUILayout.LabelField(snapshot.Timestamp.ToString("yyyy-MM-dd HH:mm"), GUILayout.Width(120));
                        EditorGUILayout.LabelField($"{snapshot.FieldNames.Count} fields");
                        
                        if (snapshot.Version < _currentSchema.Version)
                        {
                            if (GUILayout.Button("Compare", GUILayout.Width(70)))
                            {
                                _pendingMigration = _history.Compare(snapshot.Version, _currentSchema.Version);
                                _generatedMigration = new MigrationGenerator()
                                    .GenerateMigrationCode(_pendingMigration, _currentSchema);
                            }
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }
            
            EditorGUI.indentLevel--;
        }
        
        private void DrawMigration()
        {
            EditorGUILayout.LabelField(
                $"Migration: v{_pendingMigration.FromVersion} → v{_pendingMigration.ToVersion}",
                EditorStyles.boldLabel);
            
            // Summary
            if (_pendingMigration.IsBackwardCompatible)
            {
                EditorGUILayout.HelpBox("✓ Backward compatible (additive changes only)", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("⚠ Breaking changes detected - migration required", MessageType.Warning);
            }
            
            // Changes
            if (_pendingMigration.AddedFields.Count > 0)
            {
                EditorGUILayout.LabelField("Added fields:", EditorStyles.miniLabel);
                foreach (var field in _pendingMigration.AddedFields)
                {
                    EditorGUILayout.LabelField($"  + {field}", EditorStyles.miniLabel);
                }
            }
            
            if (_pendingMigration.RemovedFields.Count > 0)
            {
                EditorGUILayout.LabelField("Removed fields:", EditorStyles.miniLabel);
                foreach (var field in _pendingMigration.RemovedFields)
                {
                    EditorGUILayout.LabelField($"  - {field}", EditorStyles.miniLabel);
                }
            }
            
            if (_pendingMigration.ChangedTypes.Count > 0)
            {
                EditorGUILayout.LabelField("Type changes:", EditorStyles.miniLabel);
                foreach (var (field, (from, to)) in _pendingMigration.ChangedTypes)
                {
                    EditorGUILayout.LabelField($"  ~ {field}: {from} → {to}", EditorStyles.miniLabel);
                }
            }
            
            // Generated code
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Generated Migration Code:", EditorStyles.miniLabel);
            EditorGUILayout.TextArea(_generatedMigration, GUILayout.Height(150));
            
            EditorGUILayout.BeginHorizontal();
            {
                if (GUILayout.Button("Copy to Clipboard"))
                {
                    GUIUtility.systemCopyBuffer = _generatedMigration;
                }
                if (GUILayout.Button("Save to File"))
                {
                    var path = EditorUtility.SaveFilePanel(
                        "Save Migration",
                        "Assets/Scripts",
                        $"Migration_v{_pendingMigration.FromVersion}_to_v{_pendingMigration.ToVersion}.cs",
                        "cs");
                    
                    if (!string.IsNullOrEmpty(path))
                    {
                        File.WriteAllText(path, _generatedMigration);
                        AssetDatabase.Refresh();
                    }
                }
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawActions()
        {
            EditorGUILayout.BeginHorizontal();
            {
                if (GUILayout.Button("Record Current Schema"))
                {
                    _history.RecordSnapshot(_currentSchema);
                    Debug.Log($"Recorded schema v{_currentSchema.Version}");
                }
                
                if (GUILayout.Button("Bump Version"))
                {
                    // This would modify the source file to increment version
                    Debug.Log("Version bump requires manual code change");
                }
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
```

---

# 4. World Validator

## Problem Statement

Common bugs are discovered only in published worlds:
- Null reference exceptions on player leave
- Uninitialized synced arrays
- GetComponent in Update loops
- Network events on [LocalOnly] methods

## Solution

A pre-publish validation suite that catches issues statically.

## Implementation

### 4.1 Validation Framework

```csharp
namespace UdonSharpCE.Editor.Validation
{
    public interface IValidator
    {
        string Name { get; }
        string Category { get; }
        IEnumerable<ValidationIssue> Validate(ValidationContext context);
    }
    
    public class ValidationContext
    {
        public List<UdonSharpBehaviour> Behaviours { get; set; }
        public List<Type> BehaviourTypes { get; set; }
        public Dictionary<Type, List<MethodInfo>> Methods { get; set; }
        public Dictionary<Type, List<FieldInfo>> Fields { get; set; }
    }
    
    public class ValidationIssue
    {
        public Severity Severity { get; set; }
        public string ValidatorName { get; set; }
        public string Message { get; set; }
        public string Details { get; set; }
        public UnityEngine.Object Context { get; set; }  // For ping/selection
        public string FilePath { get; set; }
        public int LineNumber { get; set; }
        public Func<bool> AutoFix { get; set; }  // Optional auto-fix
    }
    
    public enum Severity
    {
        Error,      // Will cause runtime failure
        Warning,    // Likely bug or performance issue
        Info,       // Suggestion or style issue
        Hidden      // Passed check (for reporting)
    }
}
```

### 4.2 Validators

```csharp
namespace UdonSharpCE.Editor.Validation.Validators
{
    /// <summary>
    /// Checks for GetComponent calls in Update/FixedUpdate/LateUpdate.
    /// </summary>
    public class GetComponentInUpdateValidator : IValidator
    {
        public string Name => "GetComponent in Update";
        public string Category => "Performance";
        
        public IEnumerable<ValidationIssue> Validate(ValidationContext context)
        {
            var updateMethods = new[] { "Update", "FixedUpdate", "LateUpdate" };
            
            foreach (var type in context.BehaviourTypes)
            {
                foreach (var methodName in updateMethods)
                {
                    var method = type.GetMethod(methodName, 
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    
                    if (method == null) continue;
                    
                    // Analyze method body for GetComponent calls
                    var calls = AnalyzeMethodForCalls(method, "GetComponent");
                    
                    foreach (var call in calls)
                    {
                        yield return new ValidationIssue
                        {
                            Severity = Severity.Warning,
                            ValidatorName = Name,
                            Message = $"GetComponent<{call.GenericArg}>() called in {methodName}",
                            Details = "GetComponent is expensive. Cache the result in Start() or Awake().",
                            FilePath = GetSourceFile(type),
                            LineNumber = call.LineNumber,
                            AutoFix = () => GenerateCachedField(type, method, call)
                        };
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Checks for uninitialized synced arrays.
    /// </summary>
    public class UninitializedSyncedArrayValidator : IValidator
    {
        public string Name => "Uninitialized Synced Array";
        public string Category => "Networking";
        
        public IEnumerable<ValidationIssue> Validate(ValidationContext context)
        {
            foreach (var type in context.BehaviourTypes)
            {
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(f => f.FieldType.IsArray && 
                               f.GetCustomAttribute<UdonSyncedAttribute>() != null);
                
                foreach (var field in fields)
                {
                    // Check if field is initialized in declaration or Awake/Start
                    if (!IsFieldInitialized(type, field))
                    {
                        yield return new ValidationIssue
                        {
                            Severity = Severity.Error,
                            ValidatorName = Name,
                            Message = $"Synced array '{field.Name}' is not initialized",
                            Details = "Uninitialized synced arrays cause NullReferenceException on join. Initialize in field declaration or Awake().",
                            FilePath = GetSourceFile(type),
                            AutoFix = () => AddFieldInitializer(type, field)
                        };
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Checks for VRCPlayerApi usage after player left.
    /// </summary>
    public class PlayerApiAfterLeaveValidator : IValidator
    {
        public string Name => "Player API After Leave";
        public string Category => "Safety";
        
        public IEnumerable<ValidationIssue> Validate(ValidationContext context)
        {
            foreach (var type in context.BehaviourTypes)
            {
                var onPlayerLeft = type.GetMethod("OnPlayerLeft",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (onPlayerLeft == null) continue;
                
                // Check if the player parameter is used after any async operation
                // or stored in a field
                var issues = AnalyzePlayerApiUsage(onPlayerLeft);
                
                foreach (var issue in issues)
                {
                    yield return new ValidationIssue
                    {
                        Severity = Severity.Error,
                        ValidatorName = Name,
                        Message = $"VRCPlayerApi may be invalid in OnPlayerLeft",
                        Details = issue,
                        FilePath = GetSourceFile(type)
                    };
                }
            }
        }
    }
    
    /// <summary>
    /// Checks for [LocalOnly] methods called via SendCustomNetworkEvent.
    /// </summary>
    public class LocalOnlyNetworkCallValidator : IValidator
    {
        public string Name => "LocalOnly Network Call";
        public string Category => "Networking";
        
        public IEnumerable<ValidationIssue> Validate(ValidationContext context)
        {
            foreach (var type in context.BehaviourTypes)
            {
                var localOnlyMethods = type.GetMethods()
                    .Where(m => m.GetCustomAttribute<LocalOnlyAttribute>() != null)
                    .Select(m => m.Name)
                    .ToHashSet();
                
                if (localOnlyMethods.Count == 0) continue;
                
                // Find all SendCustomNetworkEvent calls in this type
                var networkCalls = FindNetworkEventCalls(type);
                
                foreach (var call in networkCalls)
                {
                    if (localOnlyMethods.Contains(call.EventName))
                    {
                        yield return new ValidationIssue
                        {
                            Severity = Severity.Error,
                            ValidatorName = Name,
                            Message = $"SendCustomNetworkEvent targets [LocalOnly] method '{call.EventName}'",
                            Details = "This will fail at runtime. Remove [LocalOnly] or use SendCustomEvent instead.",
                            FilePath = GetSourceFile(type),
                            LineNumber = call.LineNumber
                        };
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Checks for continuous sync on rarely-changed data.
    /// </summary>
    public class SyncModeValidator : IValidator
    {
        public string Name => "Sync Mode Appropriateness";
        public string Category => "Performance";
        
        public IEnumerable<ValidationIssue> Validate(ValidationContext context)
        {
            foreach (var type in context.BehaviourTypes)
            {
                var syncMode = GetSyncMode(type);
                if (syncMode != BehaviourSyncMode.Continuous) continue;
                
                var syncedFields = type.GetFields()
                    .Where(f => f.GetCustomAttribute<UdonSyncedAttribute>() != null)
                    .ToList();
                
                // Check if any fields are only set in Start/Awake (static after init)
                foreach (var field in syncedFields)
                {
                    var writes = FindFieldWrites(type, field);
                    
                    if (writes.All(w => w.Method == "Start" || w.Method == "Awake" || w.Method == ".ctor"))
                    {
                        yield return new ValidationIssue
                        {
                            Severity = Severity.Warning,
                            ValidatorName = Name,
                            Message = $"Field '{field.Name}' is only set during initialization but uses Continuous sync",
                            Details = "Consider Manual sync for data that doesn't change frequently.",
                            FilePath = GetSourceFile(type)
                        };
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Bandwidth limit check (delegates to Bandwidth Analyzer).
    /// </summary>
    public class BandwidthValidator : IValidator
    {
        public string Name => "Bandwidth Limits";
        public string Category => "Networking";
        
        public IEnumerable<ValidationIssue> Validate(ValidationContext context)
        {
            var analyzer = new WorldAnalyzer();
            var result = analyzer.AnalyzeScene();
            
            foreach (var violation in result.Violations)
            {
                yield return new ValidationIssue
                {
                    Severity = violation.Severity,
                    ValidatorName = Name,
                    Message = violation.Message,
                    Details = violation.Recommendation
                };
            }
            
            foreach (var behaviour in result.BehaviourResults)
            {
                foreach (var violation in behaviour.Violations)
                {
                    yield return new ValidationIssue
                    {
                        Severity = violation.Severity,
                        ValidatorName = Name,
                        Message = $"{behaviour.BehaviourType.Name}: {violation.Message}",
                        Details = violation.Recommendation
                    };
                }
            }
        }
    }
    
    /// <summary>
    /// Persistence size check.
    /// </summary>
    public class PersistenceSizeValidator : IValidator
    {
        public string Name => "Persistence Size";
        public string Category => "Persistence";
        
        public IEnumerable<ValidationIssue> Validate(ValidationContext context)
        {
            var extractor = new SchemaExtractor();
            var schemaTypes = TypeCache.GetTypesWithAttribute<PersistentSchemaAttribute>();
            
            foreach (var type in schemaTypes)
            {
                var schema = extractor.ExtractFromType(type);
                
                if (schema.TotalMaxSize > 100 * 1024)
                {
                    yield return new ValidationIssue
                    {
                        Severity = Severity.Error,
                        ValidatorName = Name,
                        Message = $"Schema '{type.Name}' exceeds 100KB limit ({schema.TotalMaxSize / 1024f:F1} KB)",
                        Details = "Reduce field sizes, remove unused fields, or compress data."
                    };
                }
                else if (schema.TotalMaxSize > 80 * 1024)
                {
                    yield return new ValidationIssue
                    {
                        Severity = Severity.Warning,
                        ValidatorName = Name,
                        Message = $"Schema '{type.Name}' approaching 100KB limit ({schema.TotalMaxSize / 1024f:F1} KB)",
                        Details = "Consider optimizing to leave headroom for future additions."
                    };
                }
            }
        }
    }
}
```

### 4.3 Validation Runner

```csharp
namespace UdonSharpCE.Editor.Validation
{
    public class ValidationRunner
    {
        private List<IValidator> _validators;
        
        public ValidationRunner()
        {
            // Discover all validators
            _validators = TypeCache.GetTypesDerivedFrom<IValidator>()
                .Where(t => !t.IsAbstract)
                .Select(t => (IValidator)Activator.CreateInstance(t))
                .ToList();
        }
        
        public ValidationReport RunAll()
        {
            var context = BuildContext();
            var report = new ValidationReport();
            
            foreach (var validator in _validators)
            {
                try
                {
                    var issues = validator.Validate(context).ToList();
                    report.Issues.AddRange(issues);
                    
                    report.ValidatorResults[validator.Name] = new ValidatorResult
                    {
                        Passed = issues.All(i => i.Severity != Severity.Error),
                        ErrorCount = issues.Count(i => i.Severity == Severity.Error),
                        WarningCount = issues.Count(i => i.Severity == Severity.Warning)
                    };
                }
                catch (Exception ex)
                {
                    report.Issues.Add(new ValidationIssue
                    {
                        Severity = Severity.Error,
                        ValidatorName = validator.Name,
                        Message = $"Validator crashed: {ex.Message}",
                        Details = ex.StackTrace
                    });
                }
            }
            
            report.TotalErrors = report.Issues.Count(i => i.Severity == Severity.Error);
            report.TotalWarnings = report.Issues.Count(i => i.Severity == Severity.Warning);
            report.AllPassed = report.TotalErrors == 0;
            
            return report;
        }
        
        private ValidationContext BuildContext()
        {
            var behaviours = FindObjectsOfType<UdonSharpBehaviour>().ToList();
            var types = behaviours.Select(b => b.GetType()).Distinct().ToList();
            
            return new ValidationContext
            {
                Behaviours = behaviours,
                BehaviourTypes = types,
                Methods = types.ToDictionary(
                    t => t,
                    t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).ToList()
                ),
                Fields = types.ToDictionary(
                    t => t,
                    t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).ToList()
                )
            };
        }
    }
    
    public class ValidationReport
    {
        public List<ValidationIssue> Issues { get; } = new();
        public Dictionary<string, ValidatorResult> ValidatorResults { get; } = new();
        public int TotalErrors { get; set; }
        public int TotalWarnings { get; set; }
        public bool AllPassed { get; set; }
    }
}
```

### 4.4 Editor Window

```csharp
namespace UdonSharpCE.Editor.Validation
{
    public class WorldValidatorWindow : EditorWindow
    {
        [MenuItem("CE Tools/World Validator")]
        public static void ShowWindow()
        {
            GetWindow<WorldValidatorWindow>("CE World Validator");
        }
        
        private ValidationReport _report;
        private Vector2 _scrollPos;
        private string _filterCategory = "All";
        private Severity _minSeverity = Severity.Info;
        
        private void OnGUI()
        {
            // Toolbar
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            {
                if (GUILayout.Button("Run All Checks", EditorStyles.toolbarButton))
                {
                    _report = new ValidationRunner().RunAll();
                }
                
                GUILayout.FlexibleSpace();
                
                if (_report != null)
                {
                    var statusColor = _report.AllPassed ? Color.green : Color.red;
                    GUI.color = statusColor;
                    EditorGUILayout.LabelField(
                        _report.AllPassed ? "✓ All Passed" : $"✗ {_report.TotalErrors} Errors",
                        GUILayout.Width(100));
                    GUI.color = Color.white;
                }
            }
            EditorGUILayout.EndHorizontal();
            
            if (_report == null)
            {
                EditorGUILayout.HelpBox("Click 'Run All Checks' to validate your world", MessageType.Info);
                return;
            }
            
            // Summary
            DrawSummary();
            
            // Filters
            EditorGUILayout.BeginHorizontal();
            {
                EditorGUILayout.LabelField("Filter:", GUILayout.Width(50));
                
                var categories = new[] { "All" }.Concat(
                    _report.Issues.Select(i => i.ValidatorName).Distinct()
                ).ToArray();
                
                var catIndex = Array.IndexOf(categories, _filterCategory);
                catIndex = EditorGUILayout.Popup(catIndex, categories, GUILayout.Width(150));
                _filterCategory = categories[Mathf.Max(0, catIndex)];
                
                _minSeverity = (Severity)EditorGUILayout.EnumPopup(_minSeverity, GUILayout.Width(80));
            }
            EditorGUILayout.EndHorizontal();
            
            // Issues list
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            {
                var filtered = _report.Issues
                    .Where(i => _filterCategory == "All" || i.ValidatorName == _filterCategory)
                    .Where(i => i.Severity <= _minSeverity)
                    .OrderBy(i => i.Severity);
                
                foreach (var issue in filtered)
                {
                    DrawIssue(issue);
                }
            }
            EditorGUILayout.EndScrollView();
            
            // Actions
            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            {
                var fixableCount = _report.Issues.Count(i => i.AutoFix != null);
                GUI.enabled = fixableCount > 0;
                if (GUILayout.Button($"Fix All Auto-Fixable ({fixableCount})"))
                {
                    foreach (var issue in _report.Issues.Where(i => i.AutoFix != null))
                    {
                        issue.AutoFix();
                    }
                    AssetDatabase.Refresh();
                    _report = new ValidationRunner().RunAll();
                }
                GUI.enabled = true;
                
                if (GUILayout.Button("Export Report"))
                {
                    ExportReport();
                }
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawSummary()
        {
            EditorGUILayout.BeginHorizontal();
            {
                // Error count
                GUI.color = _report.TotalErrors > 0 ? Color.red : Color.green;
                EditorGUILayout.LabelField($"Errors: {_report.TotalErrors}", GUILayout.Width(80));
                
                // Warning count
                GUI.color = _report.TotalWarnings > 0 ? Color.yellow : Color.green;
                EditorGUILayout.LabelField($"Warnings: {_report.TotalWarnings}", GUILayout.Width(100));
                
                GUI.color = Color.white;
                
                // Info count
                var infoCount = _report.Issues.Count(i => i.Severity == Severity.Info);
                EditorGUILayout.LabelField($"Info: {infoCount}", GUILayout.Width(80));
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawIssue(ValidationIssue issue)
        {
            var bgColor = issue.Severity switch
            {
                Severity.Error => new Color(0.5f, 0.2f, 0.2f),
                Severity.Warning => new Color(0.5f, 0.4f, 0.2f),
                _ => new Color(0.3f, 0.3f, 0.3f)
            };
            
            var icon = issue.Severity switch
            {
                Severity.Error => "✗",
                Severity.Warning => "⚠",
                _ => "ℹ"
            };
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                EditorGUILayout.BeginHorizontal();
                {
                    EditorGUILayout.LabelField($"{icon} [{issue.ValidatorName}]", EditorStyles.boldLabel, GUILayout.Width(200));
                    EditorGUILayout.LabelField(issue.Message);
                    
                    if (issue.Context != null && GUILayout.Button("→", GUILayout.Width(25)))
                    {
                        Selection.activeObject = issue.Context;
                        EditorGUIUtility.PingObject(issue.Context);
                    }
                    
                    if (issue.AutoFix != null && GUILayout.Button("Fix", GUILayout.Width(40)))
                    {
                        issue.AutoFix();
                        AssetDatabase.Refresh();
                    }
                }
                EditorGUILayout.EndHorizontal();
                
                if (!string.IsNullOrEmpty(issue.Details))
                {
                    EditorGUILayout.LabelField(issue.Details, EditorStyles.wordWrappedMiniLabel);
                }
                
                if (!string.IsNullOrEmpty(issue.FilePath))
                {
                    EditorGUILayout.BeginHorizontal();
                    {
                        var location = issue.LineNumber > 0 
                            ? $"{issue.FilePath}:{issue.LineNumber}" 
                            : issue.FilePath;
                        
                        if (GUILayout.Button(location, EditorStyles.linkLabel))
                        {
                            // Open file at line
                            UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(
                                issue.FilePath, issue.LineNumber);
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndVertical();
        }
        
        private void ExportReport()
        {
            var path = EditorUtility.SaveFilePanel("Save Validation Report", "", "validation_report.md", "md");
            if (string.IsNullOrEmpty(path)) return;
            
            var sb = new StringBuilder();
            sb.AppendLine("# CE World Validation Report");
            sb.AppendLine($"Generated: {DateTime.Now}");
            sb.AppendLine();
            sb.AppendLine($"## Summary");
            sb.AppendLine($"- Errors: {_report.TotalErrors}");
            sb.AppendLine($"- Warnings: {_report.TotalWarnings}");
            sb.AppendLine($"- Status: {(_report.AllPassed ? "PASSED" : "FAILED")}");
            sb.AppendLine();
            
            foreach (var group in _report.Issues.GroupBy(i => i.Severity).OrderBy(g => g.Key))
            {
                sb.AppendLine($"## {group.Key}s");
                foreach (var issue in group)
                {
                    sb.AppendLine($"- **[{issue.ValidatorName}]** {issue.Message}");
                    if (!string.IsNullOrEmpty(issue.Details))
                        sb.AppendLine($"  - {issue.Details}");
                }
                sb.AppendLine();
            }
            
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"Report saved to {path}");
        }
    }
}
```

---

# 5. Async Visualizer

## Problem Statement

CE.Async compiles `async`/`await` to state machines. When debugging:
- Stack traces don't match source code
- Can't see which state the task is in
- Local variables are stored in generated arrays
- Hard to understand what compiled

## Implementation

### 5.1 Debug Metadata Emission

```csharp
namespace UdonSharpCE.Compiler.Async
{
    /// <summary>
    /// Emits debug metadata during async compilation for visualizer.
    /// </summary>
    public class AsyncDebugEmitter
    {
        public AsyncDebugInfo EmitDebugInfo(MethodDefinition method, StateMachine stateMachine)
        {
            var info = new AsyncDebugInfo
            {
                MethodName = method.FullName,
                SourceFile = method.SourceFile,
                States = new List<StateDebugInfo>()
            };
            
            foreach (var state in stateMachine.States)
            {
                info.States.Add(new StateDebugInfo
                {
                    StateId = state.Id,
                    Name = state.Name ?? $"State_{state.Id}",
                    SourceLine = state.SourceLine,
                    SourceColumn = state.SourceColumn,
                    AwaitExpression = state.AwaitExpression?.ToString(),
                    LocalVariables = state.CapturedLocals.Select(l => new LocalDebugInfo
                    {
                        Name = l.Name,
                        Type = l.Type.Name,
                        StorageIndex = l.StorageIndex
                    }).ToList(),
                    Transitions = state.Transitions.Select(t => new TransitionDebugInfo
                    {
                        TargetState = t.TargetState,
                        Condition = t.Condition?.ToString() ?? "unconditional"
                    }).ToList()
                });
            }
            
            // Save to asset
            var assetPath = $"Assets/CE/Debug/Async/{SanitizePath(method.FullName)}.json";
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath));
            File.WriteAllText(assetPath, JsonUtility.ToJson(info, true));
            
            return info;
        }
    }
    
    [Serializable]
    public class AsyncDebugInfo
    {
        public string MethodName;
        public string SourceFile;
        public List<StateDebugInfo> States;
    }
    
    [Serializable]
    public class StateDebugInfo
    {
        public int StateId;
        public string Name;
        public int SourceLine;
        public int SourceColumn;
        public string AwaitExpression;
        public List<LocalDebugInfo> LocalVariables;
        public List<TransitionDebugInfo> Transitions;
    }
}
```

### 5.2 Runtime State Tracker

```csharp
namespace UdonSharpCE.Runtime.Async
{
    /// <summary>
    /// Tracks active async tasks for debugging.
    /// Only active in editor/debug builds.
    /// </summary>
    public static class AsyncDebugTracker
    {
        #if UNITY_EDITOR || CE_DEBUG
        
        private static Dictionary<int, TaskDebugState> _activeTasks = new();
        private static int _nextTaskId;
        
        public static event Action<TaskDebugState> OnTaskStarted;
        public static event Action<TaskDebugState> OnTaskStateChanged;
        public static event Action<TaskDebugState> OnTaskCompleted;
        
        public static int RegisterTask(UdonSharpBehaviour owner, string methodName)
        {
            var id = _nextTaskId++;
            var state = new TaskDebugState
            {
                TaskId = id,
                Owner = owner,
                MethodName = methodName,
                StartTime = Time.realtimeSinceStartup,
                CurrentState = 0,
                IsComplete = false
            };
            
            _activeTasks[id] = state;
            OnTaskStarted?.Invoke(state);
            
            return id;
        }
        
        public static void UpdateState(int taskId, int newState, object[] locals)
        {
            if (!_activeTasks.TryGetValue(taskId, out var state)) return;
            
            state.CurrentState = newState;
            state.StateEnteredTime = Time.realtimeSinceStartup;
            state.LocalValues = locals?.ToArray();
            
            OnTaskStateChanged?.Invoke(state);
        }
        
        public static void CompleteTask(int taskId, bool success, object result = null)
        {
            if (!_activeTasks.TryGetValue(taskId, out var state)) return;
            
            state.IsComplete = true;
            state.Success = success;
            state.Result = result;
            state.EndTime = Time.realtimeSinceStartup;
            
            OnTaskCompleted?.Invoke(state);
            
            // Keep for a bit for debugging, then remove
            // (In practice, use a ring buffer)
        }
        
        public static IReadOnlyDictionary<int, TaskDebugState> ActiveTasks => _activeTasks;
        
        #endif
    }
    
    public class TaskDebugState
    {
        public int TaskId;
        public UdonSharpBehaviour Owner;
        public string MethodName;
        public float StartTime;
        public float StateEnteredTime;
        public float EndTime;
        public int CurrentState;
        public bool IsComplete;
        public bool Success;
        public object Result;
        public object[] LocalValues;
    }
}
```

### 5.3 Editor Window

```csharp
namespace UdonSharpCE.Editor.Async
{
    public class AsyncVisualizerWindow : EditorWindow
    {
        [MenuItem("CE Tools/Async Visualizer")]
        public static void ShowWindow()
        {
            GetWindow<AsyncVisualizerWindow>("CE Async Visualizer");
        }
        
        private TaskDebugState _selectedTask;
        private AsyncDebugInfo _selectedDebugInfo;
        private Vector2 _taskListScroll;
        private Vector2 _graphScroll;
        
        private void OnEnable()
        {
            AsyncDebugTracker.OnTaskStarted += OnTaskEvent;
            AsyncDebugTracker.OnTaskStateChanged += OnTaskEvent;
            AsyncDebugTracker.OnTaskCompleted += OnTaskEvent;
        }
        
        private void OnDisable()
        {
            AsyncDebugTracker.OnTaskStarted -= OnTaskEvent;
            AsyncDebugTracker.OnTaskStateChanged -= OnTaskEvent;
            AsyncDebugTracker.OnTaskCompleted -= OnTaskEvent;
        }
        
        private void OnTaskEvent(TaskDebugState state)
        {
            Repaint();
        }
        
        private void OnGUI()
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to track async tasks", MessageType.Info);
                
                // Can still view compiled state machines
                DrawStaticAnalysis();
                return;
            }
            
            EditorGUILayout.BeginHorizontal();
            {
                // Left: Task list
                EditorGUILayout.BeginVertical(GUILayout.Width(250));
                {
                    DrawTaskList();
                }
                EditorGUILayout.EndVertical();
                
                // Right: Selected task details
                EditorGUILayout.BeginVertical();
                {
                    if (_selectedTask != null)
                    {
                        DrawTaskDetails();
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("Select a task to view details", MessageType.Info);
                    }
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawTaskList()
        {
            EditorGUILayout.LabelField("Active Tasks", EditorStyles.boldLabel);
            
            _taskListScroll = EditorGUILayout.BeginScrollView(_taskListScroll);
            {
                foreach (var (id, task) in AsyncDebugTracker.ActiveTasks)
                {
                    var style = task == _selectedTask ? "selectionRect" : "box";
                    var statusIcon = task.IsComplete 
                        ? (task.Success ? "✓" : "✗") 
                        : "▶";
                    var statusColor = task.IsComplete
                        ? (task.Success ? Color.green : Color.red)
                        : Color.yellow;
                    
                    EditorGUILayout.BeginHorizontal(style);
                    {
                        GUI.color = statusColor;
                        EditorGUILayout.LabelField(statusIcon, GUILayout.Width(20));
                        GUI.color = Color.white;
                        
                        if (GUILayout.Button($"{task.MethodName}", EditorStyles.label))
                        {
                            _selectedTask = task;
                            _selectedDebugInfo = LoadDebugInfo(task.MethodName);
                        }
                        
                        EditorGUILayout.LabelField($"S{task.CurrentState}", GUILayout.Width(30));
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndScrollView();
        }
        
        private void DrawTaskDetails()
        {
            EditorGUILayout.LabelField($"Task: {_selectedTask.MethodName}", EditorStyles.boldLabel);
            
            // Basic info
            EditorGUILayout.LabelField($"Owner: {_selectedTask.Owner?.name ?? "null"}");
            EditorGUILayout.LabelField($"Status: {(_selectedTask.IsComplete ? "Complete" : "Running")}");
            
            var elapsed = (_selectedTask.IsComplete ? _selectedTask.EndTime : Time.realtimeSinceStartup) 
                         - _selectedTask.StartTime;
            EditorGUILayout.LabelField($"Elapsed: {elapsed:F2}s");
            
            EditorGUILayout.Space();
            
            // State machine visualization
            if (_selectedDebugInfo != null)
            {
                DrawStateMachineGraph();
            }
            
            EditorGUILayout.Space();
            
            // Current state details
            if (_selectedDebugInfo != null && 
                _selectedTask.CurrentState < _selectedDebugInfo.States.Count)
            {
                var stateInfo = _selectedDebugInfo.States[_selectedTask.CurrentState];
                
                EditorGUILayout.LabelField($"Current State: {stateInfo.Name}", EditorStyles.boldLabel);
                
                // Source location
                if (stateInfo.SourceLine > 0)
                {
                    if (GUILayout.Button($"{_selectedDebugInfo.SourceFile}:{stateInfo.SourceLine}", EditorStyles.linkLabel))
                    {
                        UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(
                            _selectedDebugInfo.SourceFile, stateInfo.SourceLine);
                    }
                }
                
                // Await expression
                if (!string.IsNullOrEmpty(stateInfo.AwaitExpression))
                {
                    EditorGUILayout.LabelField($"Awaiting: {stateInfo.AwaitExpression}");
                    
                    var timeInState = Time.realtimeSinceStartup - _selectedTask.StateEnteredTime;
                    EditorGUILayout.LabelField($"Time in state: {timeInState:F2}s");
                }
                
                // Local variables
                if (stateInfo.LocalVariables.Count > 0 && _selectedTask.LocalValues != null)
                {
                    EditorGUILayout.LabelField("Local Variables:", EditorStyles.miniLabel);
                    EditorGUI.indentLevel++;
                    
                    foreach (var local in stateInfo.LocalVariables)
                    {
                        var value = local.StorageIndex < _selectedTask.LocalValues.Length
                            ? _selectedTask.LocalValues[local.StorageIndex]
                            : "<unavailable>";
                        
                        EditorGUILayout.LabelField($"{local.Name} ({local.Type}): {value}");
                    }
                    
                    EditorGUI.indentLevel--;
                }
            }
            
            // Debug controls
            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            {
                GUI.enabled = !_selectedTask.IsComplete;
                
                if (GUILayout.Button("Pause"))
                {
                    // Would need runtime support
                }
                if (GUILayout.Button("Step"))
                {
                    // Would need runtime support
                }
                if (GUILayout.Button("Cancel"))
                {
                    // Trigger cancellation token
                }
                
                GUI.enabled = true;
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawStateMachineGraph()
        {
            EditorGUILayout.LabelField("State Machine", EditorStyles.boldLabel);
            
            _graphScroll = EditorGUILayout.BeginScrollView(_graphScroll, GUILayout.Height(200));
            {
                var rect = GUILayoutUtility.GetRect(
                    _selectedDebugInfo.States.Count * 120, 
                    150);
                
                // Draw states
                var stateRects = new Rect[_selectedDebugInfo.States.Count];
                for (int i = 0; i < _selectedDebugInfo.States.Count; i++)
                {
                    var state = _selectedDebugInfo.States[i];
                    var x = 20 + i * 120;
                    var y = 50;
                    var stateRect = new Rect(x, y, 100, 50);
                    stateRects[i] = stateRect;
                    
                    // Highlight current state
                    var bgColor = i == _selectedTask.CurrentState
                        ? new Color(0.2f, 0.6f, 0.2f)
                        : new Color(0.3f, 0.3f, 0.3f);
                    
                    EditorGUI.DrawRect(stateRect, bgColor);
                    GUI.Label(stateRect, state.Name, EditorStyles.centeredGreyMiniLabel);
                }
                
                // Draw transitions
                Handles.BeginGUI();
                foreach (var state in _selectedDebugInfo.States)
                {
                    foreach (var transition in state.Transitions)
                    {
                        if (transition.TargetState < stateRects.Length)
                        {
                            var from = stateRects[state.StateId];
                            var to = stateRects[transition.TargetState];
                            
                            var startPoint = new Vector3(from.xMax, from.center.y, 0);
                            var endPoint = new Vector3(to.xMin, to.center.y, 0);
                            
                            Handles.color = Color.white;
                            Handles.DrawLine(startPoint, endPoint);
                            
                            // Arrow head
                            var dir = (endPoint - startPoint).normalized;
                            var arrowSize = 10f;
                            Handles.DrawLine(endPoint, endPoint - dir * arrowSize + Vector3.up * arrowSize * 0.5f);
                            Handles.DrawLine(endPoint, endPoint - dir * arrowSize - Vector3.up * arrowSize * 0.5f);
                        }
                    }
                }
                Handles.EndGUI();
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
```

---

# 6. ECS Browser

## Problem Statement

CE.Perf stores entities as struct arrays (SoA). Unity's Inspector can't visualize this.

## Implementation

### 6.1 Runtime Entity Query

```csharp
namespace UdonSharpCE.Editor.ECS
{
    public class ECSDebugBridge
    {
        /// <summary>
        /// Queries entities from CE.Perf runtime.
        /// </summary>
        public List<EntityDebugView> QueryEntities(EntityFilter filter = null)
        {
            var world = CEWorld.Instance;
            if (world == null) return new List<EntityDebugView>();
            
            var entities = new List<EntityDebugView>();
            
            for (int i = 0; i < world.EntityCount; i++)
            {
                if (!world.IsAlive(i)) continue;
                
                var view = new EntityDebugView
                {
                    Id = i,
                    Archetype = world.GetArchetype(i),
                    Components = new Dictionary<Type, object>()
                };
                
                // Get all components for this entity
                foreach (var componentType in view.Archetype.ComponentTypes)
                {
                    var value = world.GetComponentBoxed(i, componentType);
                    view.Components[componentType] = value;
                }
                
                // Apply filter
                if (filter != null && !filter.Matches(view))
                    continue;
                
                entities.Add(view);
            }
            
            return entities;
        }
        
        public void SetComponent(int entityId, Type componentType, object value)
        {
            CEWorld.Instance?.SetComponentBoxed(entityId, componentType, value);
        }
        
        public void DestroyEntity(int entityId)
        {
            CEWorld.Instance?.Destroy(entityId);
        }
        
        public int CreateEntity(params (Type type, object value)[] components)
        {
            var world = CEWorld.Instance;
            if (world == null) return -1;
            
            var id = world.CreateEntity();
            foreach (var (type, value) in components)
            {
                world.SetComponentBoxed(id, type, value);
            }
            return id;
        }
    }
    
    public class EntityDebugView
    {
        public int Id;
        public Archetype Archetype;
        public Dictionary<Type, object> Components;
        
        public T Get<T>() where T : struct
        {
            if (Components.TryGetValue(typeof(T), out var value))
                return (T)value;
            return default;
        }
        
        public bool Has<T>() where T : struct
        {
            return Components.ContainsKey(typeof(T));
        }
    }
    
    public class EntityFilter
    {
        public List<Type> RequiredComponents { get; set; } = new();
        public List<Type> ExcludedComponents { get; set; } = new();
        public Func<EntityDebugView, bool> CustomFilter { get; set; }
        
        public bool Matches(EntityDebugView entity)
        {
            foreach (var required in RequiredComponents)
            {
                if (!entity.Components.ContainsKey(required))
                    return false;
            }
            
            foreach (var excluded in ExcludedComponents)
            {
                if (entity.Components.ContainsKey(excluded))
                    return false;
            }
            
            if (CustomFilter != null && !CustomFilter(entity))
                return false;
            
            return true;
        }
    }
}
```

### 6.2 Editor Window

```csharp
namespace UdonSharpCE.Editor.ECS
{
    public class ECSBrowserWindow : EditorWindow
    {
        [MenuItem("CE Tools/ECS Browser")]
        public static void ShowWindow()
        {
            GetWindow<ECSBrowserWindow>("CE ECS Browser");
        }
        
        private ECSDebugBridge _bridge = new();
        private List<EntityDebugView> _entities = new();
        private EntityDebugView _selectedEntity;
        private EntityFilter _filter = new();
        
        private Vector2 _listScroll;
        private Vector2 _detailScroll;
        private string _searchText = "";
        private Type _filterComponentType;
        private bool _autoRefresh = true;
        private float _lastRefresh;
        
        private void OnGUI()
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to browse ECS entities", MessageType.Info);
                return;
            }
            
            // Auto-refresh
            if (_autoRefresh && Time.realtimeSinceStartup - _lastRefresh > 0.5f)
            {
                RefreshEntities();
                _lastRefresh = Time.realtimeSinceStartup;
            }
            
            // Toolbar
            DrawToolbar();
            
            EditorGUILayout.BeginHorizontal();
            {
                // Left: Entity list
                EditorGUILayout.BeginVertical(GUILayout.Width(300));
                {
                    DrawEntityList();
                }
                EditorGUILayout.EndVertical();
                
                // Right: Selected entity details
                EditorGUILayout.BeginVertical();
                {
                    if (_selectedEntity != null)
                    {
                        DrawEntityDetails();
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("Select an entity to view components", MessageType.Info);
                    }
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton))
                {
                    RefreshEntities();
                }
                
                _autoRefresh = GUILayout.Toggle(_autoRefresh, "Auto", EditorStyles.toolbarButton);
                
                GUILayout.Space(20);
                
                // Filter by component
                EditorGUILayout.LabelField("Filter:", GUILayout.Width(40));
                
                var componentTypes = GetAllComponentTypes();
                var typeNames = new[] { "All" }.Concat(componentTypes.Select(t => t.Name)).ToArray();
                var currentIndex = _filterComponentType == null ? 0 : 
                    Array.IndexOf(componentTypes, _filterComponentType) + 1;
                
                var newIndex = EditorGUILayout.Popup(currentIndex, typeNames, GUILayout.Width(120));
                _filterComponentType = newIndex == 0 ? null : componentTypes[newIndex - 1];
                
                if (_filterComponentType != null)
                {
                    _filter.RequiredComponents = new List<Type> { _filterComponentType };
                }
                else
                {
                    _filter.RequiredComponents.Clear();
                }
                
                GUILayout.FlexibleSpace();
                
                EditorGUILayout.LabelField($"Entities: {_entities.Count}", GUILayout.Width(100));
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawEntityList()
        {
            // Search
            _searchText = EditorGUILayout.TextField("Search", _searchText);
            
            // Column headers
            EditorGUILayout.BeginHorizontal();
            {
                EditorGUILayout.LabelField("ID", GUILayout.Width(40));
                EditorGUILayout.LabelField("Components");
                
                // Show filtered component value if filtering
                if (_filterComponentType != null)
                {
                    EditorGUILayout.LabelField(_filterComponentType.Name, GUILayout.Width(100));
                }
            }
            EditorGUILayout.EndHorizontal();
            
            _listScroll = EditorGUILayout.BeginScrollView(_listScroll);
            {
                foreach (var entity in _entities)
                {
                    // Search filter
                    if (!string.IsNullOrEmpty(_searchText))
                    {
                        var searchMatch = entity.Id.ToString().Contains(_searchText) ||
                            entity.Components.Keys.Any(t => t.Name.ToLower().Contains(_searchText.ToLower()));
                        if (!searchMatch) continue;
                    }
                    
                    var style = entity == _selectedEntity ? "selectionRect" : "box";
                    EditorGUILayout.BeginHorizontal(style);
                    {
                        // ID
                        if (GUILayout.Button(entity.Id.ToString(), EditorStyles.label, GUILayout.Width(40)))
                        {
                            _selectedEntity = entity;
                        }
                        
                        // Component icons/names
                        var componentStr = string.Join(", ", entity.Components.Keys.Select(t => t.Name).Take(3));
                        if (entity.Components.Count > 3)
                            componentStr += $" +{entity.Components.Count - 3}";
                        
                        EditorGUILayout.LabelField(componentStr);
                        
                        // Show filtered component value
                        if (_filterComponentType != null && entity.Components.TryGetValue(_filterComponentType, out var value))
                        {
                            EditorGUILayout.LabelField(FormatComponentValue(value), GUILayout.Width(100));
                        }
                        
                        // Highlight in scene
                        if (entity.Components.ContainsKey(typeof(Position)))
                        {
                            if (GUILayout.Button("◎", GUILayout.Width(25)))
                            {
                                var pos = (Position)entity.Components[typeof(Position)];
                                SceneView.lastActiveSceneView.LookAt(pos.Value);
                            }
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndScrollView();
            
            // Actions
            EditorGUILayout.BeginHorizontal();
            {
                if (GUILayout.Button("Create Entity"))
                {
                    var id = _bridge.CreateEntity();
                    RefreshEntities();
                    _selectedEntity = _entities.FirstOrDefault(e => e.Id == id);
                }
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawEntityDetails()
        {
            EditorGUILayout.LabelField($"Entity {_selectedEntity.Id}", EditorStyles.boldLabel);
            
            _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);
            {
                foreach (var (type, value) in _selectedEntity.Components)
                {
                    EditorGUILayout.LabelField(type.Name, EditorStyles.boldLabel);
                    EditorGUI.indentLevel++;
                    
                    // Draw component fields
                    var newValue = DrawComponentEditor(type, value);
                    if (!Equals(newValue, value))
                    {
                        _bridge.SetComponent(_selectedEntity.Id, type, newValue);
                    }
                    
                    EditorGUI.indentLevel--;
                    EditorGUILayout.Space();
                }
            }
            EditorGUILayout.EndScrollView();
            
            // Entity actions
            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            {
                if (GUILayout.Button("Clone"))
                {
                    var components = _selectedEntity.Components
                        .Select(kvp => (kvp.Key, kvp.Value))
                        .ToArray();
                    _bridge.CreateEntity(components);
                    RefreshEntities();
                }
                
                GUI.color = Color.red;
                if (GUILayout.Button("Destroy"))
                {
                    _bridge.DestroyEntity(_selectedEntity.Id);
                    _selectedEntity = null;
                    RefreshEntities();
                }
                GUI.color = Color.white;
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private object DrawComponentEditor(Type type, object value)
        {
            // Use reflection to draw fields
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            var boxed = value;  // Need to box for struct modification
            
            foreach (var field in fields)
            {
                var fieldValue = field.GetValue(boxed);
                var newFieldValue = DrawFieldEditor(field.Name, field.FieldType, fieldValue);
                
                if (!Equals(fieldValue, newFieldValue))
                {
                    field.SetValue(boxed, newFieldValue);
                }
            }
            
            return boxed;
        }
        
        private object DrawFieldEditor(string name, Type type, object value)
        {
            if (type == typeof(int))
                return EditorGUILayout.IntField(name, (int)value);
            if (type == typeof(float))
                return EditorGUILayout.FloatField(name, (float)value);
            if (type == typeof(bool))
                return EditorGUILayout.Toggle(name, (bool)value);
            if (type == typeof(string))
                return EditorGUILayout.TextField(name, (string)value ?? "");
            if (type == typeof(Vector2))
                return EditorGUILayout.Vector2Field(name, (Vector2)value);
            if (type == typeof(Vector3))
                return EditorGUILayout.Vector3Field(name, (Vector3)value);
            if (type == typeof(Quaternion))
            {
                var euler = ((Quaternion)value).eulerAngles;
                euler = EditorGUILayout.Vector3Field(name, euler);
                return Quaternion.Euler(euler);
            }
            if (type == typeof(Color))
                return EditorGUILayout.ColorField(name, (Color)value);
            if (type.IsEnum)
                return EditorGUILayout.EnumPopup(name, (Enum)value);
            
            // Fallback: show as string
            EditorGUILayout.LabelField(name, value?.ToString() ?? "null");
            return value;
        }
        
        private void RefreshEntities()
        {
            _entities = _bridge.QueryEntities(_filter);
            
            // Update selected entity if it still exists
            if (_selectedEntity != null)
            {
                _selectedEntity = _entities.FirstOrDefault(e => e.Id == _selectedEntity.Id);
            }
        }
        
        private string FormatComponentValue(object value)
        {
            if (value is Vector3 v3) return $"({v3.x:F1}, {v3.y:F1}, {v3.z:F1})";
            if (value is float f) return f.ToString("F2");
            return value?.ToString() ?? "null";
        }
        
        private Type[] GetAllComponentTypes()
        {
            // Find all types with [CEComponent] attribute
            return TypeCache.GetTypesWithAttribute<CEComponentAttribute>()
                .ToArray();
        }
    }
}
```

---

## Summary

| Tool | Lines of Code (Est.) | Complexity | Dependencies |
|------|---------------------|------------|--------------|
| Network Simulator | ~2,500 | High | UdonSharp compiler hooks |
| Bandwidth Analyzer | ~800 | Medium | Reflection, static analysis |
| Schema Manager | ~1,000 | Medium | Reflection, code gen |
| World Validator | ~1,200 | Medium | Roslyn/reflection for analysis |
| Async Visualizer | ~700 | Medium | Compiler debug output |
| ECS Browser | ~600 | Low | CE.Perf runtime access |

**Recommended Build Order:**

1. **Bandwidth Analyzer** — Quick win, high value, no runtime dependencies
2. **World Validator** — Builds on analyzer infrastructure
3. **Schema Manager** — Enables safe persistence iteration
4. **ECS Browser** — Required when CE.Perf ships
5. **Async Visualizer** — Required when CE.Async ships
6. **Network Simulator** — Highest value but most complex; save for last
