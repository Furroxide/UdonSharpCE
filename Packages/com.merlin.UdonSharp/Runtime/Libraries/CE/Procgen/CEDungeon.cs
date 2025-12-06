using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.Procgen
{
    /// <summary>
    /// Types of rooms in a dungeon layout.
    /// </summary>
    [PublicAPI]
    public enum RoomType
    {
        /// <summary>
        /// Starting room where players spawn.
        /// </summary>
        Start,

        /// <summary>
        /// Normal room with standard encounters.
        /// </summary>
        Normal,

        /// <summary>
        /// Treasure room with rewards.
        /// </summary>
        Treasure,

        /// <summary>
        /// Boss room at end of critical path.
        /// </summary>
        Boss,

        /// <summary>
        /// Secret room off the main path.
        /// </summary>
        Secret
    }

    /// <summary>
    /// Represents a room in the dungeon layout.
    /// </summary>
    [PublicAPI]
    public class DungeonRoom
    {
        /// <summary>
        /// Unique identifier for the room.
        /// </summary>
        public int Id;

        /// <summary>
        /// Position of the room's bottom-left corner.
        /// </summary>
        public Vector2Int Position;

        /// <summary>
        /// Size of the room in tiles.
        /// </summary>
        public Vector2Int Size;

        /// <summary>
        /// Type of the room.
        /// </summary>
        public RoomType Type;

        /// <summary>
        /// Indices of connected rooms.
        /// </summary>
        public int[] ConnectedRooms;

        /// <summary>
        /// Whether this room is on the critical path.
        /// </summary>
        public bool IsOnCriticalPath;

        /// <summary>
        /// Center position of the room.
        /// </summary>
        public Vector2Int Center => new Vector2Int(
            Position.x + Size.x / 2,
            Position.y + Size.y / 2
        );

        /// <summary>
        /// The room's bounding rectangle.
        /// </summary>
        public RectInt Bounds => new RectInt(Position, Size);
    }

    /// <summary>
    /// Represents a corridor connecting two rooms.
    /// </summary>
    [PublicAPI]
    public class DungeonCorridor
    {
        /// <summary>
        /// Index of the source room.
        /// </summary>
        public int FromRoom;

        /// <summary>
        /// Index of the destination room.
        /// </summary>
        public int ToRoom;

        /// <summary>
        /// Tiles making up the corridor path.
        /// </summary>
        public Vector2Int[] Path;

        /// <summary>
        /// Width of the corridor in tiles.
        /// </summary>
        public int Width;

        /// <summary>
        /// Whether this corridor is on the critical path.
        /// </summary>
        public bool IsOnCriticalPath;
    }

    /// <summary>
    /// Complete dungeon layout result.
    /// </summary>
    [PublicAPI]
    public class DungeonLayout
    {
        /// <summary>
        /// All rooms in the dungeon.
        /// </summary>
        public DungeonRoom[] Rooms;

        /// <summary>
        /// All corridors connecting rooms.
        /// </summary>
        public DungeonCorridor[] Corridors;

        /// <summary>
        /// Room indices forming the critical path from Start to Boss.
        /// </summary>
        public int[] CriticalPath;

        /// <summary>
        /// The bounds containing all rooms and corridors.
        /// </summary>
        public RectInt Bounds;

        /// <summary>
        /// Gets the room with Start type.
        /// </summary>
        public DungeonRoom StartRoom
        {
            get
            {
                for (int i = 0; i < Rooms.Length; i++)
                {
                    if (Rooms[i].Type == RoomType.Start)
                        return Rooms[i];
                }
                return null;
            }
        }

        /// <summary>
        /// Gets the room with Boss type.
        /// </summary>
        public DungeonRoom BossRoom
        {
            get
            {
                for (int i = 0; i < Rooms.Length; i++)
                {
                    if (Rooms[i].Type == RoomType.Boss)
                        return Rooms[i];
                }
                return null;
            }
        }
    }

    /// <summary>
    /// Configuration for dungeon generation.
    /// </summary>
    [PublicAPI]
    public class DungeonConfig
    {
        /// <summary>
        /// Target number of rooms (may be less if space is limited).
        /// </summary>
        public int RoomCount = 10;

        /// <summary>
        /// Minimum room size.
        /// </summary>
        public Vector2Int MinRoomSize = new Vector2Int(5, 5);

        /// <summary>
        /// Maximum room size.
        /// </summary>
        public Vector2Int MaxRoomSize = new Vector2Int(12, 12);

        /// <summary>
        /// Extra connections beyond minimum spanning tree (0-1).
        /// </summary>
        public float Connectivity = 0.3f;

        /// <summary>
        /// Corridor width in tiles.
        /// </summary>
        public int CorridorWidth = 2;

        /// <summary>
        /// Minimum distance between rooms.
        /// </summary>
        public int RoomSpacing = 2;

        /// <summary>
        /// Probability of treasure rooms (0-1).
        /// </summary>
        public float TreasureRoomChance = 0.15f;

        /// <summary>
        /// Probability of secret rooms (0-1).
        /// </summary>
        public float SecretRoomChance = 0.1f;

        /// <summary>
        /// Maximum generation bounds.
        /// </summary>
        public Vector2Int MaxBounds = new Vector2Int(100, 100);
    }

    /// <summary>
    /// Graph-based dungeon generator for procedural level creation.
    ///
    /// Uses deterministic random number generation for synchronized
    /// dungeon layouts across all clients.
    /// </summary>
    /// <remarks>
    /// Generation algorithm:
    /// 1. Place rooms using random placement with collision avoidance
    /// 2. Build minimum spanning tree using Prim's algorithm
    /// 3. Add extra connections based on connectivity parameter
    /// 4. Generate L-shaped corridors between connected rooms
    /// 5. Assign room types along critical path
    /// </remarks>
    /// <example>
    /// <code>
    /// // Generate dungeon with same seed on all clients
    /// var rng = new CERandom(worldSeed);
    /// var config = new DungeonConfig
    /// {
    ///     RoomCount = 15,
    ///     MinRoomSize = new Vector2Int(6, 6),
    ///     MaxRoomSize = new Vector2Int(14, 14),
    ///     Connectivity = 0.4f
    /// };
    ///
    /// var layout = CEDungeon.Generate(rng, config);
    ///
    /// // Spawn rooms
    /// foreach (var room in layout.Rooms)
    /// {
    ///     SpawnRoom(room.Position, room.Size, room.Type);
    /// }
    ///
    /// // Spawn corridors
    /// foreach (var corridor in layout.Corridors)
    /// {
    ///     SpawnCorridor(corridor.Path, corridor.Width);
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    public static class CEDungeon
    {
        #region Main Generation

        /// <summary>
        /// Generates a dungeon layout using the specified configuration.
        /// </summary>
        /// <param name="rng">Deterministic random generator.</param>
        /// <param name="config">Generation configuration.</param>
        /// <returns>Complete dungeon layout.</returns>
        public static DungeonLayout Generate(CERandom rng, DungeonConfig config)
        {
            if (rng == null || config == null)
                return null;

            // Clamp configuration values
            int roomCount = Mathf.Clamp(config.RoomCount, 2, 100);
            Vector2Int minSize = config.MinRoomSize;
            Vector2Int maxSize = config.MaxRoomSize;

            // Step 1: Place rooms
            DungeonRoom[] rooms = PlaceRooms(rng, roomCount, minSize, maxSize,
                config.RoomSpacing, config.MaxBounds);

            if (rooms.Length < 2)
            {
                // Not enough rooms - return minimal layout
                return CreateMinimalLayout(rooms);
            }

            // Step 2: Build graph connections (MST + extra)
            int[,] connections = BuildConnections(rng, rooms, config.Connectivity);

            // Step 3: Assign connected rooms to each room
            AssignConnectedRooms(rooms, connections);

            // Step 4: Generate corridors
            DungeonCorridor[] corridors = GenerateCorridors(rng, rooms, connections, config.CorridorWidth);

            // Step 5: Find critical path (longest path from Start to Boss)
            int[] criticalPath = FindCriticalPath(rooms, connections);

            // Step 6: Assign room types
            AssignRoomTypes(rng, rooms, criticalPath, config);

            // Calculate bounds
            RectInt bounds = CalculateBounds(rooms, corridors);

            return new DungeonLayout
            {
                Rooms = rooms,
                Corridors = corridors,
                CriticalPath = criticalPath,
                Bounds = bounds
            };
        }

        /// <summary>
        /// Generates a dungeon with simple parameters.
        /// </summary>
        public static DungeonLayout Generate(
            CERandom rng,
            int roomCount,
            Vector2Int minRoomSize,
            Vector2Int maxRoomSize,
            float connectivity = 0.3f)
        {
            return Generate(rng, new DungeonConfig
            {
                RoomCount = roomCount,
                MinRoomSize = minRoomSize,
                MaxRoomSize = maxRoomSize,
                Connectivity = connectivity
            });
        }

        #endregion

        #region Room Placement

        /// <summary>
        /// Places rooms using random placement with collision avoidance.
        /// </summary>
        private static DungeonRoom[] PlaceRooms(
            CERandom rng,
            int targetCount,
            Vector2Int minSize,
            Vector2Int maxSize,
            int spacing,
            Vector2Int maxBounds)
        {
            DungeonRoom[] rooms = new DungeonRoom[targetCount];
            int placedCount = 0;
            int maxAttempts = targetCount * 50;
            int attempts = 0;

            while (placedCount < targetCount && attempts < maxAttempts)
            {
                attempts++;

                // Generate random room size
                int width = rng.Range(minSize.x, maxSize.x + 1);
                int height = rng.Range(minSize.y, maxSize.y + 1);

                // Generate random position
                int x = rng.Range(0, maxBounds.x - width);
                int y = rng.Range(0, maxBounds.y - height);

                var newRoom = new DungeonRoom
                {
                    Id = placedCount,
                    Position = new Vector2Int(x, y),
                    Size = new Vector2Int(width, height)
                };

                // Check for collisions with existing rooms
                bool collides = false;
                for (int i = 0; i < placedCount; i++)
                {
                    if (RoomsOverlap(rooms[i], newRoom, spacing))
                    {
                        collides = true;
                        break;
                    }
                }

                if (!collides)
                {
                    rooms[placedCount] = newRoom;
                    placedCount++;
                }
            }

            // Trim array if we couldn't place all rooms
            if (placedCount < targetCount)
            {
                DungeonRoom[] trimmed = new DungeonRoom[placedCount];
                for (int i = 0; i < placedCount; i++)
                {
                    trimmed[i] = rooms[i];
                }
                return trimmed;
            }

            return rooms;
        }

        /// <summary>
        /// Checks if two rooms overlap (including spacing).
        /// </summary>
        private static bool RoomsOverlap(DungeonRoom a, DungeonRoom b, int spacing)
        {
            RectInt boundsA = new RectInt(
                a.Position.x - spacing,
                a.Position.y - spacing,
                a.Size.x + spacing * 2,
                a.Size.y + spacing * 2
            );

            RectInt boundsB = new RectInt(b.Position, b.Size);

            return boundsA.Overlaps(boundsB);
        }

        #endregion

        #region Graph Building

        /// <summary>
        /// Builds room connections using MST + extra edges.
        /// </summary>
        private static int[,] BuildConnections(CERandom rng, DungeonRoom[] rooms, float connectivity)
        {
            int count = rooms.Length;
            int[,] connections = new int[count, count];

            // Calculate distances between all room pairs
            float[,] distances = new float[count, count];
            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    float dist = Vector2Int.Distance(rooms[i].Center, rooms[j].Center);
                    distances[i, j] = dist;
                    distances[j, i] = dist;
                }
            }

            // Build MST using Prim's algorithm
            bool[] inTree = new bool[count];
            float[] minDist = new float[count];
            int[] parent = new int[count];

            for (int i = 0; i < count; i++)
            {
                minDist[i] = float.MaxValue;
                parent[i] = -1;
            }

            minDist[0] = 0;

            for (int iter = 0; iter < count; iter++)
            {
                // Find minimum distance vertex not in tree
                float minVal = float.MaxValue;
                int u = -1;
                for (int v = 0; v < count; v++)
                {
                    if (!inTree[v] && minDist[v] < minVal)
                    {
                        minVal = minDist[v];
                        u = v;
                    }
                }

                if (u == -1) break;

                inTree[u] = true;

                // Add edge to MST
                if (parent[u] >= 0)
                {
                    connections[u, parent[u]] = 1;
                    connections[parent[u], u] = 1;
                }

                // Update distances
                for (int v = 0; v < count; v++)
                {
                    if (!inTree[v] && distances[u, v] < minDist[v])
                    {
                        minDist[v] = distances[u, v];
                        parent[v] = u;
                    }
                }
            }

            // Add extra connections based on connectivity
            int possibleExtras = (count * (count - 1)) / 2 - (count - 1);
            int extraConnections = (int)(possibleExtras * connectivity);

            // Collect non-connected pairs sorted by distance
            int pairCount = 0;
            int[][] pairs = new int[possibleExtras][];
            float[] pairDistances = new float[possibleExtras];

            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    if (connections[i, j] == 0)
                    {
                        pairs[pairCount] = new int[] { i, j };
                        pairDistances[pairCount] = distances[i, j];
                        pairCount++;
                    }
                }
            }

            // Sort by distance (simple bubble sort for small arrays)
            for (int i = 0; i < pairCount - 1; i++)
            {
                for (int j = 0; j < pairCount - i - 1; j++)
                {
                    if (pairDistances[j] > pairDistances[j + 1])
                    {
                        float tempDist = pairDistances[j];
                        pairDistances[j] = pairDistances[j + 1];
                        pairDistances[j + 1] = tempDist;

                        int[] tempPair = pairs[j];
                        pairs[j] = pairs[j + 1];
                        pairs[j + 1] = tempPair;
                    }
                }
            }

            // Add extra connections (prefer shorter distances with some randomness)
            int added = 0;
            for (int i = 0; i < pairCount && added < extraConnections; i++)
            {
                // Higher chance for shorter distances
                float chance = 1f - (float)i / pairCount;
                if (rng.NextFloat() < chance * 0.5f + 0.5f)
                {
                    int a = pairs[i][0];
                    int b = pairs[i][1];
                    connections[a, b] = 1;
                    connections[b, a] = 1;
                    added++;
                }
            }

            return connections;
        }

        /// <summary>
        /// Assigns connected room indices to each room.
        /// </summary>
        private static void AssignConnectedRooms(DungeonRoom[] rooms, int[,] connections)
        {
            int count = rooms.Length;

            for (int i = 0; i < count; i++)
            {
                // Count connections
                int connCount = 0;
                for (int j = 0; j < count; j++)
                {
                    if (connections[i, j] > 0)
                        connCount++;
                }

                // Assign connected rooms
                rooms[i].ConnectedRooms = new int[connCount];
                int idx = 0;
                for (int j = 0; j < count; j++)
                {
                    if (connections[i, j] > 0)
                    {
                        rooms[i].ConnectedRooms[idx++] = j;
                    }
                }
            }
        }

        #endregion

        #region Corridor Generation

        /// <summary>
        /// Generates corridors between connected rooms.
        /// </summary>
        private static DungeonCorridor[] GenerateCorridors(
            CERandom rng,
            DungeonRoom[] rooms,
            int[,] connections,
            int width)
        {
            int count = rooms.Length;

            // Count corridors needed
            int corridorCount = 0;
            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    if (connections[i, j] > 0)
                        corridorCount++;
                }
            }

            DungeonCorridor[] corridors = new DungeonCorridor[corridorCount];
            int idx = 0;

            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    if (connections[i, j] > 0)
                    {
                        corridors[idx] = GenerateLShapedCorridor(
                            rng, rooms[i], rooms[j], i, j, width);
                        idx++;
                    }
                }
            }

            return corridors;
        }

        /// <summary>
        /// Generates an L-shaped corridor between two rooms.
        /// </summary>
        private static DungeonCorridor GenerateLShapedCorridor(
            CERandom rng,
            DungeonRoom from,
            DungeonRoom to,
            int fromIdx,
            int toIdx,
            int width)
        {
            Vector2Int start = from.Center;
            Vector2Int end = to.Center;

            // Choose horizontal-first or vertical-first randomly
            bool horizontalFirst = rng.NextBool();

            Vector2Int corner;
            if (horizontalFirst)
            {
                corner = new Vector2Int(end.x, start.y);
            }
            else
            {
                corner = new Vector2Int(start.x, end.y);
            }

            // Generate path points
            Vector2Int[] path = GeneratePathPoints(start, corner, end);

            return new DungeonCorridor
            {
                FromRoom = fromIdx,
                ToRoom = toIdx,
                Path = path,
                Width = width
            };
        }

        /// <summary>
        /// Generates individual tiles along a path from start through corner to end.
        /// </summary>
        private static Vector2Int[] GeneratePathPoints(Vector2Int start, Vector2Int corner, Vector2Int end)
        {
            // Calculate segment lengths
            int seg1Length = Mathf.Abs(corner.x - start.x) + Mathf.Abs(corner.y - start.y) + 1;
            int seg2Length = Mathf.Abs(end.x - corner.x) + Mathf.Abs(end.y - corner.y);

            Vector2Int[] path = new Vector2Int[seg1Length + seg2Length];
            int idx = 0;

            // First segment: start to corner
            Vector2Int current = start;
            Vector2Int dir1 = new Vector2Int(
                corner.x > start.x ? 1 : (corner.x < start.x ? -1 : 0),
                corner.y > start.y ? 1 : (corner.y < start.y ? -1 : 0)
            );

            while (current != corner)
            {
                path[idx++] = current;
                if (current.x != corner.x)
                    current.x += dir1.x;
                else if (current.y != corner.y)
                    current.y += dir1.y;
            }
            path[idx++] = corner;

            // Second segment: corner to end
            Vector2Int dir2 = new Vector2Int(
                end.x > corner.x ? 1 : (end.x < corner.x ? -1 : 0),
                end.y > corner.y ? 1 : (end.y < corner.y ? -1 : 0)
            );

            current = corner;
            while (current != end)
            {
                if (current.x != end.x)
                    current.x += dir2.x;
                else if (current.y != end.y)
                    current.y += dir2.y;

                if (idx < path.Length)
                    path[idx++] = current;
            }

            // Trim if needed
            if (idx < path.Length)
            {
                Vector2Int[] trimmed = new Vector2Int[idx];
                for (int i = 0; i < idx; i++)
                    trimmed[i] = path[i];
                return trimmed;
            }

            return path;
        }

        #endregion

        #region Critical Path

        /// <summary>
        /// Finds the longest path through the dungeon (critical path).
        /// </summary>
        private static int[] FindCriticalPath(DungeonRoom[] rooms, int[,] connections)
        {
            int count = rooms.Length;
            if (count < 2)
            {
                return count == 1 ? new int[] { 0 } : new int[0];
            }

            // BFS to find farthest room from room 0 (start candidate)
            int startRoom = FindFarthestRoom(0, rooms, connections);

            // BFS to find farthest room from start (boss candidate)
            int bossRoom = FindFarthestRoom(startRoom, rooms, connections);

            // Find path from start to boss using BFS
            int[] path = FindPath(startRoom, bossRoom, rooms.Length, connections);

            // Mark rooms on critical path
            for (int i = 0; i < path.Length; i++)
            {
                rooms[path[i]].IsOnCriticalPath = true;
            }

            return path;
        }

        /// <summary>
        /// Finds the farthest room from a starting room using BFS.
        /// </summary>
        private static int FindFarthestRoom(int start, DungeonRoom[] rooms, int[,] connections)
        {
            int count = rooms.Length;
            int[] distances = new int[count];
            bool[] visited = new bool[count];

            for (int i = 0; i < count; i++)
                distances[i] = -1;

            // Simple BFS using array as queue
            int[] queue = new int[count];
            int queueStart = 0, queueEnd = 0;

            queue[queueEnd++] = start;
            visited[start] = true;
            distances[start] = 0;

            int farthest = start;
            int maxDist = 0;

            while (queueStart < queueEnd)
            {
                int current = queue[queueStart++];

                for (int next = 0; next < count; next++)
                {
                    if (connections[current, next] > 0 && !visited[next])
                    {
                        visited[next] = true;
                        distances[next] = distances[current] + 1;
                        queue[queueEnd++] = next;

                        if (distances[next] > maxDist)
                        {
                            maxDist = distances[next];
                            farthest = next;
                        }
                    }
                }
            }

            return farthest;
        }

        /// <summary>
        /// Finds path between two rooms using BFS.
        /// </summary>
        private static int[] FindPath(int from, int to, int roomCount, int[,] connections)
        {
            if (from == to)
                return new int[] { from };

            int[] parent = new int[roomCount];
            bool[] visited = new bool[roomCount];

            for (int i = 0; i < roomCount; i++)
                parent[i] = -1;

            int[] queue = new int[roomCount];
            int queueStart = 0, queueEnd = 0;

            queue[queueEnd++] = from;
            visited[from] = true;

            while (queueStart < queueEnd)
            {
                int current = queue[queueStart++];

                if (current == to)
                    break;

                for (int next = 0; next < roomCount; next++)
                {
                    if (connections[current, next] > 0 && !visited[next])
                    {
                        visited[next] = true;
                        parent[next] = current;
                        queue[queueEnd++] = next;
                    }
                }
            }

            // Reconstruct path
            if (!visited[to])
                return new int[] { from }; // No path found

            // Count path length
            int pathLength = 0;
            int node = to;
            while (node != -1)
            {
                pathLength++;
                node = parent[node];
            }

            // Build path array
            int[] path = new int[pathLength];
            node = to;
            for (int i = pathLength - 1; i >= 0; i--)
            {
                path[i] = node;
                node = parent[node];
            }

            return path;
        }

        #endregion

        #region Room Types

        /// <summary>
        /// Assigns types to rooms based on their position in the dungeon.
        /// </summary>
        private static void AssignRoomTypes(
            CERandom rng,
            DungeonRoom[] rooms,
            int[] criticalPath,
            DungeonConfig config)
        {
            // Set all to Normal first
            for (int i = 0; i < rooms.Length; i++)
            {
                rooms[i].Type = RoomType.Normal;
            }

            if (criticalPath.Length == 0)
                return;

            // First room on critical path is Start
            rooms[criticalPath[0]].Type = RoomType.Start;

            // Last room on critical path is Boss
            if (criticalPath.Length > 1)
            {
                rooms[criticalPath[criticalPath.Length - 1]].Type = RoomType.Boss;
            }

            // Assign special rooms to non-critical-path rooms
            for (int i = 0; i < rooms.Length; i++)
            {
                if (rooms[i].Type != RoomType.Normal)
                    continue;

                if (!rooms[i].IsOnCriticalPath)
                {
                    // Off-path rooms can be Secret
                    if (rng.NextFloat() < config.SecretRoomChance)
                    {
                        rooms[i].Type = RoomType.Secret;
                        continue;
                    }
                }

                // Any room can be Treasure
                if (rng.NextFloat() < config.TreasureRoomChance)
                {
                    rooms[i].Type = RoomType.Treasure;
                }
            }
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Calculates bounds encompassing all rooms and corridors.
        /// </summary>
        private static RectInt CalculateBounds(DungeonRoom[] rooms, DungeonCorridor[] corridors)
        {
            if (rooms.Length == 0)
                return new RectInt(0, 0, 0, 0);

            int minX = int.MaxValue, minY = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue;

            // Include rooms
            for (int i = 0; i < rooms.Length; i++)
            {
                var room = rooms[i];
                if (room.Position.x < minX) minX = room.Position.x;
                if (room.Position.y < minY) minY = room.Position.y;
                if (room.Position.x + room.Size.x > maxX) maxX = room.Position.x + room.Size.x;
                if (room.Position.y + room.Size.y > maxY) maxY = room.Position.y + room.Size.y;
            }

            // Include corridors
            for (int i = 0; i < corridors.Length; i++)
            {
                var corridor = corridors[i];
                for (int j = 0; j < corridor.Path.Length; j++)
                {
                    var point = corridor.Path[j];
                    if (point.x < minX) minX = point.x;
                    if (point.y < minY) minY = point.y;
                    if (point.x + corridor.Width > maxX) maxX = point.x + corridor.Width;
                    if (point.y + corridor.Width > maxY) maxY = point.y + corridor.Width;
                }
            }

            return new RectInt(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>
        /// Creates a minimal layout for edge cases.
        /// </summary>
        private static DungeonLayout CreateMinimalLayout(DungeonRoom[] rooms)
        {
            if (rooms.Length > 0)
            {
                rooms[0].Type = RoomType.Start;
                rooms[0].ConnectedRooms = new int[0];
            }

            return new DungeonLayout
            {
                Rooms = rooms,
                Corridors = new DungeonCorridor[0],
                CriticalPath = rooms.Length > 0 ? new int[] { 0 } : new int[0],
                Bounds = rooms.Length > 0 ? rooms[0].Bounds : new RectInt()
            };
        }

        #endregion
    }
}
