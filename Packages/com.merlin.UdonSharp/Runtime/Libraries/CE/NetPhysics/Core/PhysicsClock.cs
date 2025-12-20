using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Fixed-timestep simulation clock with an accumulator.
    /// Use this to run a deterministic tick loop even with variable frame rates.
    /// </summary>
    [PublicAPI]
    public class PhysicsClock
    {
        private int _tickRate;
        private float _tickDelta;

        /// <summary>
        /// Current simulation time in seconds advanced by completed ticks.
        /// </summary>
        public float SimulationTime { get; private set; }

        /// <summary>
        /// Accumulated un-simulated time in seconds.
        /// </summary>
        public float Accumulator { get; private set; }

        /// <summary>
        /// Physics ticks per second.
        /// </summary>
        public int TickRate
        {
            get => _tickRate;
            set => SetTickRate(value);
        }

        /// <summary>
        /// Fixed tick delta time in seconds.
        /// </summary>
        public float TickDelta => _tickDelta;

        /// <summary>
        /// Interpolation alpha (0-1) for render smoothing between ticks.
        /// </summary>
        public float Alpha => _tickDelta > 0f ? Mathf.Clamp01(Accumulator / _tickDelta) : 0f;

        public PhysicsClock(int tickRate = 60)
        {
            SetTickRate(tickRate);
        }

        public void Reset()
        {
            SimulationTime = 0f;
            Accumulator = 0f;
        }

        public void SetTickRate(int tickRate)
        {
            _tickRate = Mathf.Max(1, tickRate);
            _tickDelta = 1f / _tickRate;
        }

        /// <summary>
        /// Adds time to the accumulator and returns how many ticks should be simulated.
        /// </summary>
        /// <param name="deltaTime">Time passed since last update.</param>
        /// <param name="maxTicks">Safety cap to prevent spiral-of-death.</param>
        public int ConsumeTicks(float deltaTime, int maxTicks = 8)
        {
            if (_tickDelta <= 0f)
                return 0;

            Accumulator += Mathf.Max(0f, deltaTime);

            int ticks = 0;
            while (Accumulator >= _tickDelta && ticks < maxTicks)
            {
                Accumulator -= _tickDelta;
                SimulationTime += _tickDelta;
                ticks++;
            }

            return ticks;
        }
    }
}

