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

        /// <summary>
        /// The seed the current random sequence was started from. Passing it back to
        /// <see cref="SetSeed"/> replays the same drops, latencies and duplicates.
        /// </summary>
        public int Seed { get; private set; }

        // Internal state
        private readonly List<DelayedPacket> _delayedPackets = new List<DelayedPacket>();
        private readonly List<PacketEvent> _recentEvents = new List<PacketEvent>();
        private const int MAX_RECENT_EVENTS = 100;

        // The simulator's own generator. UnityEngine.Random is global state the running world reads
        // too, so drawing from it here shifted the game's random sequence with every packet.
        private System.Random _random;

        // The latest delivery time handed out so far, in milliseconds on the simulator clock.
        private double _lastDeliveryTimeMs;

        // Send order of queued packets. Packets held back behind an earlier one share its delivery
        // time, and List.Sort is not stable, so the time alone would not keep them in order.
        private long _nextSequence;

        // Whether conditions are stored in EditorPrefs and delayed packets are pumped from
        // EditorApplication.update. False only for instances built by CreateDetached.
        private readonly bool _attached;

        /// <summary>
        /// Milliseconds on the clock packets are scheduled against. A double end to end: the old
        /// (float)timeSinceStartup * 1000f lost millisecond resolution once the editor had been open a
        /// few hours, which is the same order as the jitter being simulated.
        /// </summary>
        internal Func<double> ClockMs = () => EditorApplication.timeSinceStartup * 1000.0;

        #endregion

        #region Initialization

        private NetworkSimulator()
        {
            _attached = true;

            Conditions = NetworkProfiles.None.Clone();
            Stats = new NetworkSimulationStats();
            Stats.Reset();
            SetSeed(Environment.TickCount);

            // Load saved conditions
            LoadConditions();

            // Register for editor updates
            EditorApplication.update += Update;
        }

        private NetworkSimulator(NetworkConditions conditions, int seed)
        {
            _attached = false;

            Conditions = conditions?.Clone() ?? NetworkProfiles.None.Clone();
            Stats = new NetworkSimulationStats();
            Stats.Reset();
            SetSeed(seed);
        }

        /// <summary>
        /// Builds a simulator that is not the singleton: it reads and writes no EditorPrefs and is not
        /// pumped by the editor, so its caller drives the clock and <see cref="ProcessDelayedPackets"/>.
        /// </summary>
        internal static NetworkSimulator CreateDetached(NetworkConditions conditions, int seed)
        {
            return new NetworkSimulator(conditions, seed);
        }

        ~NetworkSimulator()
        {
            // A detached instance never subscribed, and unlike the singleton it does get collected. Its
            // finalizer must leave the delegate alone: this runs on the finalizer thread, and an
            // unsynchronized -= there can drop a subscription the main thread is adding at that moment.
            if (!_attached)
                return;

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
        /// Restarts the random sequence from <paramref name="seed"/>.
        /// </summary>
        public void SetSeed(int seed)
        {
            Seed = seed;
            _random = new System.Random(seed);
        }

        /// <summary>
        /// Resets simulation statistics.
        /// </summary>
        public void ResetStats()
        {
            Stats.Reset();
            _delayedPackets.Clear();
            _recentEvents.Clear();
            _lastDeliveryTimeMs = 0;
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

            // Anything already due goes out first. Delayed packets are otherwise only released by the next
            // editor update, so a packet that needs no delay could be logged ahead of one that was due
            // before it was even sent.
            ProcessDelayedPackets();

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
            if (Conditions.ShouldDropPacket(_random))
            {
                Stats.DroppedPackets++;
                result.Dropped = true;
                result.Delivered = false;

                packetEvent.EventType = PacketEventType.Dropped;
                RecordEvent(packetEvent);

                return result;
            }

            // Calculate latency
            float latency = Conditions.GetRandomLatency(_random);

            // Check for duplication
            if (Conditions.ShouldDuplicatePacket(_random))
            {
                Stats.DuplicatedPackets++;
                result.Duplicated = true;
            }

            result.Delivered = true;

            packetEvent.EventType = result.Duplicated ? PacketEventType.Duplicated : PacketEventType.Delivered;

            double currentTime = ClockMs();
            double deliveryTimeMs = currentTime + latency;

            if (Conditions.SimulateOutOfOrder && _delayedPackets.Count > 0 && _random.NextDouble() * 100.0 < 25.0)
            {
                double maxAdvanceMs = Math.Max(1.0, Math.Min(latency, Conditions.Jitter + latency * 0.5));
                deliveryTimeMs = Math.Max(currentTime, deliveryTimeMs - (1.0 + _random.NextDouble() * maxAdvanceMs));
            }

            // Every packet draws its own latency, so a later packet can come out with an earlier delivery
            // time than one sent before it.
            if (deliveryTimeMs < _lastDeliveryTimeMs)
            {
                if (Conditions.SimulateOutOfOrder)
                {
                    // Counted here, where the packet is known to overtake something. Labelling a fixed
                    // quarter of the packets up front reported reordering that had not happened and
                    // missed the reordering that had.
                    packetEvent.EventType = PacketEventType.OutOfOrder;
                    Stats.OutOfOrderPackets++;
                }
                else
                {
                    // Out-of-order delivery is switched off, so this packet waits its turn behind the
                    // ones already in flight. Leaving the draw alone reordered packets on every profile
                    // with a latency range while the out-of-order count stayed at zero.
                    deliveryTimeMs = _lastDeliveryTimeMs;
                }
            }

            if (deliveryTimeMs > _lastDeliveryTimeMs)
            {
                _lastDeliveryTimeMs = deliveryTimeMs;
            }

            // What the caller is told to wait is the delay actually applied, including any time spent
            // queued behind an earlier packet -- a caller that schedules by it keeps the same order.
            float appliedDelayMs = (float)(deliveryTimeMs - currentTime);
            result.DelayMs = appliedDelayMs;
            Stats.TotalLatencyMs += appliedDelayMs;
            packetEvent.LatencyMs = appliedDelayMs;

            if (appliedDelayMs > 0f)
            {
                Stats.DelayedPackets++;

                _delayedPackets.Add(new DelayedPacket
                {
                    Name = packetName,
                    SizeBytes = sizeBytes,
                    DeliveryTimeMs = deliveryTimeMs,
                    Sequence = _nextSequence++,
                    LatencyMs = appliedDelayMs,
                    EventType = packetEvent.EventType
                });
            }
            else
            {
                RecordEvent(packetEvent);
            }

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

        internal void ProcessDelayedPackets()
        {
            if (_delayedPackets.Count == 0)
                return;

            double currentTime = ClockMs();
            _delayedPackets.Sort((a, b) =>
            {
                int byTime = a.DeliveryTimeMs.CompareTo(b.DeliveryTimeMs);
                return byTime != 0 ? byTime : a.Sequence.CompareTo(b.Sequence);
            });

            for (int i = 0; i < _delayedPackets.Count;)
            {
                var packet = _delayedPackets[i];
                if (packet.DeliveryTimeMs > currentTime)
                    break;

                _delayedPackets.RemoveAt(i);
                var evt = new PacketEvent
                {
                    Timestamp = DateTime.Now,
                    PacketName = packet.Name,
                    SizeBytes = packet.SizeBytes,
                    EventType = packet.EventType,
                    LatencyMs = packet.LatencyMs
                };
                RecordEvent(evt);
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
            if (!_attached)
                return;

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
        public double DeliveryTimeMs;
        public long Sequence;
        public float LatencyMs;
        public PacketEventType EventType;
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

