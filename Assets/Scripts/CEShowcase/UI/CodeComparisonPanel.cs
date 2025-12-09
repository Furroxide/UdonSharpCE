using UdonSharp;
using UdonSharp.CE.DevTools;
using UnityEngine;

namespace CEShowcase.UI
{
    /// <summary>
    /// Displays side-by-side code comparisons between CE and standard UdonSharp approaches.
    /// Shows the simplicity and readability benefits of CE features.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CodeComparisonPanel : UdonSharpBehaviour
    {
        [Header("Display")]
        [SerializeField] private TMPro.TextMeshProUGUI _ceCodeText;
        [SerializeField] private TMPro.TextMeshProUGUI _legacyCodeText;
        [SerializeField] private TMPro.TextMeshProUGUI _titleText;
        [SerializeField] private TMPro.TextMeshProUGUI _explanationText;
        
        [Header("Settings")]
        [SerializeField] private int _currentExample = 0;
        
        // Code examples
        private string[] _titles;
        private string[] _ceCode;
        private string[] _legacyCode;
        private string[] _explanations;
        
        void Start()
        {
            InitializeExamples();
            ShowExample(_currentExample);
            
            CELogger.Debug("CodeComparison", "Code comparison panel initialized");
        }
        
        private void InitializeExamples()
        {
            _titles = new string[]
            {
                "Dictionary Lookup",
                "Object Pooling",
                "Async Sequence",
                "Spatial Query",
                "Data Persistence"
            };
            
            _ceCode = new string[]
            {
                // Dictionary lookup
                "<color=#569CD6>// CE Collections - O(1) lookup</color>\n" +
                "<color=#4EC9B0>Dictionary</color><int, PlayerData> players;\n\n" +
                "<color=#569CD6>// Add player</color>\n" +
                "players.Add(playerId, data);\n\n" +
                "<color=#569CD6>// Get player - O(1)!</color>\n" +
                "<color=#C586C0>if</color> (players.TryGetValue(id, <color=#C586C0>out var</color> p))\n" +
                "{\n" +
                "    p.score += <color=#B5CEA8>100</color>;\n" +
                "}",

                // Object pooling
                "<color=#569CD6>// CE Pool - Zero allocations</color>\n" +
                "<color=#4EC9B0>CEPool</color><GameObject> pool;\n\n" +
                "pool.Initialize(\n" +
                "    CreateBullet,\n" +
                "    OnAcquire,\n" +
                "    OnRelease\n" +
                ");\n\n" +
                "<color=#569CD6>// Get bullet - O(1)</color>\n" +
                "<color=#C586C0>var</color> bullet = pool.Acquire();\n\n" +
                "<color=#569CD6>// Return - O(1)</color>\n" +
                "pool.Release(bullet);",

                // Async sequence
                "<color=#569CD6>// CE Async - Linear flow</color>\n" +
                "<color=#C586C0>public</color> <color=#4EC9B0>UdonTask</color> PlayCutscene()\n" +
                "{\n" +
                "    <color=#C586C0>await</color> FadeOut(<color=#B5CEA8>1f</color>);\n" +
                "    <color=#C586C0>await</color> OpenCurtains();\n" +
                "    <color=#C586C0>await</color> ShowTitle();\n" +
                "    <color=#C586C0>await</color> UdonTask.Delay(<color=#B5CEA8>3f</color>);\n" +
                "    <color=#C586C0>await</color> FadeIn(<color=#B5CEA8>1f</color>);\n" +
                "}\n\n" +
                "<color=#569CD6>// That's it! Clean and readable.</color>",

                // Spatial query
                "<color=#569CD6>// CE Grid - O(1) spatial lookup</color>\n" +
                "<color=#4EC9B0>CEGrid</color> grid = <color=#C586C0>new</color> CEGrid(\n" +
                "    bounds, cellSize\n" +
                ");\n\n" +
                "<color=#569CD6>// Insert entities</color>\n" +
                "grid.Insert(entityId, position);\n\n" +
                "<color=#569CD6>// Query nearby - O(k) not O(n)!</color>\n" +
                "<color=#C586C0>int</color> count = grid.QueryRadius(\n" +
                "    pos, radius, results\n" +
                ");",

                // Data persistence
                "<color=#569CD6>// CE Persistence - Type-safe save</color>\n" +
                "[<color=#4EC9B0>PlayerData</color>(\"rpg_save\")]\n" +
                "<color=#C586C0>class</color> <color=#4EC9B0>SaveData</color>\n" +
                "{\n" +
                "    [<color=#4EC9B0>PersistKey</color>] <color=#C586C0>public int</color> xp;\n" +
                "    [<color=#4EC9B0>PersistKey</color>] <color=#C586C0>public int</color> level;\n" +
                "}\n\n" +
                "<color=#569CD6>// Save - automatic serialization</color>\n" +
                "CEPersistence.Save(myData);\n\n" +
                "<color=#569CD6>// Load - type-safe restore</color>\n" +
                "CEPersistence.Restore(<color=#C586C0>out</color> data);"
            };
            
            _legacyCode = new string[]
            {
                // Legacy dictionary lookup
                "<color=#569CD6>// Standard U# - O(n) array scan</color>\n" +
                "<color=#C586C0>int</color>[] playerIds;\n" +
                "<color=#4EC9B0>PlayerData</color>[] playerData;\n" +
                "<color=#C586C0>int</color> playerCount;\n\n" +
                "<color=#569CD6>// Find player - O(n) every time!</color>\n" +
                "<color=#C586C0>int</color> index = -<color=#B5CEA8>1</color>;\n" +
                "<color=#C586C0>for</color> (<color=#C586C0>int</color> i = <color=#B5CEA8>0</color>; i < playerCount; i++)\n" +
                "{\n" +
                "    <color=#C586C0>if</color> (playerIds[i] == id)\n" +
                "    {\n" +
                "        index = i;\n" +
                "        <color=#C586C0>break</color>;\n" +
                "    }\n" +
                "}\n" +
                "<color=#C586C0>if</color> (index >= <color=#B5CEA8>0</color>)\n" +
                "    playerData[index].score += <color=#B5CEA8>100</color>;",

                // Legacy object pooling
                "<color=#569CD6>// Standard U# - Manual management</color>\n" +
                "<color=#4EC9B0>GameObject</color>[] poolObjects;\n" +
                "<color=#C586C0>bool</color>[] poolActive;\n" +
                "<color=#C586C0>int</color> poolSize;\n\n" +
                "<color=#569CD6>// Get - O(n) scan for free slot</color>\n" +
                "<color=#4EC9B0>GameObject</color> GetFromPool()\n" +
                "{\n" +
                "    <color=#C586C0>for</color> (<color=#C586C0>int</color> i = <color=#B5CEA8>0</color>; i < poolSize; i++)\n" +
                "    {\n" +
                "        <color=#C586C0>if</color> (!poolActive[i])\n" +
                "        {\n" +
                "            poolActive[i] = <color=#569CD6>true</color>;\n" +
                "            poolObjects[i].SetActive(<color=#569CD6>true</color>);\n" +
                "            <color=#C586C0>return</color> poolObjects[i];\n" +
                "        }\n" +
                "    }\n" +
                "    <color=#C586C0>return null</color>;\n" +
                "}\n\n" +
                "<color=#569CD6>// Return - O(n) to find index!</color>\n" +
                "<color=#C586C0>void</color> ReturnToPool(GameObject obj)\n" +
                "{\n" +
                "    <color=#C586C0>for</color> (<color=#C586C0>int</color> i = 0; i < poolSize; i++)\n" +
                "    { ... }\n" +
                "}",

                // Legacy async sequence
                "<color=#569CD6>// Standard U# - State machine hell</color>\n" +
                "<color=#C586C0>int</color> _state;\n" +
                "<color=#C586C0>float</color> _timer;\n\n" +
                "<color=#C586C0>void</color> Update()\n" +
                "{\n" +
                "    _timer -= Time.deltaTime;\n" +
                "    <color=#C586C0>if</color> (_timer > <color=#B5CEA8>0</color>) <color=#C586C0>return</color>;\n" +
                "    \n" +
                "    <color=#C586C0>switch</color> (_state)\n" +
                "    {\n" +
                "        <color=#C586C0>case</color> <color=#B5CEA8>0</color>:\n" +
                "            StartFadeOut();\n" +
                "            _timer = <color=#B5CEA8>1f</color>;\n" +
                "            _state = <color=#B5CEA8>1</color>;\n" +
                "            <color=#C586C0>break</color>;\n" +
                "        <color=#C586C0>case</color> <color=#B5CEA8>1</color>:\n" +
                "            StartCurtains();\n" +
                "            _timer = <color=#B5CEA8>2f</color>;\n" +
                "            _state = <color=#B5CEA8>2</color>;\n" +
                "            <color=#C586C0>break</color>;\n" +
                "        <color=#569CD6>// ... 10 more cases ...</color>\n" +
                "    }\n" +
                "}\n\n" +
                "<color=#569CD6>// 50+ lines for same result!</color>",

                // Legacy spatial query
                "<color=#569CD6>// Standard U# - O(n squared) brute force</color>\n" +
                "<color=#4EC9B0>Vector3</color>[] positions;\n" +
                "<color=#C586C0>int</color> entityCount;\n\n" +
                "<color=#569CD6>// Find nearby - EVERY entity!</color>\n" +
                "<color=#C586C0>int</color>[] FindNearby(Vector3 pos, <color=#C586C0>float</color> r)\n" +
                "{\n" +
                "    <color=#C586C0>int</color>[] temp = <color=#C586C0>new int</color>[<color=#B5CEA8>100</color>];\n" +
                "    <color=#C586C0>int</color> count = <color=#B5CEA8>0</color>;\n" +
                "    \n" +
                "    <color=#569CD6>// O(n) - checks ALL entities</color>\n" +
                "    <color=#C586C0>for</color> (<color=#C586C0>int</color> i = <color=#B5CEA8>0</color>; i < entityCount; i++)\n" +
                "    {\n" +
                "        <color=#C586C0>float</color> d = Vector3.Distance(\n" +
                "            pos, positions[i]\n" +
                "        );\n" +
                "        <color=#C586C0>if</color> (d < r)\n" +
                "            temp[count++] = i;\n" +
                "    }\n" +
                "    <color=#C586C0>return</color> temp;\n" +
                "}\n\n" +
                "<color=#569CD6>// 500 agents = 250,000 checks/frame!</color>",

                // Legacy data persistence
                "<color=#569CD6>// Standard U# - Manual JSON</color>\n" +
                "<color=#C586C0>void</color> SaveData()\n" +
                "{\n" +
                "    <color=#C586C0>string</color> json = \"{\" +\n" +
                "        \"xp:\" + xp + \",\" +\n" +
                "        \"level:\" + level +\n" +
                "        \"}\";\n" +
                "    \n" +
                "    <color=#569CD6>// Manual VRChat API calls</color>\n" +
                "    <color=#569CD6>// No type safety</color>\n" +
                "    <color=#569CD6>// No validation</color>\n" +
                "    <color=#569CD6>// No migration support</color>\n" +
                "}\n\n" +
                "<color=#C586C0>void</color> LoadData(<color=#C586C0>string</color> json)\n" +
                "{\n" +
                "    <color=#569CD6>// Manual parsing</color>\n" +
                "    <color=#569CD6>// Error prone</color>\n" +
                "    <color=#569CD6>// No versioning</color>\n" +
                "    <color=#569CD6>// ... 30+ more lines ...</color>\n" +
                "}"
            };
            
            _explanations = new string[]
            {
                "CE Collections provide O(1) dictionary lookups instead of O(n) array scans. For 80 players, that's 80x faster!",
                "CEPool manages object lifecycle with O(1) acquire/release. No more scanning arrays to find free slots.",
                "UdonTask enables clean async/await syntax. What would be 50+ lines of state machine becomes 10 lines of linear code.",
                "CEGrid spatial partitioning reduces neighbor queries from O(n) to O(k) where k << n. Essential for large entity counts.",
                "CEPersistence provides type-safe serialization with validation, versioning, and migration support built-in."
            };
        }
        
        public void ShowExample(int index)
        {
            if (index < 0 || index >= _titles.Length) return;
            
            _currentExample = index;
            
            if (_titleText != null)
            {
                _titleText.text = "<b>" + _titles[index] + "</b>";
            }
            
            if (_ceCodeText != null)
            {
                _ceCodeText.text = "<b><color=#00FF00>CE Approach</color></b>\n\n" + _ceCode[index];
            }
            
            if (_legacyCodeText != null)
            {
                _legacyCodeText.text = "<b><color=#FF8800>Standard U#</color></b>\n\n" + _legacyCode[index];
            }
            
            if (_explanationText != null)
            {
                _explanationText.text = _explanations[index];
            }
        }
        
        // Navigation
        public void NextExample()
        {
            int next = (_currentExample + 1) % _titles.Length;
            ShowExample(next);
        }
        
        public void PreviousExample()
        {
            int prev = (_currentExample - 1 + _titles.Length) % _titles.Length;
            ShowExample(prev);
        }
        
        // Direct example selection
        public void ShowDictionaryExample() => ShowExample(0);
        public void ShowPoolingExample() => ShowExample(1);
        public void ShowAsyncExample() => ShowExample(2);
        public void ShowSpatialExample() => ShowExample(3);
        public void ShowPersistenceExample() => ShowExample(4);
        
        public int GetCurrentExampleIndex() => _currentExample;
        public int GetExampleCount() => _titles != null ? _titles.Length : 0;
    }
}
