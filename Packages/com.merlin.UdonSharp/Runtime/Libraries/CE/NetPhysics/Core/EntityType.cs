using JetBrains.Annotations;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// High-level entity categories used for prediction, prioritization, and sync.
    /// </summary>
    [PublicAPI]
    public enum EntityType
    {
        Unknown = 0,
        Ball = 1,
        LocalVehicle = 2,
        OtherVehicle = 3,
        Projectile = 4,
        StaticHazard = 5,
    }
}

