using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UdonSharp.CE.Editor.DevTools.NetworkSim
{
    /// <summary>
    /// Core network simulation engine for testing UdonSharp networking under various conditions.
    /// Simulates latency, packet loss, jitter, and other network characteristics.
    /// </summary>
    public class NetworkSimulator
    {
        #region Singleton

        private static NetworkSimulator _instance;

        /// <summary>
        /// Gets the singleton instance of the network simulator.
        /// </summary>
        public static NetworkSimulator Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new NetworkSimulator();
                }
                return _instance;
            }
        }

        #endregion

        #region State

        /// <summary>
        /// Current network conditions being simulated.
        /// </summary>
        public NetworkConditions Conditions { get; private set; }

        /// <summary>
        /// Statistics for the current simulation session.
        /// </summary>
        public NetworkSimulationStats Stats { get; private set; }

        /// <summary>
        /// Whether the simulator is currently active.
        /// </summary>
        public bool IsActive => Conditions?.Enabled ?? false;

        /// <summary>
        /// Event fired when simulation conditions change.
        /// </summary>
        public event Action<NetworkConditions> OnConditionsChanged;

        /// <summary>
        /// Event fired when a packet is processed.
        /// </summary>
        public event Action<PacketEvent> OnPacketProcessed;

        // Internal state
        private readonly Queue<DelayedPacket> _delayedPackets = new Queue<DelayedPacket>();
        private readonly List<PacketEvent> _recentEvents = new List<PacketEvent>();
        private const int MAX_RECENT_EVENTS = 100;

        #endregion

        #region Initialization

        private NetworkSimulator()
        {
            Conditions = NetworkProfiles.None.Clone();
            Stats = new NetworkSimulationStats();
            Stats.Reset();

            // Load saved conditions
            LoadConditions();

            // Register for editor updates
            EditorApplication.update += Update;
        }

        ~NetworkSimulator()
        {
            EditorApplication.update -= Update;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Sets the network conditions to simulate.
        /// </summary>
        public void SetConditions(NetworkConditions conditions)
        {
            Conditions = conditions?.Clone() ?? NetworkProfiles.None.Clone();
            SaveConditions();
            OnConditionsChanged?.Invoke(Conditions);
            Debug.Log($"[NetworkSimulator] Conditions set to: {Conditions.ProfileName}");
        }

        /// <summary>
        /// Applies a predefined profile.
        /// </summary>
        public void ApplyProfile(int profileIndex)
        {
            var profiles = NetworkProfiles.GetAllProfiles();
            if (profileIndex >= 0 && profileIndex < profiles.Length)
            {
                SetConditions(profiles[profileIndex]);
            }
        }

        /// <summary>
        /// Enables or disables the simulation.
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            Conditions.Enabled = enabled;
            SaveConditions();
            OnConditionsChanged?.Invoke(Conditions);

            if (enabled)
            {
                Stats.Reset();
                Debug.Log("[NetworkSimulator] Simulation enabled");
            }
            else
            {
                Debug.Log("[NetworkSimulator] Simulation disabled");
            }
        }

        /// <summary>
        /// Resets simulation statistics.
        /// </summary>
        public void ResetStats()
        {
            Stats.Reset();
            _recentEvents.Clear();
        }

        /// <summary>
        /// Simulates processing a network packet.
        /// Returns the result indicating whether the packet should be delivered.
        /// </summary>
        public PacketResult SimulatePacket(string packetName, int sizeBytes)
        {
            if (!IsActive)
            {
                return new PacketResult
                {
                    Delivered = true,
                    DelayMs = 0,
                    Dropped = false,
                    Duplicated = false
                };
            }

            Stats.TotalPackets++;
            Stats.TotalBytes += sizeBytes;

            var result = new PacketResult();
            var packetEvent = new PacketEvent
            {
                Timestamp = DateTime.Now,
                PacketName = packetName,
                SizeBytes = sizeBytes
            };

            // Check for packet loss
            if (Conditions.ShouldDropPacket())
            {
                Stats.DroppedPackets++;
                result.Dropped = true;
                result.Delivered = false;

                packetEvent.EventType = PacketEventType.Dropped;
                RecordEvent(packetEvent);

                return result;
            }

            // Calculate latency
            float latency = Conditions.GetRandomLatency();
            result.DelayMs = latency;
            Stats.TotalLatencyMs += latency;

            if (latency > 0)
            {
                Stats.DelayedPackets++;
            }

            // Check for duplication
            if (Conditions.ShouldDuplicatePacket())
            {
                Stats.DuplicatedPackets++;
                result.Duplicated = true;
            }

            result.Delivered = true;

            packetEvent.EventType = result.Duplicated ? PacketEventType.Duplicated : PacketEventType.Delivered;
            packetEvent.LatencyMs = latency;
            RecordEvent(packetEvent);

            return result;
        }

        /// <summary>
        /// Gets recent packet events for display.
        /// </summary>
        public List<PacketEvent> GetRecentEvents()
        {
            return new List<PacketEvent>(_recentEvents);
        }

        /// <summary>
        /// Estimates the effective bandwidth under current conditions.
        /// </summary>
        public float EstimateEffectiveBandwidth()
        {
            if (!IsActive || Conditions.BandwidthLimitKBps <= 0)
            {
                return float.MaxValue;
            }

            // Account for packet loss reducing effective bandwidth
            float lossMultiplier = 1f - (Conditions.PacketLossPercent / 100f);

            // Account for duplicates increasing bandwidth usage
            float dupMultiplier = 1f + (Conditions.DuplicatePercent / 100f);

            return Conditions.BandwidthLimitKBps * lossMultiplier / dupMultiplier;
        }

        #endregion

        #region Internal Methods

        private void Update()
        {
            // Process delayed packets (if we implement actual delay simulation)
            ProcessDelayedPackets();
        }

        private void ProcessDelayedPackets()
        {
            float currentTime = (float)EditorApplication.timeSinceStartup * 1000f;

            while (_delayedPackets.Count > 0)
            {
                var packet = _delayedPackets.Peek();
                if (packet.DeliveryTimeMs <= currentTime)
                {
                    _delayedPackets.Dequeue();
                    // Deliver packet
                    var evt = new PacketEvent
                    {
                        Timestamp = DateTime.Now,
                        PacketName = packet.Name,
                        SizeBytes = packet.SizeBytes,
                        EventType = PacketEventType.Delivered,
                        LatencyMs = packet.LatencyMs
                    };
                    RecordEvent(evt);
                }
                else
                {
                    break;
                }
            }
        }

        private void RecordEvent(PacketEvent evt)
        {
            _recentEvents.Add(evt);

            // Trim old events
            while (_recentEvents.Count > MAX_RECENT_EVENTS)
            {
                _recentEvents.RemoveAt(0);
            }

            OnPacketProcessed?.Invoke(evt);
        }

        private void SaveConditions()
        {
            EditorPrefs.SetString("CE_NetworkSim_Profile", Conditions.ProfileName);
            EditorPrefs.SetFloat("CE_NetworkSim_LatencyMin", Conditions.LatencyMin);
            EditorPrefs.SetFloat("CE_NetworkSim_LatencyMax", Conditions.LatencyMax);
            EditorPrefs.SetFloat("CE_NetworkSim_Jitter", Conditions.Jitter);
            EditorPrefs.SetFloat("CE_NetworkSim_PacketLoss", Conditions.PacketLossPercent);
            EditorPrefs.SetFloat("CE_NetworkSim_Bandwidth", Conditions.BandwidthLimitKBps);
            EditorPrefs.SetBool("CE_NetworkSim_OutOfOrder", Conditions.SimulateOutOfOrder);
            EditorPrefs.SetFloat("CE_NetworkSim_Duplicate", Conditions.DuplicatePercent);
            EditorPrefs.SetBool("CE_NetworkSim_Enabled", Conditions.Enabled);
        }

        private void LoadConditions()
        {
            if (!EditorPrefs.HasKey("CE_NetworkSim_Profile"))
                return;

            Conditions.ProfileName = EditorPrefs.GetString("CE_NetworkSim_Profile", "Custom");
            Conditions.LatencyMin = EditorPrefs.GetFloat("CE_NetworkSim_LatencyMin", 0f);
            Conditions.LatencyMax = EditorPrefs.GetFloat("CE_NetworkSim_LatencyMax", 0f);
            Conditions.Jitter = EditorPrefs.GetFloat("CE_NetworkSim_Jitter", 0f);
            Conditions.PacketLossPercent = EditorPrefs.GetFloat("CE_NetworkSim_PacketLoss", 0f);
            Conditions.BandwidthLimitKBps = EditorPrefs.GetFloat("CE_NetworkSim_Bandwidth", 0f);
            Conditions.SimulateOutOfOrder = EditorPrefs.GetBool("CE_NetworkSim_OutOfOrder", false);
            Conditions.DuplicatePercent = EditorPrefs.GetFloat("CE_NetworkSim_Duplicate", 0f);
            Conditions.Enabled = EditorPrefs.GetBool("CE_NetworkSim_Enabled", false);
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Result of simulating a packet through the network.
    /// </summary>
    public struct PacketResult
    {
        /// <summary>
        /// Whether the packet was delivered.
        /// </summary>
        public bool Delivered;

        /// <summary>
        /// Simulated delay in milliseconds.
        /// </summary>
        public float DelayMs;

        /// <summary>
        /// Whether the packet was dropped.
        /// </summary>
        public bool Dropped;

        /// <summary>
        /// Whether the packet was duplicated.
        /// </summary>
        public bool Duplicated;
    }

    /// <summary>
    /// A packet queued for delayed delivery.
    /// </summary>
    internal struct DelayedPacket
    {
        public string Name;
        public int SizeBytes;
        public float DeliveryTimeMs;
        public float LatencyMs;
    }

    /// <summary>
    /// Types of packet events.
    /// </summary>
    public enum PacketEventType
    {
        Delivered,
        Dropped,
        Duplicated,
        OutOfOrder
    }

    /// <summary>
    /// Record of a packet event for display.
    /// </summary>
    public class PacketEvent
    {
        public DateTime Timestamp;
        public string PacketName;
        public int SizeBytes;
        public PacketEventType EventType;
        public float LatencyMs;
    }

    #endregion
}

