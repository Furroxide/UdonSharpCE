using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UdonSharp;
using UdonSharp.CE.Editor.DevTools.Analysis;
using UdonSharp.CE.Editor.DevTools.Validation;
using UdonSharp.CE.Editor.DevTools.Validation.Validators;
using UdonSharp.CE.Net;
using UdonSharp.Tests.CE.Validation;

namespace UdonSharp.CE.Tests.DevTools
{
    public class SyncAttributeToolingTests
    {
        [UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
        private class SyncFieldFixture : UdonSharpBehaviour
        {
            [Sync(Authority.Owner)]
            public int value;
        }

        [Test]
        public void BehaviourAnalyzer_TreatsSyncAttributeAsSynced()
        {
            var analyzer = new BehaviourAnalyzer();

            BehaviourAnalysisResult result = analyzer.Analyze(typeof(SyncFieldFixture));

            Assert.That(result.Fields.Select(field => field.FieldName), Does.Contain("value"));
        }

        [Test]
        public void SyncModeValidator_TreatsSyncAttributeAsSynced()
        {
            var validator = new SyncModeValidator();
            MethodInfo getSyncedFields = typeof(SyncModeValidator).GetMethod("GetSyncedFields", BindingFlags.Instance | BindingFlags.NonPublic);

            var syncedFields = (List<FieldInfo>)getSyncedFields.Invoke(validator, new object[] { typeof(SyncFieldFixture) });

            Assert.That(syncedFields.Select(field => field.Name), Does.Contain("value"));
        }

        [Test]
        public void UninitializedSyncedArrayValidator_DoesNotFlagInitializedSyncArray()
        {
            var validator = new UninitializedSyncedArrayValidator();
            var context = new ValidationContext
            {
                BehaviourTypes = new List<System.Type> { typeof(InitializedSyncArrayValidationFixture) }
            };

            List<ValidationIssue> issues = validator.Validate(context).ToList();

            Assert.That(issues, Is.Empty);
        }

        [Test]
        public void UninitializedSyncedArrayValidator_TreatsSyncAttributeAsSynced()
        {
            var validator = new UninitializedSyncedArrayValidator();
            MethodInfo hasSyncAttribute = typeof(UninitializedSyncedArrayValidator)
                .GetMethod("HasUdonSyncedAttribute", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo field = typeof(InitializedSyncArrayValidationFixture).GetField("values", BindingFlags.Instance | BindingFlags.Public);

            bool isSynced = (bool)hasSyncAttribute.Invoke(validator, new object[] { field });

            Assert.That(isSynced, Is.True);
        }
    }
}
