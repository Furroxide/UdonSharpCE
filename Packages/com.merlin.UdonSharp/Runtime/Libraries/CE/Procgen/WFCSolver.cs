using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.Procgen
{
    /// <summary>
    /// Status of the WFC solving process.
    /// </summary>
    [PublicAPI]
    public enum WFCStatus
    {
        /// <summary>
        /// Solving is in progress.
        /// </summary>
        InProgress,

        /// <summary>
        /// Successfully solved the entire grid.
        /// </summary>
        Completed,

        /// <summary>
        /// Encountered a contradiction (no valid tiles for a cell).
        /// </summary>
        Failed,

        /// <summary>
        /// Manually aborted by user.
        /// </summary>
        Aborted
    }

    /// <summary>
    /// Defines a tile type for Wave Function Collapse.
    /// </summary>
    [PublicAPI]
    public class WFCTile
    {
        /// <summary>
        /// Unique identifier for this tile.
        /// </summary>
        public int Id;

        /// <summary>
        /// Selection weight (higher = more likely to be chosen).
        /// </summary>
        public float Weight = 1f;

        /// <summary>
        /// Valid tile IDs that can be placed to the north (+Y).
        /// </summary>
        public int[] ValidNorth;

        /// <summary>
        /// Valid tile IDs that can be placed to the south (-Y).
        /// </summary>
        public int[] ValidSouth;

        /// <summary>
        /// Valid tile IDs that can be placed to the east (+X).
        /// </summary>
        public int[] ValidEast;

        /// <summary>
        /// Valid tile IDs that can be placed to the west (-X).
        /// </summary>
        public int[] ValidWest;

        /// <summary>
        /// Optional name for debugging.
        /// </summary>
        public string Name;
    }

    /// <summary>
    /// Configuration for the WFC solver.
    /// </summary>
    [PublicAPI]
    public class WFCConfig
    {
        /// <summary>
        /// Available tile definitions.
        /// </summary>
        public WFCTile[] Tiles;

        /// <summary>
        /// Grid width in cells.
        /// </summary>
        public int Width = 16;

        /// <summary>
        /// Grid height in cells.
        /// </summary>
        public int Height = 16;

        /// <summary>
        /// Maximum iterations per Tick() call for time-slicing.
        /// </summary>
        public int MaxIterationsPerFrame = 100;

        /// <summary>
        /// Whether to wrap edges (toroidal topology).
        /// </summary>
        public bool WrapEdges = false;
    }

    /// <summary>
    /// Wave Function Collapse solver for procedural grid generation.
    ///
    /// WFC generates patterns by progressively collapsing possibilities
    /// based on adjacency constraints, creating varied but coherent layouts.
    /// </summary>
    /// <remarks>
    /// Algorithm overview:
    /// 1. Initialize all cells with all possible tiles
    /// 2. Find cell with minimum entropy (fewest possibilities)
    /// 3. Collapse that cell to single tile based on weights
    /// 4. Propagate constraints to neighboring cells
    /// 5. Repeat until fully solved or contradiction detected
    ///
    /// Supports time-slicing via Tick() for frame budget management.
    /// </remarks>
    /// <example>
    /// <code>
    /// // Define tile constraints
    /// var tiles = new WFCTile[]
    /// {
    ///     new WFCTile {
    ///         Id = 0, Name = "Grass", Weight = 3f,
    ///         ValidNorth = new[] { 0, 1 },
    ///         ValidSouth = new[] { 0, 1 },
    ///         ValidEast = new[] { 0, 1 },
    ///         ValidWest = new[] { 0, 1 }
    ///     },
    ///     new WFCTile {
    ///         Id = 1, Name = "Water", Weight = 1f,
    ///         ValidNorth = new[] { 0, 1 },
    ///         ValidSouth = new[] { 0, 1 },
    ///         ValidEast = new[] { 0, 1 },
    ///         ValidWest = new[] { 0, 1 }
    ///     }
    /// };
    ///
    /// var config = new WFCConfig
    /// {
    ///     Tiles = tiles,
    ///     Width = 32,
    ///     Height = 32,
    ///     MaxIterationsPerFrame = 50
    /// };
    ///
    /// var solver = new WFCSolver(config, worldSeed);
    ///
    /// // In Update():
    /// if (solver.Status == WFCStatus.InProgress)
    /// {
    ///     solver.Tick();
    /// }
    /// else if (solver.Status == WFCStatus.Completed)
    /// {
    ///     int[,] result = solver.GetResult();
    ///     SpawnTiles(result);
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    public class WFCSolver
    {
        #region State

        /// <summary>
        /// Current solver status.
        /// </summary>
        public WFCStatus Status { get; private set; }

        /// <summary>
        /// Number of cells that have been collapsed.
        /// </summary>
        public int CollapsedCount { get; private set; }

        /// <summary>
        /// Total number of cells in the grid.
        /// </summary>
        public int TotalCells { get; private set; }

        /// <summary>
        /// Progress as a value from 0 to 1.
        /// </summary>
        public float Progress => TotalCells > 0 ? (float)CollapsedCount / TotalCells : 0f;

        private readonly WFCConfig _config;
        private readonly CERandom _rng;

        // Grid state: possibilities[x, y] is a bitmask of valid tile indices
        private ulong[,] _possibilities;

        // Result grid: result[x, y] is the chosen tile ID (-1 if uncollapsed)
        private int[,] _result;

        // Number of tiles (max 64 for bitmask)
        private int _tileCount;

        // Precomputed: which tiles are valid in each direction for each tile
        private ulong[] _validNorth;
        private ulong[] _validSouth;
        private ulong[] _validEast;
        private ulong[] _validWest;

        // Propagation stack
        private int[] _propagateStackX;
        private int[] _propagateStackY;
        private int _propagateStackSize;

        #endregion

        #region Construction

        /// <summary>
        /// Creates a new WFC solver with the given configuration.
        /// </summary>
        /// <param name="config">Solver configuration.</param>
        /// <param name="seed">Random seed for deterministic generation.</param>
        public WFCSolver(WFCConfig config, int seed)
        {
            _config = config;
            _rng = new CERandom(seed);
            _tileCount = config.Tiles?.Length ?? 0;
            TotalCells = config.Width * config.Height;

            if (_tileCount == 0 || _tileCount > 64)
            {
                Status = WFCStatus.Failed;
                return;
            }

            PrecomputeConstraints();
            Reset();
        }

        /// <summary>
        /// Precomputes constraint bitmasks for efficient propagation.
        /// </summary>
        private void PrecomputeConstraints()
        {
            _validNorth = new ulong[_tileCount];
            _validSouth = new ulong[_tileCount];
            _validEast = new ulong[_tileCount];
            _validWest = new ulong[_tileCount];

            for (int i = 0; i < _tileCount; i++)
            {
                var tile = _config.Tiles[i];

                if (tile.ValidNorth != null)
                {
                    for (int j = 0; j < tile.ValidNorth.Length; j++)
                    {
                        int validIdx = TileIdToIndex(tile.ValidNorth[j]);
                        if (validIdx >= 0)
                            _validNorth[i] |= 1UL << validIdx;
                    }
                }

                if (tile.ValidSouth != null)
                {
                    for (int j = 0; j < tile.ValidSouth.Length; j++)
                    {
                        int validIdx = TileIdToIndex(tile.ValidSouth[j]);
                        if (validIdx >= 0)
                            _validSouth[i] |= 1UL << validIdx;
                    }
                }

                if (tile.ValidEast != null)
                {
                    for (int j = 0; j < tile.ValidEast.Length; j++)
                    {
                        int validIdx = TileIdToIndex(tile.ValidEast[j]);
                        if (validIdx >= 0)
                            _validEast[i] |= 1UL << validIdx;
                    }
                }

                if (tile.ValidWest != null)
                {
                    for (int j = 0; j < tile.ValidWest.Length; j++)
                    {
                        int validIdx = TileIdToIndex(tile.ValidWest[j]);
                        if (validIdx >= 0)
                            _validWest[i] |= 1UL << validIdx;
                    }
                }
            }
        }

        /// <summary>
        /// Converts a tile ID to its array index.
        /// </summary>
        private int TileIdToIndex(int tileId)
        {
            for (int i = 0; i < _tileCount; i++)
            {
                if (_config.Tiles[i].Id == tileId)
                    return i;
            }
            return -1;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Resets the solver to initial state for re-solving.
        /// </summary>
        public void Reset()
        {
            int width = _config.Width;
            int height = _config.Height;

            _possibilities = new ulong[width, height];
            _result = new int[width, height];

            // Initialize all cells with all possibilities
            ulong allPossible = (1UL << _tileCount) - 1;
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    _possibilities[x, y] = allPossible;
                    _result[x, y] = -1;
                }
            }

            _propagateStackX = new int[width * height * 4];
            _propagateStackY = new int[width * height * 4];
            _propagateStackSize = 0;

            CollapsedCount = 0;
            Status = WFCStatus.InProgress;

            _rng.Reset();
        }

        /// <summary>
        /// Performs one or more solve iterations (time-sliced).
        /// Call this from Update() until Status is not InProgress.
        /// </summary>
        /// <returns>Current status after tick.</returns>
        public WFCStatus Tick()
        {
            if (Status != WFCStatus.InProgress)
                return Status;

            int iterations = _config.MaxIterationsPerFrame;

            for (int i = 0; i < iterations; i++)
            {
                // Find cell with minimum entropy
                if (!FindMinEntropyCell(out int x, out int y))
                {
                    // All cells collapsed
                    Status = WFCStatus.Completed;
                    return Status;
                }

                // Collapse that cell
                if (!CollapseCell(x, y))
                {
                    Status = WFCStatus.Failed;
                    return Status;
                }

                // Propagate constraints
                if (!Propagate())
                {
                    Status = WFCStatus.Failed;
                    return Status;
                }
            }

            return Status;
        }

        /// <summary>
        /// Solves the entire grid synchronously.
        /// Use for small grids or non-time-critical generation.
        /// </summary>
        /// <returns>Final status.</returns>
        public WFCStatus Solve()
        {
            while (Status == WFCStatus.InProgress)
            {
                if (!FindMinEntropyCell(out int x, out int y))
                {
                    Status = WFCStatus.Completed;
                    return Status;
                }

                if (!CollapseCell(x, y) || !Propagate())
                {
                    Status = WFCStatus.Failed;
                    return Status;
                }
            }

            return Status;
        }

        /// <summary>
        /// Aborts the current solve operation.
        /// </summary>
        public void Abort()
        {
            Status = WFCStatus.Aborted;
        }

        /// <summary>
        /// Gets the result grid as tile IDs.
        /// </summary>
        /// <returns>2D array of tile IDs (-1 for uncollapsed cells).</returns>
        public int[,] GetResult()
        {
            int width = _config.Width;
            int height = _config.Height;
            int[,] result = new int[width, height];

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    result[x, y] = _result[x, y];
                }
            }

            return result;
        }

        /// <summary>
        /// Gets the tile ID at a specific position.
        /// </summary>
        public int GetTileAt(int x, int y)
        {
            if (x < 0 || x >= _config.Width || y < 0 || y >= _config.Height)
                return -1;
            return _result[x, y];
        }

        /// <summary>
        /// Forces a specific tile at a position before solving.
        /// </summary>
        public bool SetTile(int x, int y, int tileId)
        {
            if (x < 0 || x >= _config.Width || y < 0 || y >= _config.Height)
                return false;

            int tileIndex = TileIdToIndex(tileId);
            if (tileIndex < 0)
                return false;

            _possibilities[x, y] = 1UL << tileIndex;
            _result[x, y] = tileId;
            CollapsedCount++;

            PushPropagate(x, y);
            return Propagate();
        }

        #endregion

        #region Core Algorithm

        /// <summary>
        /// Finds the cell with minimum entropy (fewest possibilities).
        /// </summary>
        private bool FindMinEntropyCell(out int minX, out int minY)
        {
            minX = -1;
            minY = -1;
            int minEntropy = int.MaxValue;
            float minNoise = float.MaxValue;

            int width = _config.Width;
            int height = _config.Height;

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (_result[x, y] >= 0)
                        continue; // Already collapsed

                    int entropy = CountBits(_possibilities[x, y]);

                    if (entropy == 0)
                    {
                        // Contradiction found
                        minX = x;
                        minY = y;
                        return true;
                    }

                    if (entropy < minEntropy ||
                        (entropy == minEntropy && _rng.NextFloat() < minNoise))
                    {
                        minEntropy = entropy;
                        minNoise = _rng.NextFloat();
                        minX = x;
                        minY = y;
                    }
                }
            }

            return minX >= 0;
        }

        /// <summary>
        /// Collapses a cell to a single tile based on weights.
        /// </summary>
        private bool CollapseCell(int x, int y)
        {
            ulong possible = _possibilities[x, y];

            if (possible == 0)
                return false; // Contradiction

            // Calculate total weight of possible tiles
            float totalWeight = 0f;
            for (int i = 0; i < _tileCount; i++)
            {
                if ((possible & (1UL << i)) != 0)
                {
                    totalWeight += _config.Tiles[i].Weight;
                }
            }

            if (totalWeight <= 0f)
                return false;

            // Choose tile based on weight
            float choice = _rng.NextFloat() * totalWeight;
            float accumulated = 0f;
            int chosenIndex = -1;

            for (int i = 0; i < _tileCount; i++)
            {
                if ((possible & (1UL << i)) != 0)
                {
                    accumulated += _config.Tiles[i].Weight;
                    if (choice < accumulated)
                    {
                        chosenIndex = i;
                        break;
                    }
                }
            }

            if (chosenIndex < 0)
            {
                // Fallback: choose last valid tile
                for (int i = _tileCount - 1; i >= 0; i--)
                {
                    if ((possible & (1UL << i)) != 0)
                    {
                        chosenIndex = i;
                        break;
                    }
                }
            }

            if (chosenIndex < 0)
                return false;

            // Collapse to chosen tile
            _possibilities[x, y] = 1UL << chosenIndex;
            _result[x, y] = _config.Tiles[chosenIndex].Id;
            CollapsedCount++;

            PushPropagate(x, y);
            return true;
        }

        /// <summary>
        /// Propagates constraints from collapsed cells to neighbors.
        /// </summary>
        private bool Propagate()
        {
            int width = _config.Width;
            int height = _config.Height;
            bool wrap = _config.WrapEdges;

            while (_propagateStackSize > 0)
            {
                _propagateStackSize--;
                int x = _propagateStackX[_propagateStackSize];
                int y = _propagateStackY[_propagateStackSize];

                ulong possible = _possibilities[x, y];
                if (possible == 0)
                    return false; // Contradiction

                // Calculate what's valid for each neighbor
                ulong validForNorth = 0;
                ulong validForSouth = 0;
                ulong validForEast = 0;
                ulong validForWest = 0;

                for (int i = 0; i < _tileCount; i++)
                {
                    if ((possible & (1UL << i)) != 0)
                    {
                        validForNorth |= _validNorth[i];
                        validForSouth |= _validSouth[i];
                        validForEast |= _validEast[i];
                        validForWest |= _validWest[i];
                    }
                }

                // Propagate to neighbors
                // North (+Y)
                int ny = y + 1;
                if (ny < height || wrap)
                {
                    if (wrap && ny >= height) ny = 0;
                    if (ny < height && PropagateToNeighbor(x, ny, validForNorth))
                    {
                        if (_possibilities[x, ny] == 0) return false;
                    }
                }

                // South (-Y)
                ny = y - 1;
                if (ny >= 0 || wrap)
                {
                    if (wrap && ny < 0) ny = height - 1;
                    if (ny >= 0 && PropagateToNeighbor(x, ny, validForSouth))
                    {
                        if (_possibilities[x, ny] == 0) return false;
                    }
                }

                // East (+X)
                int nx = x + 1;
                if (nx < width || wrap)
                {
                    if (wrap && nx >= width) nx = 0;
                    if (nx < width && PropagateToNeighbor(nx, y, validForEast))
                    {
                        if (_possibilities[nx, y] == 0) return false;
                    }
                }

                // West (-X)
                nx = x - 1;
                if (nx >= 0 || wrap)
                {
                    if (wrap && nx < 0) nx = width - 1;
                    if (nx >= 0 && PropagateToNeighbor(nx, y, validForWest))
                    {
                        if (_possibilities[nx, y] == 0) return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Applies constraint to a neighbor cell.
        /// </summary>
        /// <returns>True if the neighbor's possibilities changed.</returns>
        private bool PropagateToNeighbor(int x, int y, ulong validMask)
        {
            ulong oldPossible = _possibilities[x, y];
            ulong newPossible = oldPossible & validMask;

            if (newPossible == oldPossible)
                return false;

            _possibilities[x, y] = newPossible;

            if (newPossible != 0)
            {
                PushPropagate(x, y);
            }

            return true;
        }

        /// <summary>
        /// Adds a cell to the propagation stack.
        /// </summary>
        private void PushPropagate(int x, int y)
        {
            if (_propagateStackSize < _propagateStackX.Length)
            {
                _propagateStackX[_propagateStackSize] = x;
                _propagateStackY[_propagateStackSize] = y;
                _propagateStackSize++;
            }
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Counts set bits in a ulong (population count).
        /// </summary>
        private static int CountBits(ulong value)
        {
            int count = 0;
            while (value != 0)
            {
                count += (int)(value & 1);
                value >>= 1;
            }
            return count;
        }

        /// <summary>
        /// Gets the number of remaining possibilities for a cell.
        /// </summary>
        public int GetEntropyAt(int x, int y)
        {
            if (x < 0 || x >= _config.Width || y < 0 || y >= _config.Height)
                return 0;
            return CountBits(_possibilities[x, y]);
        }

        /// <summary>
        /// Gets all possible tile IDs for a cell.
        /// </summary>
        public int[] GetPossibilitiesAt(int x, int y)
        {
            if (x < 0 || x >= _config.Width || y < 0 || y >= _config.Height)
                return new int[0];

            ulong possible = _possibilities[x, y];
            int count = CountBits(possible);
            int[] result = new int[count];
            int idx = 0;

            for (int i = 0; i < _tileCount && idx < count; i++)
            {
                if ((possible & (1UL << i)) != 0)
                {
                    result[idx++] = _config.Tiles[i].Id;
                }
            }

            return result;
        }

        #endregion
    }
}
