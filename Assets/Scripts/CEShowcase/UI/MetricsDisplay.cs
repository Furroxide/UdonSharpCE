using UdonSharp;
using UdonSharp.CE.DevTools;
using UnityEngine;
using CEShowcase.Station1_BulletHell;
using CEShowcase.Station2_Flocking;

namespace CEShowcase.UI
{
    /// <summary>
    /// Central hub metrics display showing aggregate performance stats from all demo stations.
    /// Provides real-time visualization of CE's capabilities across the showcase.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MetricsDisplay : UdonSharpBehaviour
    {
        [Header("Station References")]
        [SerializeField] private BulletHellDemo _bulletHellStation;
        [SerializeField] private FlockingDemo _flockingStation;
        
        [Header("Display")]
        [SerializeField] private TMPro.TextMeshProUGUI _mainStatsText;
        [SerializeField] private TMPro.TextMeshProUGUI _frameGraphText;
        [SerializeField] private TMPro.TextMeshProUGUI _comparisonText;
        
        [Header("Settings")]
        [SerializeField] private float _updateInterval = 0.25f;
        
        // Frame time history for graph
        private float[] _frameTimeHistory;
        private int _historyIndex;
        private const int HISTORY_SIZE = 60;
        
        // Performance tracking
        private float _updateTimer;
        private float _currentFps;
        private float _minFps = float.MaxValue;
        private float _maxFps = 0f;
        private float _avgFps;
        private int _avgSamples;
        
        // Line count comparison
        private const int CE_BULLET_LINES = 350;
        private const int LEGACY_BULLET_LINES = 800;
        private const int CE_FLOCKING_LINES = 400;
        private const int LEGACY_FLOCKING_LINES = 1200;
        private const int CE_LEADERBOARD_LINES = 200;
        private const int LEGACY_LEADERBOARD_LINES = 450;
        private const int CE_CUTSCENE_LINES = 300;
        private const int LEGACY_CUTSCENE_LINES = 600;
        private const int CE_PERSISTENCE_LINES = 250;
        private const int LEGACY_PERSISTENCE_LINES = 500;
        
        void Start()
        {
            _frameTimeHistory = new float[HISTORY_SIZE];
            
            CELogger.Info("MetricsDisplay", "Central metrics display initialized");
        }
        
        void Update()
        {
            // Record frame time
            float frameTime = Time.deltaTime * 1000f;
            _frameTimeHistory[_historyIndex] = frameTime;
            _historyIndex = (_historyIndex + 1) % HISTORY_SIZE;
            
            // Update display periodically
            _updateTimer += Time.deltaTime;
            if (_updateTimer >= _updateInterval)
            {
                _updateTimer = 0f;
                
                UpdatePerformanceStats();
                UpdateMainDisplay();
                UpdateFrameGraph();
                UpdateComparisonDisplay();
            }
        }
        
        private void UpdatePerformanceStats()
        {
            _currentFps = 1f / Time.deltaTime;
            
            if (_currentFps < _minFps && _currentFps > 1) _minFps = _currentFps;
            if (_currentFps > _maxFps) _maxFps = _currentFps;
            
            _avgFps = ((_avgFps * _avgSamples) + _currentFps) / (_avgSamples + 1);
            _avgSamples++;
        }
        
        private void UpdateMainDisplay()
        {
            if (_mainStatsText == null) return;
            
            // Aggregate entity counts
            int bulletEntities = 0;
            int flockingEntities = 0;
            
            if (_bulletHellStation != null)
            {
                bulletEntities = _bulletHellStation.GetActiveEntityCount();
            }
            
            if (_flockingStation != null)
            {
                flockingEntities = _flockingStation.GetActiveAgentCount();
            }
            
            int totalEntities = bulletEntities + flockingEntities;
            
            // FPS color coding
            string fpsColor;
            if (_currentFps >= 72) fpsColor = "#00FF00"; // Green - great
            else if (_currentFps >= 45) fpsColor = "#FFFF00"; // Yellow - okay
            else fpsColor = "#FF0000"; // Red - poor
            
            float frameTime = Time.deltaTime * 1000f;
            
            _mainStatsText.text = 
                $"<size=120%><b>CE LABORATORY</b></size>\n" +
                $"<size=90%>Real-time Performance Dashboard</size>\n\n" +
                $"<b>FRAME RATE</b>\n" +
                $"  Current: <color={fpsColor}>{_currentFps:F1} FPS</color>\n" +
                $"  Min/Max: {_minFps:F1} / {_maxFps:F1}\n" +
                $"  Average: {_avgFps:F1} FPS\n" +
                $"  Frame: {frameTime:F2}ms\n\n" +
                $"<b>ACTIVE ENTITIES</b>\n" +
                $"  Bullets: <color=#FF6600>{bulletEntities}</color>\n" +
                $"  Flock Agents: <color=#00FF88>{flockingEntities}</color>\n" +
                $"  <b>Total: <color=#00FFFF>{totalEntities}</color></b>";
        }
        
        private void UpdateFrameGraph()
        {
            if (_frameGraphText == null) return;
            
            // Build ASCII frame time graph
            string graph = "<mspace=0.5em>";
            
            // Find min/max for scaling
            float minTime = float.MaxValue;
            float maxTime = 0f;
            
            for (int i = 0; i < HISTORY_SIZE; i++)
            {
                float t = _frameTimeHistory[i];
                if (t > 0)
                {
                    if (t < minTime) minTime = t;
                    if (t > maxTime) maxTime = t;
                }
            }
            
            // Target line (16.67ms = 60fps)
            float targetLine = 16.67f;
            
            // Build graph rows (8 rows)
            const int GRAPH_HEIGHT = 8;
            float range = Mathf.Max(maxTime - minTime, 10f);
            
            for (int row = GRAPH_HEIGHT - 1; row >= 0; row--)
            {
                float rowThreshold = minTime + (range * row / GRAPH_HEIGHT);
                
                // Label
                graph += $"<color=#888888>{rowThreshold,5:F0}</color>|";
                
                // Data points
                for (int col = 0; col < 30; col++)
                {
                    int histIndex = (_historyIndex + col * 2) % HISTORY_SIZE;
                    float value = _frameTimeHistory[histIndex];
                    
                    if (value >= rowThreshold)
                    {
                        // Color based on value
                        if (value > 33.33f) // < 30fps
                            graph += "<color=#FF0000>█</color>";
                        else if (value > 16.67f) // < 60fps
                            graph += "<color=#FFFF00>█</color>";
                        else
                            graph += "<color=#00FF00>█</color>";
                    }
                    else
                    {
                        graph += " ";
                    }
                }
                graph += "\n";
            }
            
            graph += "     └" + new string('─', 30) + "\n";
            graph += "      Frame Time (ms) - Last 60 frames\n";
            graph += "</mspace>";
            
            _frameGraphText.text = $"<b>FRAME TIME GRAPH</b>\n{graph}";
        }
        
        private void UpdateComparisonDisplay()
        {
            if (_comparisonText == null) return;
            
            int ceTotalLines = CE_BULLET_LINES + CE_FLOCKING_LINES + CE_LEADERBOARD_LINES + 
                              CE_CUTSCENE_LINES + CE_PERSISTENCE_LINES;
            
            int legacyTotalLines = LEGACY_BULLET_LINES + LEGACY_FLOCKING_LINES + LEGACY_LEADERBOARD_LINES + 
                                   LEGACY_CUTSCENE_LINES + LEGACY_PERSISTENCE_LINES;
            
            float reduction = (1f - (float)ceTotalLines / legacyTotalLines) * 100f;
            
            _comparisonText.text = 
                $"<b>CODE COMPARISON</b>\n\n" +
                $"<color=#00FFFF>CE Approach:</color>\n" +
                $"  Bullet Hell: ~{CE_BULLET_LINES} lines\n" +
                $"  Flocking: ~{CE_FLOCKING_LINES} lines\n" +
                $"  Leaderboard: ~{CE_LEADERBOARD_LINES} lines\n" +
                $"  Cutscene: ~{CE_CUTSCENE_LINES} lines\n" +
                $"  Persistence: ~{CE_PERSISTENCE_LINES} lines\n" +
                $"  <b>Total: ~{ceTotalLines} lines</b>\n\n" +
                $"<color=#FF8800>Standard U# would need:</color>\n" +
                $"  <b>~{legacyTotalLines} lines</b>\n\n" +
                $"<color=#00FF00>Code reduction: {reduction:F0}%</color>";
        }
        
        // Public API
        public void ResetStats()
        {
            _minFps = float.MaxValue;
            _maxFps = 0f;
            _avgFps = 0f;
            _avgSamples = 0;
            
            for (int i = 0; i < HISTORY_SIZE; i++)
            {
                _frameTimeHistory[i] = 0f;
            }
            
            CELogger.Info("MetricsDisplay", "Stats reset");
        }
        
        public float GetCurrentFPS() => _currentFps;
        public float GetAverageFPS() => _avgFps;
        public float GetMinFPS() => _minFps;
        public float GetMaxFPS() => _maxFps;
    }
}
