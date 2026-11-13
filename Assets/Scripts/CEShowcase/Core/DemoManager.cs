using UdonSharp;
using UdonSharp.CE.DevTools;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;

namespace CEShowcase.Core
{
    /// <summary>
    /// Central coordinator for all CE demo stations.
    /// Manages station activation, global state, and provides shared utilities.
    /// 
    /// The CE Laboratory showcases the full power of UdonSharpCE:
    /// - Station 1: Bullet Storm (ECS + Pooling via CEWorld/CEPool)
    /// - Station 2: Crowd Simulation (Spatial partitioning via CEGrid)
    /// - Station 3: Leaderboard (Collections via CEDictionary)
    /// - Station 4: Cutscene Theater (Async via UdonTask)
    /// - Station 5: Persistent Inventory (Persistence via CEPersistence)
    /// - Station 6: Procgen Lab (Generation via CERandom/CENoise)
    /// - Station 7: Network Hub (Multiplayer via CE.Net)
    /// </summary>
    // Manual sync: station state is shared world state, so every client must agree on it. It was
    // previously BehaviourSyncMode.None with purely local toggles, which meant two players were
    // essentially never looking at the same world.
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class DemoManager : UdonSharpBehaviour
    {
        [Header("Station References")]
        [SerializeField] private GameObject _station1_BulletHell;
        [SerializeField] private GameObject _station2_Flocking;
        [SerializeField] private GameObject _station3_Leaderboard;
        [SerializeField] private GameObject _station4_Cutscene;
        [SerializeField] private GameObject _station5_Persistence;
        [SerializeField] private GameObject _station6_Procgen;
        [SerializeField] private GameObject _station7_Networking;
        
        [Header("Performance Monitor")]
        [SerializeField] private PerformanceMonitor _performanceMonitor;

        [Header("UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _welcomeText;
        [SerializeField] private TMPro.TextMeshProUGUI _featureListText;
        
        private const int StationCount = 7;

        // VRChat's Environment layer -- the layer a world's standing collision belongs on. Anything on it
        // is treated as part of the world rather than as station content; see IsWorldGeometry.
        private const int EnvironmentLayer = 11;

        // Synced station state. Initialized at declaration -- an uninitialized synced array does not
        // sync at all in Udon. Defaults to all-visible so a fresh instance shows the showcase
        // immediately; owner and late-joiner heap defaults agree, and OnDeserialization still wins.
        [UdonSynced] private bool[] _stationActive = new bool[] { true, true, true, true, true, true, true };

        // What this client has actually applied. OnDeserialization fires for every synced write in the
        // world, and a full sweep walks station 1's 2000-bullet pool and station 2's 500 agents through
        // GetComponentsInChildren four times over. Only the stations whose state moved need touching.
        private bool[] _appliedState;

        void Start()
        {
            CELogger.Info("DemoManager", "CE Showcase Demo World initialized - 7 stations available");

            // Apply whatever state we have. A late joiner may already have received _stationActive
            // before Start ran, so this must not reset it -- only the object's owner may write.
            ApplyAllStations();

            // Display welcome message
            UpdateWelcomeText();
            UpdateFeatureList();
        }

        private void OnEnable()
        {
            // Re-apply on enable. Shared station state only ever reaches a client through Start or
            // OnDeserialization, and a disabled behaviour receives neither -- so a client whose
            // DemoManager is switched on later would render the compiled heap default (every station
            // hidden) until the owner happened to serialize again, which may be never once the world has
            // settled. Idempotent and writes no synced field, so a non-owner running it is harmless.
            //
            // The component states may have been changed by anything while this was disabled, so drop the
            // applied-state cache and re-assert all seven.
            _appliedState = null;
            ApplyAllStations();
        }

        public override void OnDeserialization()
        {
            ApplyAllStations();
        }

        /// <summary>
        /// Applies a station's shared state locally.
        ///
        /// Deliberately does NOT call SetActive on the station root. The station roots host every synced
        /// UdonBehaviour in the world (leaderboard, cutscene, inventory, the five collectibles, procgen),
        /// and a disabled UdonBehaviour in VRChat stops running Update and stops receiving
        /// OnDeserialization, OnPlayerJoined/Left/Restored, SendCustomNetworkEvent and SendCustomEvent.
        /// Disabling the roots is therefore the same as switching sync off for the whole showcase.
        ///
        /// Instead we toggle renderers, colliders and canvases, which is the same approach
        /// CollectibleItem.SetVisualActive already uses to avoid deactivating its own Udon host.
        ///
        /// Ground geometry is exempt -- see <see cref="IsWorldGeometry"/>.
        /// </summary>
        private void SetStationVisible(GameObject root, bool visible)
        {
            if (root == null)
                return;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null || IsWorldGeometry(renderers[i].gameObject))
                    continue;

                renderers[i].enabled = visible;
            }

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] == null || IsWorldGeometry(colliders[i].gameObject))
                    continue;

                colliders[i].enabled = visible;
            }

            Canvas[] canvases = root.GetComponentsInChildren<Canvas>(true);
            for (int i = 0; i < canvases.Length; i++)
            {
                if (canvases[i] != null)
                    canvases[i].enabled = visible;
            }

            // Light is not a Renderer, so the loop above never reaches it. Without this, station 4's
            // stage lights and station 6's dig-site glows keep pouring light into the room from a
            // station that is otherwise invisible.
            Light[] lights = root.GetComponentsInChildren<Light>(true);
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i] != null)
                    lights[i].enabled = visible;
            }

            // Station 4 renders its stage to a RenderTexture; a hidden station should not keep paying
            // for a camera whose screen is switched off.
            Camera[] cameras = root.GetComponentsInChildren<Camera>(true);
            for (int i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] != null)
                    cameras[i].enabled = visible;
            }

            // Same reasoning for sound: hiding the theater mid-cutscene should not leave its music
            // playing out of thin air. Disabling an AudioSource stops it; re-enabling stays silent
            // until something calls Play, which is what we want.
            AudioSource[] audioSources = root.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < audioSources.Length; i++)
            {
                if (audioSources[i] != null)
                    audioSources[i].enabled = visible;
            }

            // Note the invariant this sweep imposes: showing a station writes enabled = true on every
            // renderer and collider under it, so a component a station author deliberately left disabled
            // comes back on. Per-object visibility therefore has to live on a child object the way
            // CollectibleItem does it with CE_Visual -- the five collectibles used to carry a second copy
            // of their mesh on the host object as well, and this sweep resurrected it behind every pickup.
            //
            // The same holds for the Light, Camera and AudioSource loops above: this sweep owns their
            // enabled flags outright. A station that needs one of its own lights dark while it is visible
            // has to say so with intensity, which is what DigSite and TheaterEffects both do.
        }

        /// <summary>
        /// Station platforms and their guard rails live on VRChat's Environment layer, which is where a
        /// world's standing collision belongs, and they are never toggled.
        ///
        /// Hiding a station used to disable its floor collider along with the demo content on top of it.
        /// Stations 1, 2 and 6 overhang the hub floor -- station 6 reaches x=52.1 where the hub floor
        /// stops at x=50, and its buttons sit right out there -- so switching those stations off opened a
        /// hole in the world and anyone standing on them fell until the respawn height caught them.
        /// Leaving the renderer on too is deliberate: solid-but-invisible ground reads as a bug.
        /// </summary>
        private bool IsWorldGeometry(GameObject go)
        {
            return go.layer == EnvironmentLayer;
        }

        private GameObject GetStationRoot(int stationIndex)
        {
            switch (stationIndex)
            {
                case 1: return _station1_BulletHell;
                case 2: return _station2_Flocking;
                case 3: return _station3_Leaderboard;
                case 4: return _station4_Cutscene;
                case 5: return _station5_Persistence;
                case 6: return _station6_Procgen;
                case 7: return _station7_Networking;
                default: return null;
            }
        }

        private void ApplyAllStations()
        {
            EnsureStateArray();

            bool firstApply = _appliedState == null || _appliedState.Length != StationCount;
            if (firstApply)
                _appliedState = new bool[StationCount];

            for (int i = 0; i < StationCount; i++)
            {
                if (!firstApply && _appliedState[i] == _stationActive[i])
                    continue;

                SetStationVisible(GetStationRoot(i + 1), _stationActive[i]);
                _appliedState[i] = _stationActive[i];
            }
        }

        private void EnsureStateArray()
        {
            if (_stationActive == null || _stationActive.Length != StationCount)
                _stationActive = new bool[StationCount];
        }

        /// <summary>
        /// Takes ownership so this client's write is not discarded. VRChat silently drops writes to synced
        /// variables from non-owners.
        /// </summary>
        private bool TakeOwnership()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null)
                return false;

            if (!Networking.IsOwner(localPlayer, gameObject))
                Networking.SetOwner(localPlayer, gameObject);

            return Networking.IsOwner(localPlayer, gameObject);
        }
        
        private void UpdateWelcomeText()
        {
            if (_welcomeText == null) return;
            
            _welcomeText.text = 
                "<b>Welcome to the CE Laboratory!</b>\n\n" +
                "Experience the full power of UdonSharpCE:\n" +
                "Pure C# development for VRChat worlds.\n\n" +
                "<size=80%>Visit each station to see CE features in action.</size>";
        }
        
        private void UpdateFeatureList()
        {
            if (_featureListText == null) return;
            
            _featureListText.text = 
                "<b>STATIONS</b>\n\n" +
                "<color=#FF6600>1. Bullet Storm</color> - ECS + Pooling\n" +
                "   <size=70%>CEWorld, CEPool - 2000+ entities</size>\n\n" +
                "<color=#00FF00>2. Crowd Sim</color> - Spatial Partitioning\n" +
                "   <size=70%>CEGrid - 500+ flocking agents</size>\n\n" +
                "<color=#00FFFF>3. Leaderboard</color> - Collections\n" +
                "   <size=70%>CEDictionary - O(1) lookups</size>\n\n" +
                "<color=#FF00FF>4. Theater</color> - Async/Await\n" +
                "   <size=70%>UdonTask - Clean sequential code</size>\n\n" +
                "<color=#FFFF00>5. Inventory</color> - Persistence\n" +
                "   <size=70%>CEPersistence - Save/Load data</size>\n\n" +
                "<color=#FF8800>6. Procgen Lab</color> - Generation\n" +
                "   <size=70%>CERandom, CENoise - Deterministic</size>\n\n" +
                "<color=#8888FF>7. Network Hub</color> - Multiplayer\n" +
                "   <size=70%>[Sync], [Rpc], RateLimiter</size>";
        }
        
        /// <summary>
        /// Toggle a specific station on/off
        /// </summary>
        public void ToggleStation(int stationIndex)
        {
            if (stationIndex < 1 || stationIndex > StationCount)
                return;

            EnsureStateArray();

            if (!TakeOwnership())
            {
                CELogger.Warning("DemoManager", "Could not take ownership; station toggle ignored.");
                return;
            }

            bool active = !_stationActive[stationIndex - 1];
            _stationActive[stationIndex - 1] = active;

            SetStationVisible(GetStationRoot(stationIndex), active);
            if (_appliedState != null && _appliedState.Length == StationCount)
                _appliedState[stationIndex - 1] = active;
            CELogger.Info("DemoManager", "Station " + stationIndex + ": " + (active ? "ON" : "OFF"));

            RequestSerialization();
        }

        /// <summary>
        /// Set all stations active or inactive
        /// </summary>
        public void SetAllStationsActive(bool active)
        {
            EnsureStateArray();

            if (!TakeOwnership())
            {
                CELogger.Warning("DemoManager", "Could not take ownership; station state change ignored.");
                return;
            }

            for (int i = 0; i < StationCount; i++)
                _stationActive[i] = active;

            ApplyAllStations();
            RequestSerialization();
        }
        
        /// <summary>
        /// Get the performance monitor reference
        /// </summary>
        public PerformanceMonitor GetPerformanceMonitor()
        {
            return _performanceMonitor;
        }
        
        // ========================================
        // UI BUTTON CALLBACKS
        // ========================================
        
        public void OnStation1Button() => ToggleStation(1);
        public void OnStation2Button() => ToggleStation(2);
        public void OnStation3Button() => ToggleStation(3);
        public void OnStation4Button() => ToggleStation(4);
        public void OnStation5Button() => ToggleStation(5);
        public void OnStation6Button() => ToggleStation(6);
        public void OnStation7Button() => ToggleStation(7);
        
        public void OnActivateAllButton() => SetAllStationsActive(true);
        public void OnDeactivateAllButton() => SetAllStationsActive(false);
        
        // ========================================
        // STATION STATUS
        // ========================================
        
        /// <summary>
        /// Shared station state. Heavy stations should gate their per-frame work on this rather than
        /// relying on their GameObject being deactivated, which would also switch their sync off.
        /// </summary>
        public bool IsStationActive(int stationIndex)
        {
            if (_stationActive == null || stationIndex < 1 || stationIndex > _stationActive.Length)
                return false;

            return _stationActive[stationIndex - 1];
        }

        public int GetActiveStationCount()
        {
            if (_stationActive == null)
                return 0;

            int count = 0;
            for (int i = 0; i < _stationActive.Length; i++)
            {
                if (_stationActive[i])
                    count++;
            }

            return count;
        }
    }
}
