using System;
using UnityEngine;

namespace UdonSharp.CE.Editor.DevTools.NetworkSim
{
    /// <summary>
    /// Defines network condition parameters for simulation.
    /// </summary>
    [Serializable]
    public class NetworkConditions
    {
        /// <summary>
        /// Profile name for display.
        /// </summary>
        public string ProfileName = "Custom";

        /// <summary>
        /// Minimum latency in milliseconds.
        /// </summary>
        [Range(0, 5000)]
        public float LatencyMin = 0f;

        /// <summary>
        /// Maximum latency in milliseconds.
        /// </summary>
        [Range(0, 5000)]
        public float LatencyMax = 0f;

        /// <summary>
        /// Jitter variance in milliseconds (random variation added to latency).
        /// </summary>
        [Range(0, 500)]
        public float Jitter = 0f;

        /// <summary>
        /// Packet loss percentage (0-100).
        /// </summary>
        [Range(0, 100)]
        public float PacketLossPercent = 0f;

        /// <summary>
        /// Bandwidth limit in KB/s. 0 means unlimited.
        /// </summary>
        [Range(0, 10000)]
        public float BandwidthLimitKBps = 0f;

        /// <summary>
        /// Whether to simulate out-of-order packet delivery.
        /// </summary>
        public bool SimulateOutOfOrder = false;

        /// <summary>
        /// Probability of duplicate packets (0-100).
        /// </summary>
        [Range(0, 50)]
        public float DuplicatePercent = 0f;

        /// <summary>
        /// Whether simulation is enabled.
        /// </summary>
        public bool Enabled = false;

        /// <summary>
        /// Creates a deep copy of the conditions.
        /// </summary>
        public NetworkConditions Clone()
        {
            return new NetworkConditions
            {
                ProfileName = ProfileName,
                LatencyMin = LatencyMin,
                LatencyMax = LatencyMax,
                Jitter = Jitter,
                PacketLossPercent = PacketLossPercent,
                BandwidthLimitKBps = BandwidthLimitKBps,
                SimulateOutOfOrder = SimulateOutOfOrder,
                DuplicatePercent = DuplicatePercent,
                Enabled = Enabled
            };
        }

        /// <summary>
        /// Gets the average latency.
        /// </summary>
        public float AverageLatency => (LatencyMin + LatencyMax) / 2f;

        /// <summary>
        /// Gets a random latency value within the configured range.
        /// </summary>
        public float GetRandomLatency()
        {
            float baseLatency = UnityEngine.Random.Range(LatencyMin, LatencyMax);
            float jitterValue = UnityEngine.Random.Range(-Jitter, Jitter);
            return Mathf.Max(0f, baseLatency + jitterValue);
        }

        /// <summary>
        /// Determines if a packet should be dropped based on loss percentage.
        /// </summary>
        public bool ShouldDropPacket()
        {
            return UnityEngine.Random.Range(0f, 100f) < PacketLossPercent;
        }

        /// <summary>
        /// Determines if a packet should be duplicated.
        /// </summary>
        public bool ShouldDuplicatePacket()
        {
            return UnityEngine.Random.Range(0f, 100f) < DuplicatePercent;
        }
    }

    /// <summary>
    /// Predefined network condition profiles.
    /// </summary>
    public static class NetworkProfiles
    {
        /// <summary>
        /// No network degradation - ideal conditions.
        /// </summary>
        public static NetworkConditions None => new NetworkConditions
        {
            ProfileName = "None (Ideal)",
            LatencyMin = 0f,
            LatencyMax = 0f,
            Jitter = 0f,
            PacketLossPercent = 0f,
            BandwidthLimitKBps = 0f,
            SimulateOutOfOrder = false,
            DuplicatePercent = 0f,
            Enabled = false
        };

        /// <summary>
        /// Good WiFi connection.
        /// </summary>
        public static NetworkConditions WiFi => new NetworkConditions
        {
            ProfileName = "WiFi (Good)",
            LatencyMin = 10f,
            LatencyMax = 50f,
            Jitter = 5f,
            PacketLossPercent = 0.5f,
            BandwidthLimitKBps = 0f,
            SimulateOutOfOrder = false,
            DuplicatePercent = 0f,
            Enabled = true
        };

        /// <summary>
        /// Poor WiFi connection with interference.
        /// </summary>
        public static NetworkConditions WiFiPoor => new NetworkConditions
        {
            ProfileName = "WiFi (Poor)",
            LatencyMin = 50f,
            LatencyMax = 200f,
            Jitter = 30f,
            PacketLossPercent = 5f,
            BandwidthLimitKBps = 500f,
            SimulateOutOfOrder = true,
            DuplicatePercent = 0.5f,
            Enabled = true
        };

        /// <summary>
        /// 4G mobile connection.
        /// </summary>
        public static NetworkConditions Mobile4G => new NetworkConditions
        {
            ProfileName = "Mobile 4G",
            LatencyMin = 30f,
            LatencyMax = 100f,
            Jitter = 15f,
            PacketLossPercent = 1f,
            BandwidthLimitKBps = 2000f,
            SimulateOutOfOrder = false,
            DuplicatePercent = 0f,
            Enabled = true
        };

        /// <summary>
        /// 3G mobile connection.
        /// </summary>
        public static NetworkConditions Mobile3G => new NetworkConditions
        {
            ProfileName = "Mobile 3G",
            LatencyMin = 100f,
            LatencyMax = 400f,
            Jitter = 50f,
            PacketLossPercent = 3f,
            BandwidthLimitKBps = 500f,
            SimulateOutOfOrder = true,
            DuplicatePercent = 0.5f,
            Enabled = true
        };

        /// <summary>
        /// Satellite connection (high latency).
        /// </summary>
        public static NetworkConditions Satellite => new NetworkConditions
        {
            ProfileName = "Satellite",
            LatencyMin = 500f,
            LatencyMax = 800f,
            Jitter = 50f,
            PacketLossPercent = 2f,
            BandwidthLimitKBps = 1000f,
            SimulateOutOfOrder = true,
            DuplicatePercent = 0f,
            Enabled = true
        };

        /// <summary>
        /// Extremely poor connection for stress testing.
        /// </summary>
        public static NetworkConditions Terrible => new NetworkConditions
        {
            ProfileName = "Terrible (Stress Test)",
            LatencyMin = 200f,
            LatencyMax = 2000f,
            Jitter = 200f,
            PacketLossPercent = 15f,
            BandwidthLimitKBps = 100f,
            SimulateOutOfOrder = true,
            DuplicatePercent = 5f,
            Enabled = true
        };

        /// <summary>
        /// Gets all predefined profiles.
        /// </summary>
        public static NetworkConditions[] GetAllProfiles()
        {
            return new[]
            {
                None,
                WiFi,
                WiFiPoor,
                Mobile4G,
                Mobile3G,
                Satellite,
                Terrible
            };
        }

        /// <summary>
        /// Gets profile names for dropdown display.
        /// </summary>
        public static string[] GetProfileNames()
        {
            return new[]
            {
                "None (Ideal)",
                "WiFi (Good)",
                "WiFi (Poor)",
                "Mobile 4G",
                "Mobile 3G",
                "Satellite",
                "Terrible (Stress Test)",
                "Custom"
            };
        }
    }

    /// <summary>
    /// Statistics collected during network simulation.
    /// </summary>
    [Serializable]
    public class NetworkSimulationStats
    {
        /// <summary>
        /// Total packets processed.
        /// </summary>
        public long TotalPackets;

        /// <summary>
        /// Packets dropped due to simulated loss.
        /// </summary>
        public long DroppedPackets;

        /// <summary>
        /// Packets delayed due to latency simulation.
        /// </summary>
        public long DelayedPackets;

        /// <summary>
        /// Packets duplicated.
        /// </summary>
        public long DuplicatedPackets;

        /// <summary>
        /// Packets delivered out of order.
        /// </summary>
        public long OutOfOrderPackets;

        /// <summary>
        /// Total bytes transmitted.
        /// </summary>
        public long TotalBytes;

        /// <summary>
        /// Total simulated latency accumulated (ms).
        /// </summary>
        public double TotalLatencyMs;

        /// <summary>
        /// Simulation start time.
        /// </summary>
        public DateTime StartTime;

        /// <summary>
        /// Average latency per packet.
        /// </summary>
        public double AverageLatencyMs => TotalPackets > 0 ? TotalLatencyMs / TotalPackets : 0;

        /// <summary>
        /// Actual packet loss rate.
        /// </summary>
        public float ActualLossRate => TotalPackets > 0 ? (float)DroppedPackets / TotalPackets * 100f : 0f;

        /// <summary>
        /// Duration of the simulation.
        /// </summary>
        public TimeSpan Duration => DateTime.Now - StartTime;

        /// <summary>
        /// Resets all statistics.
        /// </summary>
        public void Reset()
        {
            TotalPackets = 0;
            DroppedPackets = 0;
            DelayedPackets = 0;
            DuplicatedPackets = 0;
            OutOfOrderPackets = 0;
            TotalBytes = 0;
            TotalLatencyMs = 0;
            StartTime = DateTime.Now;
        }
    }
}

