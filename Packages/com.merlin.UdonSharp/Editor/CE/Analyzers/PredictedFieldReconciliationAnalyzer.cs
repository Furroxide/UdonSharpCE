using System.Collections.Generic;
using UdonSharp;
using UdonSharp.CE.Net;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Detects <c>[Sync(Authority.Predicted)]</c> fields on a behaviour whose declared sync mode replicates
    /// no variables at all, so the prediction has nothing it could ever converge on.
    ///
    /// Declaring a field predicted switches CE0014 off for it, and nothing takes CE0014's place. Predicted
    /// records *who may write the field* -- every client, with the owner's copy authoritative -- and says
    /// nothing about where the local value came from: local simulation is the usual reason a client has a
    /// value of its own to write, but this package's own NetVehicle._ownerPlayerId is predicted and is never
    /// simulated, every client latches it out of an AssignDriver broadcast. What remains is a claim, taken
    /// on trust, that the owner's copy is published periodically and pulls the local ones back together.
    /// When that claim is false the field does not fail loudly: every client keeps writing happily, each
    /// from its own slightly different starting point, and the divergence grows for as long as the world
    /// stays open. The author sees it working perfectly on their own client, because on their own client
    /// there is only one copy.
    ///
    /// This check verifies far less of that claim than a reader might hope, and the reason is worth
    /// stating rather than discovering. CE0063 exists because CE0014 was reporting five pieces of correct
    /// code on this package's own NetVehicle. A replacement check that invents its own false positives has
    /// not fixed anything, it has moved the noise, so the rule here is that a report requires positive
    /// evidence that the value cannot converge. Failing to *find* a reconciliation path is not that
    /// evidence, and no amount of it adds up to that evidence.
    ///
    /// Applied honestly, that rule leaves exactly one shape: a sync mode that replicates nothing. Every
    /// other shape either converges or cannot be shown not to.
    ///
    /// <list type="bullet">
    /// <item><description>Continuous replicates from the owner on VRChat's own schedule, with no call for
    /// an author to forget.</description></item>
    /// <item><description>Manual with any <c>RequestSerialization()</c> the owner can reach converges --
    /// and "the owner can reach it" is much weaker than "an ownership guard sits on it". An earlier
    /// version of this check read a missing guard as a missing send and reported
    /// '<c>void Update() { _boost -= Time.deltaTime; RequestSerialization(); }</c>'. Udon runs Update on
    /// every client, the owner included, so that behaviour serializes from the owner every single frame.
    /// It is a bandwidth bug -- CE0017's, and CE0017 does report it -- not a divergence. Deciding it
    /// diverged took a real reconciliation and called it a defect.</description></item>
    /// <item><description>Manual whose only send sits in a base class, a property accessor, a local
    /// function, or a public method some other behaviour drives (the pool manager doing
    /// '<c>if (Networking.IsOwner(p.gameObject)) p.Publish();</c>', which is how NetPhysicsWorld drives
    /// its entities) still converges. All this analyzer could observe there is that its own walk stopped
    /// early.</description></item>
    /// <item><description>Manual with no <c>RequestSerialization()</c> anywhere in its source is the one
    /// case that looks provable and is not: <c>other.RequestSerialization()</c> from a manager behaviour
    /// serializes this one, and so does a <c>SendCustomEvent</c> wired up in the inspector. CE0015 already
    /// reports the common form of it, at the write, and names the cross-behaviour call in its fix
    /// text.</description></item>
    /// </list>
    ///
    /// So what CE0063 guarantees is small and total: if it fires, the behaviour's declared mode transmits
    /// no variable to anyone, ever, and the prediction is unreconcilable no matter what any other
    /// behaviour does. What it does not do is verify that a Manual behaviour publishes on a cadence. That
    /// part of the claim is left to review, because every automatic test for it that fits in this
    /// analyzer answers "I could not find one" and says "there is not one".
    ///
    /// An earlier version of this check did have a Manual arm. It was deleted once the four shapes above
    /// were found, and the list is kept as the record of why rather than as a todo: reinstating that arm
    /// reinstates those false positives, which is the exact failure CE0063 was written to replace. CE0014's
    /// fix text is worded to match -- it tells an author that declaring Predicted switches a check off, not
    /// that it hands the field to this one.
    ///
    /// In a normal compile this reports nothing, because BehaviourSyncModeEmitter raises CE0033 for
    /// None/NoVariableSync plus synced state during Phase A codegen and that compile never reaches Bind.
    /// The check is kept as a backstop so that narrowing CE0033 -- or the compiler's own sync-mode
    /// compatibility gate -- cannot silently turn "cannot converge" into "nobody looks".
    /// </summary>
    /// <example>
    /// // Warning - nothing on this behaviour is ever transmitted, so the guesses never meet
    /// [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    /// public class Blaster : UdonSharpBehaviour
    /// {
    ///     [Sync(Authority.Predicted)] private float _charge;   // CE0063
    /// }
    ///
    /// // Good - the mode replicates, and the owner publishes on a cadence
    /// [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    /// public class Blaster : UdonSharpBehaviour
    /// {
    ///     [Sync(Authority.Predicted)] private float _charge;
    ///
    ///     private void FixedUpdate()
    ///     {
    ///         if (!Networking.IsOwner(gameObject))
    ///             return;
    ///
    ///         RequestSerialization();
    ///     }
    /// }
    /// </example>
    internal class PredictedFieldReconciliationAnalyzer : ICompileTimeAnalyzer
    {
        public string AnalyzerId => "CE0063";
        // The name is the scope label users see: CEAnalyzerRunner.GetAnalyzerInfo() reports it, and CE0019
        // prints it when this analyzer throws. It has to describe the check that exists, not the one the
        // deleted Manual arm made -- "Never Reconciled" promised a reconciliation proof this cannot perform
        // and the Description right below it correctly disclaims, which is the worst of both.
        public string AnalyzerName => "Predicted Field On Non-Replicating Behaviour";
        public string Description => "Detects [Sync(Authority.Predicted)] fields on behaviours whose sync mode replicates no variables.";
        public bool IsEnabledByDefault => true;

        public IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            if (type == null || compilationContext == null)
                return diagnostics;

            BehaviourSyncMode? syncMode = CENetworkAnalysis.GetDeclaredSyncMode(type);

            // The mode is asked first because it is the only question with a reportable answer, and
            // because everything after it costs attribute decoding on every field of every behaviour in
            // the project. Any, Manual, Continuous and the null case where neither this behaviour nor any
            // base declares a mode all leave here: Any and null hand the real mode to an inspector choice
            // this cannot see, and Manual and Continuous both transmit variables, so a value published on
            // either can reach the clients predicting it. See the type comment for why the Manual half of
            // that -- whether anything actually publishes -- is not checked further.
            if (syncMode != BehaviourSyncMode.None && syncMode != BehaviourSyncMode.NoVariableSync)
                return diagnostics;

            foreach (FieldSymbol field in GetPredictedFields(type))
                diagnostics.Add(ReportNoReplication(field, type, syncMode.Value));

            return diagnostics;
        }

        /// <summary>
        /// This behaviour's own synced fields that declare <see cref="Authority.Predicted"/>.
        ///
        /// The declaration state is read rather than the authority value alone, so nothing but an
        /// explicit <c>[Sync(Authority.Predicted)]</c> can land here. A bare <c>[UdonSynced]</c> field
        /// carries no CE attribute to read and comes back <see cref="CENetworkAnalysis.SyncAuthorityDeclaration.Absent"/>;
        /// a CE <c>[Sync]</c> whose argument cannot be decoded comes back
        /// <see cref="CENetworkAnalysis.SyncAuthorityDeclaration.Unreadable"/>. Neither is Predicted, so
        /// neither can reach a report -- upstream sync stays outside this check by construction rather
        /// than by a filter someone could later relax.
        /// </summary>
        private static List<FieldSymbol> GetPredictedFields(TypeSymbol type)
        {
            var predicted = new List<FieldSymbol>();

            foreach (KeyValuePair<string, FieldSymbol> syncedField in CENetworkAnalysis.GetSyncedFields(type))
            {
                CENetworkAnalysis.SyncAuthorityDeclaration declaration =
                    CENetworkAnalysis.GetAuthorityDeclaration(syncedField.Value, out Authority authority);

                if (declaration == CENetworkAnalysis.SyncAuthorityDeclaration.Declared &&
                    authority == Authority.Predicted)
                {
                    predicted.Add(syncedField.Value);
                }
            }

            return predicted;
        }

        private AnalyzerDiagnostic ReportNoReplication(FieldSymbol field, TypeSymbol type, BehaviourSyncMode syncMode)
        {
            return AnalyzerDiagnostic.Warning(
                CENetworkAnalysis.GetFieldLocation(field),
                $"Field '{field.Name}' is declared [Sync(Authority.Predicted)], but '{type.Name}' uses " +
                $"BehaviourSyncMode.{syncMode} and never replicates variables. Every client predicts " +
                $"'{field.Name}' locally and no owner value can ever arrive to correct it, so the clients " +
                "diverge permanently with nothing reported at runtime.",
                AnalyzerId,
                AnalyzerId,
                $"Give '{type.Name}' a sync mode that replicates -- [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)] " +
                "with an owner-side RequestSerialization(), or BehaviourSyncMode.Continuous -- or, if this value is " +
                $"genuinely local-only, drop the sync attribute from '{field.Name}' so it stops claiming to be " +
                "reconciled.");
        }
    }
}
