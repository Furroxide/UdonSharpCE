using System.Collections.Generic;
using UdonSharp;
using UdonSharp.CE.Persistence;
using UdonSharp.CE.Procgen;
using VRC.SDK3.Data;

namespace CESyncRuntimeTests
{
    /// <summary>
    /// Fixture for CEProcgenPersistenceUdonVmRegressionTests: runs CE.Procgen and CE.Persistence as a compiled
    /// Udon program so the test can compare what the VM produced with what the same calls produce in .NET.
    ///
    /// The defects this covers do not exist in .NET. UdonSharp lowers every numeric cast to System.Convert,
    /// which throws where C# wraps, and an exception halts the behaviour: a negative seed, a noise coordinate
    /// beyond int range, a float.MaxValue size estimate and a NaN dungeon connectivity all halted here while
    /// passing every editor test. CEDungeon and WFCSolver used multidimensional arrays, which UdonSharp cannot
    /// compile at all. Each Run method therefore ends by setting its flag, so a flag still false afterwards
    /// means the VM halted part-way, and every result is copied into a plain public field for the test to read.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CEProcgenPersistenceVmTestBehaviour : UdonSharpBehaviour
    {
        // ---- RunRandom -------------------------------------------------------------------------

        public bool randomRan;
        public int[] negativeSeedDraws;
        public int[] lowestSeedDraws;
        public int[] wideRangeDraws;
        public int[] inclusiveTopDraws;
        public int clockSeededDistinctDraws;

        // ---- RunNoise --------------------------------------------------------------------------

        public bool noiseRan;
        public float[] simplexSamples;
        public float[] latticeSamples;
        public float farCoordinateSample;
        public float noOctaveSample;
        public float worleySeedOne;
        public float worleySeedTwo;

        // ---- RunDungeon ------------------------------------------------------------------------

        public bool dungeonRan;
        public int[] dungeonSummary;
        public int hostileConnectivityRooms;

        // ---- RunWfc ----------------------------------------------------------------------------

        public bool wfcRan;
        public int wfcStatus;
        public int[] wfcGrid;

        // ---- RunSizeEstimator ------------------------------------------------------------------

        public bool estimatorRan;
        public int[] estimates;

        // ---- RunPersistence --------------------------------------------------------------------

        public bool persistenceRan;
        public string persistenceJson;
        public bool persistenceRestored;
        public string restoredName;
        public long restoredBig;
        public ulong restoredHuge;
        public int[] restoredScores;
        public string[] restoredTags;
        public bool legacyNumberRestored;
        public long legacyNumberValue;
        public bool corruptRejected;
        public bool outOfRangeRejected;
        public int validationErrorCount;

        public void RunRandom()
        {
            CERandom negative = new CERandom(-12345);
            negativeSeedDraws = new int[8];
            for (int i = 0; i < negativeSeedDraws.Length; i++)
                negativeSeedDraws[i] = negative.NextInt();

            CERandom lowest = new CERandom(int.MinValue);
            lowestSeedDraws = new int[8];
            for (int i = 0; i < lowestSeedDraws.Length; i++)
                lowestSeedDraws[i] = lowest.Range(-50, 50);

            CERandom wide = new CERandom(99);
            wideRangeDraws = new int[16];
            for (int i = 0; i < wideRangeDraws.Length; i++)
                wideRangeDraws[i] = wide.Range(int.MinValue, int.MaxValue);

            CERandom inclusive = new CERandom(7);
            inclusiveTopDraws = new int[8];
            for (int i = 0; i < inclusiveTopDraws.Length; i++)
                inclusiveTopDraws[i] = inclusive.RangeInclusive(5, int.MaxValue);

            // The clock seed is different every run, so only its shape can be checked: a generator stuck in
            // the all-zero state returns 0 for ever.
            CERandom clock = new CERandom();
            int first = clock.NextInt();
            int distinct = 1;
            for (int i = 0; i < 7; i++)
            {
                if (clock.NextInt() != first)
                    distinct++;
            }

            clockSeededDistinctDraws = distinct;
            randomRan = true;
        }

        public void RunNoise()
        {
            CENoise.Initialize(-7);

            simplexSamples = new float[64];
            int n = 0;
            for (int x = 0; x < 4; x++)
            {
                for (int y = 0; y < 4; y++)
                {
                    for (int z = 0; z < 4; z++)
                    {
                        simplexSamples[n] = CENoise.Simplex3D(x * 61.7f - 100f, y * 53.3f - 100f, z * 47.9f - 100f);
                        n++;
                    }
                }
            }

            latticeSamples = new float[4];
            latticeSamples[0] = CENoise.Perlin2D(-2f, 0.37f);
            latticeSamples[1] = CENoise.Perlin2D(-1.9999f, 0.37f);
            latticeSamples[2] = CENoise.Perlin1D(-17f);
            latticeSamples[3] = CENoise.Perlin3D(0.4f, -3f, 0.7f);

            farCoordinateSample = CENoise.Perlin2D(3.0e9f, 0.5f);
            noOctaveSample = CENoise.Fractal2D(0.5f, 0.5f, 0);

            CENoise.Initialize(1);
            worleySeedOne = CENoise.Worley2D(3.3f, 7.7f);
            CENoise.Initialize(999);
            worleySeedTwo = CENoise.Worley2D(3.3f, 7.7f);

            noiseRan = true;
        }

        public void RunDungeon()
        {
            DungeonLayout layout = CEDungeon.Generate(new CERandom(2024), new DungeonConfig());
            dungeonSummary = Summarize(layout);

            DungeonConfig hostile = new DungeonConfig();
            hostile.RoomCount = 8;
            hostile.Connectivity = float.NaN;
            hostileConnectivityRooms = CEDungeon.Generate(new CERandom(5), hostile).Rooms.Length;

            dungeonRan = true;
        }

        /// <summary>
        /// Flattens a layout into ints. The test builds the same summary from the .NET layout, so the two
        /// arrays agree only if the VM produced the identical dungeon.
        /// </summary>
        public static int[] Summarize(DungeonLayout layout)
        {
            DungeonRoom[] rooms = layout.Rooms;
            DungeonCorridor[] corridors = layout.Corridors;

            int[] summary = new int[8 + rooms.Length * 5 + corridors.Length * 3];
            summary[0] = rooms.Length;
            summary[1] = corridors.Length;
            summary[2] = layout.CriticalPath.Length;
            summary[4] = layout.BoundsMin.x;
            summary[5] = layout.BoundsMin.y;
            summary[6] = layout.BoundsSize.x;
            summary[7] = layout.BoundsSize.y;

            int index = 8;
            for (int i = 0; i < rooms.Length; i++)
            {
                summary[index] = rooms[i].Position.x;
                summary[index + 1] = rooms[i].Position.y;
                summary[index + 2] = rooms[i].Size.x;
                summary[index + 3] = rooms[i].Size.y;
                summary[index + 4] = (int)rooms[i].Type;
                index += 5;
            }

            int flagged = 0;
            for (int i = 0; i < corridors.Length; i++)
            {
                summary[index] = corridors[i].FromRoom;
                summary[index + 1] = corridors[i].ToRoom;
                summary[index + 2] = corridors[i].Path.Length;
                index += 3;

                if (corridors[i].IsOnCriticalPath)
                    flagged++;
            }

            summary[3] = flagged;
            return summary;
        }

        public void RunWfc()
        {
            WFCTile[] tiles = new WFCTile[3];

            // A negative id, a zero weight, and adjacency that forces the zero-weight tile in places: each
            // is a path that used to fail or never finish.
            int[] water = new int[2];
            water[0] = -1;
            water[1] = 4;
            int[] any = new int[3];
            any[0] = -1;
            any[1] = 4;
            any[2] = 9;
            int[] rock = new int[2];
            rock[0] = 4;
            rock[1] = 9;

            tiles[0] = MakeTile(-1, 1f, water);
            tiles[1] = MakeTile(4, 2f, any);
            tiles[2] = MakeTile(9, 0f, rock);

            WFCConfig config = new WFCConfig();
            config.Tiles = tiles;
            config.Width = 6;
            config.Height = 5;

            WFCSolver solver = new WFCSolver(config, 31);
            solver.Solve();

            wfcStatus = (int)solver.Status;
            wfcGrid = solver.GetResult();
            wfcRan = true;
        }

        public static WFCTile MakeTile(int id, float weight, int[] neighbours)
        {
            WFCTile tile = new WFCTile();
            tile.Id = id;
            tile.Weight = weight;
            tile.ValidNorth = neighbours;
            tile.ValidSouth = neighbours;
            tile.ValidEast = neighbours;
            tile.ValidWest = neighbours;
            return tile;
        }

        public void RunSizeEstimator()
        {
            estimates = new int[6];
            estimates[0] = SizeEstimator.EstimateFloatSize(float.MaxValue);
            estimates[1] = SizeEstimator.EstimateDoubleSize(double.MaxValue);
            estimates[2] = SizeEstimator.EstimateDoubleSize(-1e300);

            // Nested containers go through the [RecursiveMethod] estimators, which only Udon can get wrong.
            DataDictionary inner = new DataDictionary();
            inner["depth"] = 2;
            DataList middle = new DataList();
            middle.Add(inner);
            middle.Add("text");
            DataDictionary outer = new DataDictionary();
            outer["list"] = middle;
            outer["n"] = 12.5;
            estimates[3] = SizeEstimator.EstimateDataDictionarySize(outer);

            DataDictionary oddKeys = new DataDictionary();
            oddKeys[5] = "five";
            oddKeys["s"] = "x";
            estimates[4] = SizeEstimator.EstimateDataDictionarySize(oddKeys);

            estimates[5] = SizeEstimator.EstimateStringSize("日本語");
            estimatorRan = true;
        }

        public void RunPersistence()
        {
            CEProcgenPersistenceVmModel model = new CEProcgenPersistenceVmModel();
            model.name = "vm";
            model.big = long.MinValue;
            model.huge = ulong.MaxValue;
            model.scores = new int[3];
            model.scores[0] = 3;
            model.scores[1] = -1;
            model.scores[2] = 40;
            model.tags = new string[2];
            model.tags[0] = "first";
            model.ratio = 0.25f;
            model.flag = true;

            persistenceJson = CEPersistence.ToJson(model);

            CEProcgenPersistenceVmModel restored;
            persistenceRestored = CEPersistence.FromJson(persistenceJson, out restored) == RestoreResult.Success;
            if (persistenceRestored)
            {
                restoredName = restored.name;
                restoredBig = restored.big;
                restoredHuge = restored.huge;
                restoredScores = restored.scores;
                restoredTags = restored.tags;
            }

            // A save written before 64-bit fields were stored as strings holds a plain JSON number.
            CEProcgenPersistenceVmModel legacy;
            legacyNumberRestored = CEPersistence.FromJson("{\"big\":12345}", out legacy) == RestoreResult.Success;
            if (legacyNumberRestored)
                legacyNumberValue = legacy.big;

            CEProcgenPersistenceVmModel corrupt;
            corruptRejected = CEPersistence.FromJson("{\"scores\":[1,\"two\"]}", out corrupt) == RestoreResult.ParseError;

            CEProcgenPersistenceVmModel outOfRange;
            outOfRangeRejected = CEPersistence.FromJson("{\"scores\":[1e300]}", out outOfRange) == RestoreResult.ParseError;

            model.ratio = 2f;
            List<ValidationError> errors;
            CEPersistence.Validate(model, out errors);
            validationErrorCount = errors != null ? errors.Count : -1;

            persistenceRan = true;
        }
    }

    /// <summary>
    /// A field-converted persistence model, so the VM runs the generated converters rather than the raw
    /// DataDictionary path the showcase uses. Its key is unique to this fixture.
    /// </summary>
    [PlayerData("ce_test_vm_persistence_model", Version = 3)]
    public class CEProcgenPersistenceVmModel
    {
        [PersistKey("name")] public string name;
        [PersistKey("big")] public long big;
        [PersistKey("huge")] public ulong huge;
        [PersistKey("scores")] public int[] scores;
        [PersistKey("tags")] public string[] tags;
        [PersistKey("ratio"), Range(0, 1)] public float ratio;
        [PersistKey("flag")] public bool flag;
    }
}
