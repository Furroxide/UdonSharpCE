using System;
using System.Collections.Generic;
using NUnit.Framework;
using UdonSharp;
using UdonSharp.CE.Tests;
using UdonSharp.Tests.CE.Net;
using UdonSharpEditor;

namespace UdonSharp.CE.Tests.Net
{
    public class SyncAttributeIntegrationTests
    {
        private readonly List<string> _createdAssetPaths = new List<string>();

        [TearDown]
        public void TearDown()
        {
            EditorTestUtilities.DeleteAssets(_createdAssetPaths);
            EditorTestUtilities.CompileAllPrograms();
        }

        [Test]
        public void SyncAttribute_ProducesLinearSyncMetadataAndSyncAssemblyTag()
        {
            var asset = EditorTestUtilities.CreateProgramAssetForBehaviourType(typeof(SyncLinearIntegrationTestBehaviour), _createdAssetPaths);

            EditorTestUtilities.CompileAllPrograms();

            Assert.That(EditorTestUtilities.GetAssemblyError(asset), Is.Null.Or.Empty);
            Assert.That(asset.fieldDefinitions.ContainsKey("pos"), Is.True);
            Assert.That(asset.fieldDefinitions["pos"].SyncMode, Is.EqualTo(UdonSyncMode.Linear));
            Assert.That(EditorTestUtilities.GetUasm(asset), Does.Contain("sync").IgnoreCase);
        }

        [Test]
        public void SyncAttribute_NamedInterpolationProperty_ProducesLinearSyncMetadata()
        {
            var asset = EditorTestUtilities.CreateProgramAssetForBehaviourType(typeof(SyncNamedInterpolationIntegrationTestBehaviour), _createdAssetPaths);

            EditorTestUtilities.CompileAllPrograms();

            Assert.That(EditorTestUtilities.GetAssemblyError(asset), Is.Null.Or.Empty);
            Assert.That(asset.fieldDefinitions.ContainsKey("pos"), Is.True);
            Assert.That(asset.fieldDefinitions["pos"].SyncMode, Is.EqualTo(UdonSyncMode.Linear));
        }

        [Test]
        public void SyncAttribute_TriggersExistingManualModeCompatibilityError()
        {
            var asset = EditorTestUtilities.CreateProgramAssetForBehaviourType(typeof(SyncManualLinearIntegrationTestBehaviour), _createdAssetPaths);

            EditorTestUtilities.CompileAllPrograms();

            string error = EditorTestUtilities.GetAssemblyError(asset);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(error, Does.Contain("Manual sync"));
        }

        [Test]
        public void SyncAttribute_TriggersExistingUnsupportedTypeError()
        {
            var asset = EditorTestUtilities.CreateProgramAssetForBehaviourType(typeof(SyncInvalidTypeIntegrationTestBehaviour), _createdAssetPaths);

            EditorTestUtilities.CompileAllPrograms();

            string error = EditorTestUtilities.GetAssemblyError(asset);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(error, Does.Contain("not supported by Udon sync"));
        }
    }
}
