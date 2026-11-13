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
        /// Encountered a contradiction (no valid tiles for a cell), or the configuration cannot be solved
        /// at all (no tiles, more than 64 tiles, a null tile, or a negative grid size).
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
        /// Unique identifier for this tile. Any int is allowed, including negative values.
        /// </summary>
        public int Id;

        /// <summary>
        /// Selection weight (higher = more likely to be chosen).
        /// A weight of zero or less is never chosen while a weighted tile is still possible; such a tile is
        /// only placed in a cell where the constraints have ruled out every weighted one.
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
        /// Available tile definitions. Between 1 and 64 tiles, none of them null.
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
        /// Maximum iterations per Tick() call for time-slicing. Values below 1 are treated as 1.
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
    ///
    /// A contradiction ends the solve with <see cref="WFCStatus.Failed"/>; there is no backtracking, and
    /// Reset() replays the same seed into the same contradiction. To try again, build a solver with a
    /// different seed or loosen the adjacency rules.
    ///
    /// The grid is exposed as one flat array, column by column: cell (x, y) is at index
    /// <c>x * Height + y</c>. UdonSharp cannot compile multidimensional arrays, so a solver that held or
    /// returned an <c>int[,]</c> could not be built into any behaviour.
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
    ///     int[] result = solver.GetResult();   // tile at (x, y) is result[x * solver.Height + y]
    ///     SpawnTiles(result, solver.Width, solver.Height);
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
        public WFCStatus Status => _status;

        /// <summary>
        /// Number of cells that have been collapsed.
        /// </summary>
        public int CollapsedCount => _collapsedCount;

        /// <summary>
        /// Total number of cells in the grid.
        /// </summary>
        public int TotalCells => _totalCells;

        /// <summary>
        /// Progress as a value from 0 to 1.
        /// </summary>
        public float Progress => _totalCells > 0 ? (float)_collapsedCount / _totalCells : 0f;

        /// <summary>
        /// Width of the grid being solved, as read from the configuration at the last Reset().
        /// </summary>
        public int Width => _width;

        /// <summary>
        /// Height of the grid being solved, as read from the configuration at the last Reset().
        /// </summary>
        public int Height => _height;

        private WFCStatus _status;
        private int _collapsedCount;
        private int _totalCells;

        private readonly WFCConfig _config;
        private readonly CERandom _rng;

        // Everything the solve reads is copied out of the caller-owned config at Reset(), so editing the
        // config mid-solve cannot leave the grid arrays and the loops that index them disagreeing on size.
        private WFCTile[] _tiles;
        private int _width;
        private int _height;
        private bool _wrapEdges;

        // Grid state, one entry per cell at index x * _height + y.
        // _possibilities is a bitmask of valid tile indices.
        private ulong[] _possibilities;

        // Chosen tile ID per cell (-1 until the cell is collapsed)
        private int[] _result;

        // Whether each cell has been collapsed. This is a separate flag rather than "_result >= 0": tile
        // IDs are caller-chosen ints, and a tile with a negative ID used to look uncollapsed for ever. It
        // was picked and re-collapsed on every iteration, so Solve() never returned.
        private bool[] _collapsed;

        // Number of tiles (max 64 for bitmask)
        private int _tileCount;

        // Precomputed: which tiles are valid in each direction for each tile
        private ulong[] _validNorth;
        private ulong[] _validSouth;
        private ulong[] _validEast;
        private ulong[] _validWest;

        // Cells waiting for their constraints to be pushed out to their neighbours. A cell is queued at
        // most once (_queued), so one slot per cell is always enough. Without that guard a cell was pushed
        // again for every bit it lost, the fixed-size stack could fill, and the overflow was dropped
        // silently -- leaving neighbours that were never told about a constraint.
        private int[] _propagateStack;
        private bool[] _queued;
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

            Reset();
        }

        /// <summary>
        /// Copies the configuration into the solver and reports whether it can be solved at all.
        /// </summary>
        private bool ReadConfig()
        {
            _tiles = null;
            _tileCount = 0;
            _width = 0;
            _height = 0;
            _totalCells = 0;
            _wrapEdges = false;

            if (_config == null)
                return false;

            int width = _config.Width;
            int height = _config.Height;

            // A negative size cannot be allocated, and neither can a cell count that overflows int. Failing
            // here keeps both a status the caller can read rather than an exception that halts the behaviour.
            if (width < 0 || height < 0)
                return false;

            if (width > 0 && height > int.MaxValue / width)
                return false;

            _width = width;
            _height = height;
            _totalCells = width * height;
            _wrapEdges = _config.WrapEdges;

            WFCTile[] tiles = _config.Tiles;
            if (tiles == null || tiles.Length == 0 || tiles.Length > 64)
                return false;

            for (int i = 0; i < tiles.Length; i++)
            {
                if (tiles[i] == null)
                    return false;
            }

            _tiles = tiles;
            _tileCount = tiles.Length;

            return true;
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
                WFCTile tile = _tiles[i];

                _validNorth[i] = BuildMask(tile.ValidNorth);
                _validSouth[i] = BuildMask(tile.ValidSouth);
                _validEast[i] = BuildMask(tile.ValidEast);
                _validWest[i] = BuildMask(tile.ValidWest);
            }
        }

        /// <summary>
        /// Converts a list of tile IDs into a bitmask of tile indices. Unknown IDs are ignored.
        /// </summary>
        private ulong BuildMask(int[] tileIds)
        {
            ulong mask = 0;

            if (tileIds == null)
                return mask;

            for (int j = 0; j < tileIds.Length; j++)
            {
                int validIdx = TileIdToIndex(tileIds[j]);
                if (validIdx >= 0)
                    mask |= 1UL << validIdx;
            }

            return mask;
        }

        /// <summary>
        /// Converts a tile ID to its array index.
        /// </summary>
        private int TileIdToIndex(int tileId)
        {
            for (int i = 0; i < _tileCount; i++)
            {
                if (_tiles[i].Id == tileId)
                    return i;
            }
            return -1;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Resets the solver to initial state for re-solving. The configuration is read again, so a
        /// solver that failed on an unusable configuration can be reset once the configuration is fixed.
        /// </summary>
        public void Reset()
        {
            _collapsedCount = 0;
            _propagateStackSize = 0;

            if (!ReadConfig())
            {
                // Dropping the grid makes every accessor below take its "no grid" path. They used to
                // dereference arrays that an unusable configuration had never allocated.
                _possibilities = null;
                _result = null;
                _collapsed = null;
                _propagateStack = null;
                _queued = null;
                _status = WFCStatus.Failed;
                return;
            }

            PrecomputeConstraints();

            int cellCount = _totalCells;

            _possibilities = new ulong[cellCount];
            _result = new int[cellCount];
            _collapsed = new bool[cellCount];

            // Initialize all cells with all possibilities. A shift count is taken modulo 64, so
            // (1UL << 64) - 1 is 0, not "all bits": with exactly 64 tiles -- the documented maximum --
            // every cell started with nothing possible and the first step failed.
            ulong allPossible = _tileCount >= 64 ? 0xFFFFFFFFFFFFFFFFUL : (1UL << _tileCount) - 1;
            for (int i = 0; i < cellCount; i++)
            {
                _possibilities[i] = allPossible;
                _result[i] = -1;
            }

            _propagateStack = new int[cellCount];
            _queued = new bool[cellCount];

            _status = WFCStatus.InProgress;

            _rng.Reset();
        }

        /// <summary>
        /// Performs one or more solve iterations (time-sliced).
        /// Call this from Update() until Status is not InProgress.
        /// </summary>
        /// <returns>Current status after tick.</returns>
        public WFCStatus Tick()
        {
            if (_status != WFCStatus.InProgress)
                return _status;

            // A budget of zero or less ran no iteration at all, so the status stayed InProgress for ever
            // and an Update() loop waiting on it never finished.
            int iterations = _config.MaxIterationsPerFrame;
            if (iterations < 1)
                iterations = 1;

            for (int i = 0; i < iterations; i++)
            {
                if (!Step())
                    return _status;
            }

            return _status;
        }

        /// <summary>
        /// Solves the entire grid synchronously.
        /// Use for small grids or non-time-critical generation.
        /// </summary>
        /// <returns>Final status.</returns>
        public WFCStatus Solve()
        {
            // Every step either collapses a cell that was not collapsed before or ends the solve, so this
            // runs at most TotalCells + 1 times.
            while (_status == WFCStatus.InProgress)
            {
                Step();
            }

            return _status;
        }

        /// <summary>
        /// Aborts the current solve operation.
        /// </summary>
        public void Abort()
        {
            _status = WFCStatus.Aborted;
        }

        /// <summary>
        /// Gets the result grid as tile IDs.
        /// </summary>
        /// <returns>
        /// A copy of the grid, <see cref="Width"/> * <see cref="Height"/> long, with cell (x, y) at index
        /// <c>x * Height + y</c>. Uncollapsed cells hold -1; use <see cref="IsCollapsed"/> to tell them
        /// apart from a tile whose ID is -1. Empty when the configuration could not be solved.
        /// </returns>
        public int[] GetResult()
        {
            if (_result == null)
                return new int[0];

            int[] result = new int[_result.Length];

            for (int i = 0; i < result.Length; i++)
            {
                result[i] = _result[i];
            }

            return result;
        }

        /// <summary>
        /// Gets the tile ID at a specific position (-1 if the cell is uncollapsed or out of range).
        /// </summary>
        public int GetTileAt(int x, int y)
        {
            if (_result == null || x < 0 || x >= _width || y < 0 || y >= _height)
                return -1;
            return _result[x * _height + y];
        }

        /// <summary>
        /// Gets whether the cell at a position has been collapsed to a tile.
        /// </summary>
        public bool IsCollapsed(int x, int y)
        {
            if (_collapsed == null || x < 0 || x >= _width || y < 0 || y >= _height)
                return false;
            return _collapsed[x * _height + y];
        }

        /// <summary>
        /// Forces a specific tile at a position before solving.
        /// </summary>
        /// <returns>
        /// True when the cell now holds the tile. False when the request was refused -- the position or
        /// tile ID is unknown, the solver is not in progress, or earlier constraints have already ruled the
        /// tile out for that cell -- in which case the grid is left exactly as it was. Also false when the
        /// tile was placed but propagating it ran into a contradiction, in which case Status is Failed.
        /// </returns>
        public bool SetTile(int x, int y, int tileId)
        {
            if (_status != WFCStatus.InProgress || _result == null)
                return false;

            if (x < 0 || x >= _width || y < 0 || y >= _height)
                return false;

            int tileIndex = TileIdToIndex(tileId);
            if (tileIndex < 0)
                return false;

            int cell = x * _height + y;
            ulong tileBit = 1UL << tileIndex;

            // Forcing a tile the constraints have already excluded used to overwrite the cell anyway,
            // return false, and leave the solver InProgress on a grid that then completed with illegal
            // neighbours. Refusing keeps the grid consistent. A cell that is already collapsed is a repeat
            // of the same request, not a second collapse to count.
            if (_collapsed[cell])
                return _possibilities[cell] == tileBit;

            if ((_possibilities[cell] & tileBit) == 0)
                return false;

            _possibilities[cell] = tileBit;
            _result[cell] = _tiles[tileIndex].Id;
            _collapsed[cell] = true;
            _collapsedCount++;

            PushPropagate(cell);

            if (!Propagate())
            {
                _status = WFCStatus.Failed;
                return false;
            }

            return true;
        }

        #endregion

        #region Core Algorithm

        /// <summary>
        /// Runs one find / collapse / propagate iteration.
        /// </summary>
        /// <returns>True while the solve is still in progress.</returns>
        private bool Step()
        {
            int cell = FindMinEntropyCell();
            if (cell < 0)
            {
                // All cells collapsed
                _status = WFCStatus.Completed;
                return false;
            }

            if (!CollapseCell(cell) || !Propagate())
            {
                _status = WFCStatus.Failed;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Finds the cell with minimum entropy (fewest possibilities).
        /// </summary>
        /// <returns>The cell index, or -1 when every cell is collapsed.</returns>
        private int FindMinEntropyCell()
        {
            int minCell = -1;
            int minEntropy = int.MaxValue;
            float minNoise = float.MaxValue;

            int cellCount = _possibilities.Length;

            for (int cell = 0; cell < cellCount; cell++)
            {
                if (_collapsed[cell])
                    continue; // Already collapsed

                int entropy = CountBits(_possibilities[cell]);

                if (entropy == 0)
                {
                    // Contradiction found; CollapseCell reports it
                    return cell;
                }

                if (entropy < minEntropy ||
                    (entropy == minEntropy && _rng.NextFloat() < minNoise))
                {
                    minEntropy = entropy;
                    minNoise = _rng.NextFloat();
                    minCell = cell;
                }
            }

            return minCell;
        }

        /// <summary>
        /// Collapses a cell to a single tile based on weights.
        /// </summary>
        private bool CollapseCell(int cell)
        {
            ulong possible = _possibilities[cell];

            if (possible == 0)
                return false; // Contradiction

            // Calculate total weight of possible tiles. Weights of zero or less contribute nothing.
            float totalWeight = 0f;
            for (int i = 0; i < _tileCount; i++)
            {
                if ((possible & (1UL << i)) != 0)
                {
                    float weight = _tiles[i].Weight;
                    if (weight > 0f)
                        totalWeight += weight;
                }
            }

            int chosenIndex = -1;

            // Choose tile based on weight
            if (totalWeight > 0f)
            {
                float choice = _rng.NextFloat() * totalWeight;
                float accumulated = 0f;

                for (int i = 0; i < _tileCount; i++)
                {
                    if ((possible & (1UL << i)) != 0)
                    {
                        float weight = _tiles[i].Weight;
                        if (weight > 0f)
                        {
                            accumulated += weight;
                            if (choice < accumulated)
                            {
                                chosenIndex = i;
                                break;
                            }
                        }
                    }
                }
            }

            if (chosenIndex < 0)
            {
                // No weighted tile is left: take the last valid one. A cell whose only remaining tiles
                // weigh nothing used to be reported as a contradiction, failing a grid that had a legal
                // answer -- the constraints had forced exactly the tile a zero weight says not to volunteer.
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
            _possibilities[cell] = 1UL << chosenIndex;
            _result[cell] = _tiles[chosenIndex].Id;
            _collapsed[cell] = true;
            _collapsedCount++;

            PushPropagate(cell);
            return true;
        }

        /// <summary>
        /// Propagates constraints from collapsed cells to neighbors.
        /// </summary>
        /// <returns>False when a cell was left with no possible tile.</returns>
        private bool Propagate()
        {
            int width = _width;
            int height = _height;
            bool wrap = _wrapEdges;

            while (_propagateStackSize > 0)
            {
                _propagateStackSize--;
                int cell = _propagateStack[_propagateStackSize];
                _queued[cell] = false;

                ulong possible = _possibilities[cell];
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

                int x = cell / height;
                int y = cell - x * height;

                // Propagate to neighbors; an index of -1 means "off the edge of a grid that does not wrap"
                // North (+Y)
                int ny = y + 1;
                if (ny >= height)
                    ny = wrap ? 0 : -1;
                if (ny >= 0 && !ConstrainNeighbor(x * height + ny, validForNorth))
                    return false;

                // South (-Y)
                ny = y - 1;
                if (ny < 0)
                    ny = wrap ? height - 1 : -1;
                if (ny >= 0 && !ConstrainNeighbor(x * height + ny, validForSouth))
                    return false;

                // East (+X)
                int nx = x + 1;
                if (nx >= width)
                    nx = wrap ? 0 : -1;
                if (nx >= 0 && !ConstrainNeighbor(nx * height + y, validForEast))
                    return false;

                // West (-X)
                nx = x - 1;
                if (nx < 0)
                    nx = wrap ? width - 1 : -1;
                if (nx >= 0 && !ConstrainNeighbor(nx * height + y, validForWest))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Applies constraint to a neighbor cell.
        /// </summary>
        /// <returns>False when the constraint left the neighbor with no possible tile.</returns>
        private bool ConstrainNeighbor(int cell, ulong validMask)
        {
            ulong oldPossible = _possibilities[cell];
            ulong newPossible = oldPossible & validMask;

            if (newPossible == oldPossible)
                return true;

            _possibilities[cell] = newPossible;

            if (newPossible == 0)
                return false;

            PushPropagate(cell);
            return true;
        }

        /// <summary>
        /// Adds a cell to the propagation stack unless it is already waiting on it.
        /// </summary>
        private void PushPropagate(int cell)
        {
            if (_queued[cell])
                return;

            _queued[cell] = true;
            _propagateStack[_propagateStackSize] = cell;
            _propagateStackSize++;
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
            if (_possibilities == null || x < 0 || x >= _width || y < 0 || y >= _height)
                return 0;
            return CountBits(_possibilities[x * _height + y]);
        }

        /// <summary>
        /// Gets all possible tile IDs for a cell.
        /// </summary>
        public int[] GetPossibilitiesAt(int x, int y)
        {
            if (_possibilities == null || x < 0 || x >= _width || y < 0 || y >= _height)
                return new int[0];

            ulong possible = _possibilities[x * _height + y];
            int count = CountBits(possible);
            int[] result = new int[count];
            int idx = 0;

            for (int i = 0; i < _tileCount && idx < count; i++)
            {
                if ((possible & (1UL << i)) != 0)
                {
                    result[idx] = _tiles[i].Id;
                    idx++;
                }
            }

            return result;
        }

        #endregion
    }
}
