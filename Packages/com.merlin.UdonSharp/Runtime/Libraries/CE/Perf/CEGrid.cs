using System;
using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.Perf
{
    /// <summary>
    /// Grid-based spatial partitioning for efficient proximity queries.
    ///
    /// Divides 3D space into a uniform grid of cells, enabling O(1) lookups
    /// for nearby entities instead of O(n) brute-force comparisons.
    /// </summary>
    /// <remarks>
    /// Essential for high-entity-count scenarios:
    /// - Collision detection between many objects
    /// - Nearest-neighbor queries
    /// - Line-of-sight checks
    /// - Area-of-effect abilities
    ///
    /// The grid uses a fixed cell size. Choose based on:
    /// - Typical query radius (cell size ≈ query radius)
    /// - Entity density (more dense = smaller cells)
    /// - Memory constraints (cells * maxEntitiesPerCell * sizeof(int))
    /// </remarks>
    /// <example>
    /// <code>
    /// public class ProximitySystem : UdonSharpBehaviour
    /// {
    ///     private CEGrid grid;
    ///     private Vector3[] positions;
    ///
    ///     void Start()
    ///     {
    ///         // Create grid: 100x10x100 cells, 2 unit cell size
    ///         grid = new CEGrid(
    ///             new Vector3(-100, -5, -100),  // min bounds
    ///             new Vector3(100, 5, 100),     // max bounds
    ///             2.0f,                          // cell size
    ///             32                             // max entities per cell
    ///         );
    ///     }
    ///
    ///     void Update()
    ///     {
    ///         // Clear and rebuild grid each frame
    ///         grid.Clear();
    ///         for (int i = 0; i < entityCount; i++)
    ///         {
    ///             grid.Insert(i, positions[i]);
    ///         }
    ///
    ///         // Query for nearby entities
    ///         int[] nearby = new int[32];
    ///         int count = grid.QueryRadius(playerPos, 5.0f, nearby);
    ///     }
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    public class CEGrid
    {
        #region Constants

        /// <summary>
        /// Ceiling applied to a query bound, in cells, before it is converted to an int. Far beyond any
        /// grid that can be allocated and comfortably inside the range of int.
        /// </summary>
        private const float QueryCellLimit = 1073741824f;

        #endregion

        #region Fields

        /// <summary>
        /// Minimum world bounds.
        /// </summary>
        private readonly Vector3 _boundsMin;

        /// <summary>
        /// Maximum world bounds.
        /// </summary>
        private readonly Vector3 _boundsMax;

        /// <summary>
        /// Size of each grid cell in world units.
        /// </summary>
        private readonly float _cellSize;

        /// <summary>
        /// Inverse cell size for faster position-to-cell conversion.
        /// </summary>
        private readonly float _invCellSize;

        /// <summary>
        /// Number of cells in X dimension.
        /// </summary>
        private readonly int _cellsX;

        /// <summary>
        /// Number of cells in Y dimension.
        /// </summary>
        private readonly int _cellsY;

        /// <summary>
        /// Number of cells in Z dimension.
        /// </summary>
        private readonly int _cellsZ;

        /// <summary>
        /// Total number of cells.
        /// </summary>
        private readonly int _totalCells;

        /// <summary>
        /// Maximum entities per cell.
        /// </summary>
        private readonly int _maxPerCell;

        /// <summary>
        /// Entity IDs stored in each cell (flattened 2D array).
        /// [cellIndex * _maxPerCell + entitySlot]
        /// </summary>
        private readonly int[] _cellEntities;

        /// <summary>
        /// Number of entities in each cell.
        /// </summary>
        private readonly int[] _cellCounts;

        /// <summary>
        /// Total entities currently in the grid.
        /// </summary>
        private int _totalEntityCount;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the minimum world bounds.
        /// </summary>
        public Vector3 BoundsMin => _boundsMin;

        /// <summary>
        /// Gets the maximum world bounds.
        /// </summary>
        public Vector3 BoundsMax => _boundsMax;

        /// <summary>
        /// Gets the cell size.
        /// </summary>
        public float CellSize => _cellSize;

        /// <summary>
        /// Gets the grid dimensions.
        /// </summary>
        public Vector3Int Dimensions => new Vector3Int(_cellsX, _cellsY, _cellsZ);

        /// <summary>
        /// Gets the total cell count.
        /// </summary>
        public int TotalCells => _totalCells;

        /// <summary>
        /// Gets the maximum entities per cell.
        /// </summary>
        public int MaxPerCell => _maxPerCell;

        /// <summary>
        /// Gets the total entity count.
        /// </summary>
        public int EntityCount => _totalEntityCount;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new spatial partitioning grid.
        /// </summary>
        /// <param name="boundsMin">Minimum world bounds.</param>
        /// <param name="boundsMax">Maximum world bounds.</param>
        /// <param name="cellSize">Size of each cell in world units.</param>
        /// <param name="maxPerCell">Maximum entities per cell. Default is 32.</param>
        public CEGrid(Vector3 boundsMin, Vector3 boundsMax, float cellSize, int maxPerCell = 32)
        {
            if (cellSize <= 0) cellSize = 1.0f;
            if (maxPerCell < 1) maxPerCell = 1;
            if (maxPerCell > 256) maxPerCell = 256;

            _boundsMin = boundsMin;
            _boundsMax = boundsMax;
            _cellSize = cellSize;
            _invCellSize = 1.0f / cellSize;
            _maxPerCell = maxPerCell;

            // Calculate grid dimensions
            Vector3 size = boundsMax - boundsMin;
            _cellsX = Mathf.Max(1, Mathf.CeilToInt(size.x * _invCellSize));
            _cellsY = Mathf.Max(1, Mathf.CeilToInt(size.y * _invCellSize));
            _cellsZ = Mathf.Max(1, Mathf.CeilToInt(size.z * _invCellSize));

            _totalCells = _cellsX * _cellsY * _cellsZ;

            // Limit total cells to prevent excessive memory use
            if (_totalCells > 1000000)
            {
                Debug.LogWarning($"[CE.Perf] CEGrid: Cell count {_totalCells} is very large, consider larger cell size");
            }

            // Allocate storage
            _cellEntities = new int[_totalCells * maxPerCell];
            _cellCounts = new int[_totalCells];
            _totalEntityCount = 0;

            // Initialize entity slots to -1 (invalid)
            for (int i = 0; i < _cellEntities.Length; i++)
            {
                _cellEntities[i] = -1;
            }
        }

        #endregion

        #region Position to Cell

        /// <summary>
        /// Converts a world position to a cell index.
        /// </summary>
        /// <param name="position">World position.</param>
        /// <returns>Cell index, or -1 if out of bounds.</returns>
        /// <remarks>
        /// Both faces of the bounds are inside the grid. A coordinate exactly on the max face floors to
        /// one past the last cell whenever the extent is a whole number of cells, and it used to be
        /// rejected for that -- which dropped precisely the entities a caller had clamped to its arena
        /// bounds (Mathf.Clamp(pos, min, max) lands on max exactly) out of every query, while the same
        /// clamp on the min side worked. Such a coordinate belongs to the last cell.
        /// </remarks>
        public int PositionToCellIndex(Vector3 position)
        {
            int x = PositionToCellX(position.x);
            int y = PositionToCellY(position.y);
            int z = PositionToCellZ(position.z);

            // The max-face test sits inside the out-of-range branch so that in-range positions, which
            // are nearly all of them, pay nothing for it.
            if (x < 0 || x >= _cellsX)
            {
                if (x != _cellsX || position.x > _boundsMax.x) return -1;
                x = _cellsX - 1;
            }

            if (y < 0 || y >= _cellsY)
            {
                if (y != _cellsY || position.y > _boundsMax.y) return -1;
                y = _cellsY - 1;
            }

            if (z < 0 || z >= _cellsZ)
            {
                if (z != _cellsZ || position.z > _boundsMax.z) return -1;
                z = _cellsZ - 1;
            }

            return CellCoordsToIndex(x, y, z);
        }

        /// <summary>
        /// Converts a world X position to a cell X coordinate.
        /// </summary>
        private int PositionToCellX(float x)
        {
            return Mathf.FloorToInt((x - _boundsMin.x) * _invCellSize);
        }

        /// <summary>
        /// Converts a world Y position to a cell Y coordinate.
        /// </summary>
        private int PositionToCellY(float y)
        {
            return Mathf.FloorToInt((y - _boundsMin.y) * _invCellSize);
        }

        /// <summary>
        /// Converts a world Z position to a cell Z coordinate.
        /// </summary>
        private int PositionToCellZ(float z)
        {
            return Mathf.FloorToInt((z - _boundsMin.z) * _invCellSize);
        }

        /// <summary>
        /// Converts a world X coordinate to a cell X coordinate for a query range, saturating in float
        /// space before the conversion. Anything below the grid comes back as -1.
        /// </summary>
        /// <remarks>
        /// Query extents are caller input, and "everything, however far away" is a reasonable thing to
        /// ask for. Converting a float that large to int is not a saturating operation: the result is
        /// unspecified, and on x64 it is int.MinValue. As the upper end of a range that value then won
        /// the Mathf.Min against the last cell and the cell loops ran zero times, so a query with an
        /// infinite radius returned nothing at all instead of everything.
        /// </remarks>
        private int QueryCellX(float x)
        {
            return Mathf.FloorToInt(Mathf.Clamp((x - _boundsMin.x) * _invCellSize, -1f, QueryCellLimit));
        }

        /// <summary>
        /// Y counterpart of <see cref="QueryCellX"/>.
        /// </summary>
        private int QueryCellY(float y)
        {
            return Mathf.FloorToInt(Mathf.Clamp((y - _boundsMin.y) * _invCellSize, -1f, QueryCellLimit));
        }

        /// <summary>
        /// Z counterpart of <see cref="QueryCellX"/>.
        /// </summary>
        private int QueryCellZ(float z)
        {
            return Mathf.FloorToInt(Mathf.Clamp((z - _boundsMin.z) * _invCellSize, -1f, QueryCellLimit));
        }

        /// <summary>
        /// Converts cell coordinates to a flat cell index.
        /// </summary>
        private int CellCoordsToIndex(int x, int y, int z)
        {
            return x + y * _cellsX + z * _cellsX * _cellsY;
        }

        /// <summary>
        /// Converts a cell index to cell coordinates.
        /// </summary>
        /// <param name="cellIndex">The cell index.</param>
        /// <param name="x">Output X coordinate.</param>
        /// <param name="y">Output Y coordinate.</param>
        /// <param name="z">Output Z coordinate.</param>
        public void CellIndexToCoords(int cellIndex, out int x, out int y, out int z)
        {
            int xyPlane = _cellsX * _cellsY;
            z = cellIndex / xyPlane;
            int remainder = cellIndex % xyPlane;
            y = remainder / _cellsX;
            x = remainder % _cellsX;
        }

        /// <summary>
        /// Gets the world-space center of a cell.
        /// </summary>
        /// <param name="cellIndex">The cell index.</param>
        /// <returns>Center position in world space.</returns>
        public Vector3 GetCellCenter(int cellIndex)
        {
            CellIndexToCoords(cellIndex, out int x, out int y, out int z);
            return new Vector3(
                _boundsMin.x + (x + 0.5f) * _cellSize,
                _boundsMin.y + (y + 0.5f) * _cellSize,
                _boundsMin.z + (z + 0.5f) * _cellSize
            );
        }

        #endregion

        #region Insert / Remove / Clear

        /// <summary>
        /// Inserts an entity into the grid at the specified position.
        /// </summary>
        /// <param name="entityId">The entity ID to insert.</param>
        /// <param name="position">World position of the entity.</param>
        /// <returns>True if inserted successfully.</returns>
        public bool Insert(int entityId, Vector3 position)
        {
            int cellIndex = PositionToCellIndex(position);
            if (cellIndex < 0) return false;

            int count = _cellCounts[cellIndex];
            if (count >= _maxPerCell)
            {
                // Cell is full
                return false;
            }

            int baseIndex = cellIndex * _maxPerCell;
            _cellEntities[baseIndex + count] = entityId;
            _cellCounts[cellIndex] = count + 1;
            _totalEntityCount++;

            return true;
        }

        /// <summary>
        /// Removes an entity from a specific cell.
        /// </summary>
        /// <param name="entityId">The entity ID to remove.</param>
        /// <param name="cellIndex">The cell to remove from.</param>
        /// <returns>True if the entity was found and removed.</returns>
        public bool RemoveFromCell(int entityId, int cellIndex)
        {
            if (cellIndex < 0 || cellIndex >= _totalCells) return false;

            int count = _cellCounts[cellIndex];
            int baseIndex = cellIndex * _maxPerCell;

            for (int i = 0; i < count; i++)
            {
                if (_cellEntities[baseIndex + i] == entityId)
                {
                    // Swap with last and decrement count
                    _cellEntities[baseIndex + i] = _cellEntities[baseIndex + count - 1];
                    _cellEntities[baseIndex + count - 1] = -1;
                    _cellCounts[cellIndex] = count - 1;
                    _totalEntityCount--;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Removes an entity from the grid by searching all cells.
        /// More expensive than RemoveFromCell - use when cell is unknown.
        /// </summary>
        /// <param name="entityId">The entity ID to remove.</param>
        /// <returns>True if the entity was found and removed.</returns>
        public bool Remove(int entityId)
        {
            for (int cellIndex = 0; cellIndex < _totalCells; cellIndex++)
            {
                if (RemoveFromCell(entityId, cellIndex))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Clears all entities from the grid.
        /// </summary>
        public void Clear()
        {
            for (int i = 0; i < _totalCells; i++)
            {
                _cellCounts[i] = 0;
            }
            _totalEntityCount = 0;
        }

        #endregion

        #region Queries

        /// <summary>
        /// Queries all entities within a radius of a position.
        /// </summary>
        /// <param name="center">Center of the query.</param>
        /// <param name="radius">Search radius.</param>
        /// <param name="result">Array to fill with entity IDs.</param>
        /// <returns>Number of entities found.</returns>
        /// <remarks>
        /// This returns all entities in cells that intersect the query sphere.
        /// For exact distance filtering, check distances after the query.
        /// </remarks>
        public int QueryRadius(Vector3 center, float radius, int[] result)
        {
            if (result == null) return 0;

            // Calculate cell range to check
            float lowX = center.x - radius;
            float lowY = center.y - radius;
            float lowZ = center.z - radius;

            int minCellX = Mathf.Max(0, QueryCellX(lowX));
            int maxCellX = Mathf.Min(_cellsX - 1, QueryCellX(center.x + radius));
            int minCellY = Mathf.Max(0, QueryCellY(lowY));
            int maxCellY = Mathf.Min(_cellsY - 1, QueryCellY(center.y + radius));
            int minCellZ = Mathf.Max(0, QueryCellZ(lowZ));
            int maxCellZ = Mathf.Min(_cellsZ - 1, QueryCellZ(center.z + radius));

            // A range that begins exactly on the max face still covers the entities stored on it,
            // which PositionToCellIndex files under the last cell.
            if (minCellX == _cellsX && lowX <= _boundsMax.x) minCellX = _cellsX - 1;
            if (minCellY == _cellsY && lowY <= _boundsMax.y) minCellY = _cellsY - 1;
            if (minCellZ == _cellsZ && lowZ <= _boundsMax.z) minCellZ = _cellsZ - 1;

            int resultCount = 0;
            int maxResults = result.Length;

            // Iterate over all cells in range
            for (int z = minCellZ; z <= maxCellZ; z++)
            {
                for (int y = minCellY; y <= maxCellY; y++)
                {
                    for (int x = minCellX; x <= maxCellX; x++)
                    {
                        int cellIndex = CellCoordsToIndex(x, y, z);
                        int count = _cellCounts[cellIndex];
                        int baseIndex = cellIndex * _maxPerCell;

                        for (int i = 0; i < count && resultCount < maxResults; i++)
                        {
                            result[resultCount] = _cellEntities[baseIndex + i];
                            resultCount++;
                        }
                    }
                }
            }

            return resultCount;
        }

        /// <summary>
        /// Queries all entities within an axis-aligned bounding box.
        /// </summary>
        /// <param name="min">Minimum corner of the box.</param>
        /// <param name="max">Maximum corner of the box.</param>
        /// <param name="result">Array to fill with entity IDs.</param>
        /// <returns>Number of entities found.</returns>
        public int QueryBox(Vector3 min, Vector3 max, int[] result)
        {
            if (result == null) return 0;

            int minCellX = Mathf.Max(0, QueryCellX(min.x));
            int maxCellX = Mathf.Min(_cellsX - 1, QueryCellX(max.x));
            int minCellY = Mathf.Max(0, QueryCellY(min.y));
            int maxCellY = Mathf.Min(_cellsY - 1, QueryCellY(max.y));
            int minCellZ = Mathf.Max(0, QueryCellZ(min.z));
            int maxCellZ = Mathf.Min(_cellsZ - 1, QueryCellZ(max.z));

            // A box whose min corner sits exactly on the max face still covers the entities stored on
            // it, which PositionToCellIndex files under the last cell.
            if (minCellX == _cellsX && min.x <= _boundsMax.x) minCellX = _cellsX - 1;
            if (minCellY == _cellsY && min.y <= _boundsMax.y) minCellY = _cellsY - 1;
            if (minCellZ == _cellsZ && min.z <= _boundsMax.z) minCellZ = _cellsZ - 1;

            int resultCount = 0;
            int maxResults = result.Length;

            for (int z = minCellZ; z <= maxCellZ; z++)
            {
                for (int y = minCellY; y <= maxCellY; y++)
                {
                    for (int x = minCellX; x <= maxCellX; x++)
                    {
                        int cellIndex = CellCoordsToIndex(x, y, z);
                        int count = _cellCounts[cellIndex];
                        int baseIndex = cellIndex * _maxPerCell;

                        for (int i = 0; i < count && resultCount < maxResults; i++)
                        {
                            result[resultCount] = _cellEntities[baseIndex + i];
                            resultCount++;
                        }
                    }
                }
            }

            return resultCount;
        }

        /// <summary>
        /// Gets all entities in a specific cell.
        /// </summary>
        /// <param name="cellIndex">The cell index.</param>
        /// <param name="result">Array to fill with entity IDs.</param>
        /// <returns>Number of entities in the cell.</returns>
        public int GetCellEntities(int cellIndex, int[] result)
        {
            if (result == null) return 0;
            if (cellIndex < 0 || cellIndex >= _totalCells) return 0;

            int count = _cellCounts[cellIndex];
            int baseIndex = cellIndex * _maxPerCell;
            int copyCount = Mathf.Min(count, result.Length);

            for (int i = 0; i < copyCount; i++)
            {
                result[i] = _cellEntities[baseIndex + i];
            }

            return copyCount;
        }

        /// <summary>
        /// Gets the number of entities in a specific cell.
        /// </summary>
        /// <param name="cellIndex">The cell index.</param>
        /// <returns>Number of entities in the cell.</returns>
        public int GetCellCount(int cellIndex)
        {
            if (cellIndex < 0 || cellIndex >= _totalCells) return 0;
            return _cellCounts[cellIndex];
        }

        /// <summary>
        /// Checks if a cell is empty.
        /// </summary>
        /// <param name="cellIndex">The cell index.</param>
        /// <returns>True if the cell is empty.</returns>
        public bool IsCellEmpty(int cellIndex)
        {
            if (cellIndex < 0 || cellIndex >= _totalCells) return true;
            return _cellCounts[cellIndex] == 0;
        }

        #endregion

        #region Iteration Helpers

        /// <summary>
        /// Iterates over all non-empty cells.
        /// </summary>
        /// <remarks>
        /// Takes a delegate, so it is for editor and plain C# callers; Udon cannot represent one. From an
        /// UdonSharpBehaviour, loop over <c>0..TotalCells</c> and test <see cref="GetCellCount"/>.
        /// </remarks>
        /// <param name="action">Action called with (cellIndex, entityCount).</param>
        public void ForEachNonEmptyCell(Action<int, int> action)
        {
            if (action == null) return;

            for (int i = 0; i < _totalCells; i++)
            {
                int count = _cellCounts[i];
                if (count > 0)
                {
                    action(i, count);
                }
            }
        }

        /// <summary>
        /// Counts non-empty cells.
        /// </summary>
        /// <returns>Number of cells with at least one entity.</returns>
        public int CountNonEmptyCells()
        {
            int count = 0;
            for (int i = 0; i < _totalCells; i++)
            {
                if (_cellCounts[i] > 0)
                {
                    count++;
                }
            }
            return count;
        }

        #endregion

        #region Update Helper

        /// <summary>
        /// Updates an entity's position in the grid.
        /// More efficient than Remove + Insert when the old position is known.
        /// </summary>
        /// <param name="entityId">The entity ID.</param>
        /// <param name="oldPosition">Previous position.</param>
        /// <param name="newPosition">New position.</param>
        /// <returns>True if the update was successful.</returns>
        public bool UpdatePosition(int entityId, Vector3 oldPosition, Vector3 newPosition)
        {
            int oldCell = PositionToCellIndex(oldPosition);
            int newCell = PositionToCellIndex(newPosition);

            // Same cell, no update needed
            if (oldCell == newCell)
            {
                return true;
            }

            // Remove from old cell
            if (oldCell >= 0)
            {
                RemoveFromCell(entityId, oldCell);
            }

            // Insert into new cell
            if (newCell >= 0)
            {
                return Insert(entityId, newPosition);
            }

            return false;
        }

        #endregion
    }
}
