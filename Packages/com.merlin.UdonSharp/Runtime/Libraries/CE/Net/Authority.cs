using JetBrains.Annotations;

namespace UdonSharp.CE.Net
{
    /// <summary>
    /// Declares who is allowed to write a synced field, and therefore what a write on a non-owner means.
    /// </summary>
    /// <remarks>
    /// VRChat treats every synced variable identically on the wire: only the owner's copy is ever
    /// serialized, and every other client's copy is replaced by whatever the owner last sent. What the
    /// network layer cannot know is whether a non-owner write is a mistake or the entire point, and the
    /// two failure modes are opposites. Writing owner-authoritative state on a non-owner is a silent
    /// desync -- the value looks right on the one client that caused it and nowhere else. Refusing to
    /// write predicted state on a non-owner is an unresponsive world -- every client stalls waiting for a
    /// packet it could have computed itself.
    ///
    /// Declaring the role at the field is what lets the analyzers stop guessing: an <see cref="Owner"/>
    /// field gets the strict CE0014 ownership check, and a <see cref="Predicted"/> field is exempted
    /// from it. That exemption is not a swap for an equivalent check on the other side. CE0063 reports
    /// only the degenerate case -- a predicted field on a behaviour whose declared sync mode replicates
    /// no variables at all -- and it is a backstop a normal compile never reaches, because CE0033
    /// rejects that combination during codegen. Whether the owner really publishes a value to reconcile
    /// against is taken on trust.
    ///
    /// <see cref="Owner"/> is 0 so that a defaulted or zero-initialized value is the strict reading. A
    /// field that never gets an explicit authority is checked as if it were authoritative, which is the
    /// direction that reports a bug instead of hiding one.
    /// </remarks>
    [PublicAPI]
    public enum Authority
    {
        /// <summary>
        /// Only the object's owner may write this field. A write on any other client is discarded.
        /// </summary>
        /// <remarks>
        /// "Discarded" is worth being precise about, because nothing complains at the time: VRChat neither
        /// rejects nor errors on a non-owner write. The local copy really does change, it is simply never
        /// serialized, and the owner's next deserialization overwrites it. The symptom is therefore a
        /// score, a phase, or a held-item id that is correct on exactly one client and briefly so.
        ///
        /// CE0014 enforces this by requiring every write to sit on a path guarded by an ownership check,
        /// or on a path that takes ownership first.
        ///
        /// Pick Owner for state that one client decides and the rest are told about: scores, game phase,
        /// who is holding what, whether a door is unlocked. If in doubt, pick Owner -- it is the reading
        /// that reports the desync rather than the one that permits it.
        /// </remarks>
        Owner = 0,

        /// <summary>
        /// Every client may write this field locally, and the owner's value is authoritative: the next
        /// deserialization replaces the local copy with the owner's.
        /// </summary>
        /// <remarks>
        /// This is predict-and-reconcile. The non-owner write is still never transmitted -- that part is
        /// no different from <see cref="Owner"/> -- but here that is the design rather than the bug.
        ///
        /// What Predicted records is <em>who may write the field</em>, not where the local value came
        /// from. Every client writes it, the owner's copy is the one that wins, and the owner's periodic
        /// packet is there to bound how far the copies drift apart. Local simulation is the usual reason
        /// a client has a value of its own to write, but it is one example of the rule rather than the
        /// rule itself. This package's own NetVehicle._ownerPlayerId is Predicted and is never simulated:
        /// every client latches the driver id out of an AssignDriver broadcast so input routing starts on
        /// the frame the assignment is heard instead of stalling for the owner's next serialization, and
        /// the owner republishes it afterwards. The write happens on every client, so the field is
        /// Predicted however the value was obtained.
        ///
        /// CE0014 does not apply to a Predicted field: the unguarded write it would report is the
        /// prediction. Nothing takes CE0014's place. Declaring Predicted is a claim that the owner
        /// reconciles the field, and on a Manual or Continuous behaviour that claim is taken on trust:
        /// CE0063 reports only the shape it can prove, a predicted field on a behaviour whose declared
        /// sync mode replicates no variables at all (BehaviourSyncMode.None or NoVariableSync), and even
        /// that is normally pre-empted by CE0033 during codegen. It cannot check that the owner publishes
        /// on a cadence, because RequestSerialization() may be reached from an ungated Update (which runs
        /// on the owner too), a base-class method, another behaviour entirely, or an inspector-wired
        /// SendCustomEvent -- none of them provable from source. So if the owner never sends, nothing
        /// warns: the clients diverge quietly, and keep diverging for as long as the world stays open.
        ///
        /// Pick Predicted for state every client writes for itself and that stalls visibly if it waits
        /// for the network: a boost meter draining, a jump or dodge cooldown counting down, a projectile
        /// advancing between packets, or a broadcast value each client applies the moment it hears it.
        ///
        /// Do not pick Predicted to quiet CE0014 on state only the owner can know. Authority is declared
        /// per field, not per write, so marking a field Predicted also switches the check off for the
        /// genuinely authoritative writes to that same field.
        /// </remarks>
        Predicted = 1
    }
}
