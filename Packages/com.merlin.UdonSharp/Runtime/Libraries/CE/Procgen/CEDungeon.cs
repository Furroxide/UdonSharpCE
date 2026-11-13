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
        /// The room's top-right corner (exclusive): <see cref="Position"/> plus <see cref="Size"/>.
        /// Together with Position this is the room's rectangle. It is a pair of Vector2Int rather than a
        /// RectInt because Udon does not expose RectInt, so a RectInt member could not be compiled.
        /// </summary>
        public Vector2Int Max => new Vector2Int(
            Position.x + Size.x,
            Position.y + Size.y
        );
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
        /// Bottom-left corner of the area containing all rooms and corridors.
        /// Held as a corner and a size rather than a RectInt, which Udon does not expose.
        /// </summary>
        public Vector2Int BoundsMin;

        /// <summary>
        /// Size of the area containing all rooms and corridors.
        /// </summary>
        public Vector2Int BoundsSize;

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
        /// Maximum generation bounds. Every room lies inside (0, 0) to this size; a room size larger than
        /// the bounds is shrunk to fit.
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
    ///
    /// Generation runs to completion inside one call; it is not time-sliced. Cost grows with the square of
    /// the room count, so expect a hitch on large layouts and generate them while nobody is watching.
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
        // Room-to-room tables in this file (connections, distances) are count * count arrays indexed
        // [from * count + to]. UdonSharp cannot compile a multidimensional array, so while these were
        // int[,] and float[,] no behaviour that called Generate could be built at all.

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
            int[] connections = BuildConnections(rng, rooms, config.Connectivity);

            // Step 3: Assign connected rooms to each room
            AssignConnectedRooms(rooms, connections);

            // Step 4: Generate corridors
            DungeonCorridor[] corridors = GenerateCorridors(rng, rooms, connections, config.CorridorWidth);

            // Step 5: Find critical path (longest path from Start to Boss)
            int[] criticalPath = FindCriticalPath(rooms, connections);
            MarkCriticalCorridors(corridors, criticalPath);

            // Step 6: Assign room types
            AssignRoomTypes(rng, rooms, criticalPath, config);

            DungeonLayout layout = new DungeonLayout();
            layout.Rooms = rooms;
            layout.Corridors = corridors;
            layout.CriticalPath = criticalPath;

            // Calculate bounds
            AssignBounds(layout);

            return layout;
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
            int boundsX = maxBounds.x;
            int boundsY = maxBounds.y;

            // No room fits in an empty or inverted area. Returning none lets the caller hand back an empty
            // layout instead of a room hanging outside bounds that cannot contain it.
            if (boundsX < 1 || boundsY < 1)
                return new DungeonRoom[0];

            int minWidth = minSize.x;
            int maxWidth = maxSize.x;
            int minHeight = minSize.y;
            int maxHeight = maxSize.y;

            DungeonRoom[] rooms = new DungeonRoom[targetCount];

            // Placed rectangles are mirrored into int arrays so the collision test below never has to read
            // them back out of the room objects, and a rejected candidate never allocates one.
            int[] placedX = new int[targetCount];
            int[] placedY = new int[targetCount];
            int[] placedWidth = new int[targetCount];
            int[] placedHeight = new int[targetCount];

            int placedCount = 0;
            int maxAttempts = targetCount * 50;
            int attempts = 0;

            while (placedCount < targetCount && attempts < maxAttempts)
            {
                attempts++;

                // Generate random room size
                int width = rng.RangeInclusive(minWidth, maxWidth);
                int height = rng.RangeInclusive(minHeight, maxHeight);

                // A configured size the bounds cannot hold used to be placed at the origin anyway and
                // overhang them (8x8 bounds produced an 11x9 room). Shrink it to fit, and never let a
                // zero or negative configured size through as a room with no area.
                if (width < 1) width = 1;
                if (height < 1) height = 1;
                if (width > boundsX) width = boundsX;
                if (height > boundsY) height = boundsY;

                // Generate random position
                int x = rng.RangeInclusive(0, boundsX - width);
                int y = rng.RangeInclusive(0, boundsY - height);

                // Check for collisions with existing rooms, each grown by the spacing on every side
                bool collides = false;
                for (int i = 0; i < placedCount; i++)
                {
                    if (x < placedX[i] + placedWidth[i] + spacing &&
                        x + width > placedX[i] - spacing &&
                        y < placedY[i] + placedHeight[i] + spacing &&
                        y + height > placedY[i] - spacing)
                    {
                        collides = true;
                        break;
                    }
                }

                if (!collides)
                {
                    DungeonRoom newRoom = new DungeonRoom();
                    newRoom.Id = placedCount;
                    newRoom.Position = new Vector2Int(x, y);
                    newRoom.Size = new Vector2Int(width, height);

                    rooms[placedCount] = newRoom;
                    placedX[placedCount] = x;
                    placedY[placedCount] = y;
                    placedWidth[placedCount] = width;
                    placedHeight[placedCount] = height;
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

        #endregion

        #region Graph Building

        /// <summary>
        /// Builds room connections using MST + extra edges.
        /// </summary>
        /// <returns>A count * count table; entry [a * count + b] is 1 when rooms a and b are connected.</returns>
        private static int[] BuildConnections(CERandom rng, DungeonRoom[] rooms, float connectivity)
        {
            int count = rooms.Length;
            int[] connections = new int[count * count];

            // Calculate distances between all room pairs
            float[] distances = new float[count * count];
            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    float dist = Vector2Int.Distance(rooms[i].Center, rooms[j].Center);
                    distances[i * count + j] = dist;
                    distances[j * count + i] = dist;
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
                    connections[u * count + parent[u]] = 1;
                    connections[parent[u] * count + u] = 1;
                }

                // Update distances
                for (int v = 0; v < count; v++)
                {
                    if (!inTree[v] && distances[u * count + v] < minDist[v])
                    {
                        minDist[v] = distances[u * count + v];
                        parent[v] = u;
                    }
                }
            }

            // Add extra connections based on connectivity
            int possibleExtras = (count * (count - 1)) / 2 - (count - 1);

            // The product is only cast for a connectivity strictly inside (0, 1). UdonSharp lowers the cast
            // to Convert.ToInt32, which throws on NaN or on a value outside int range, so a careless
            // connectivity would otherwise halt the calling behaviour instead of being clamped.
            int extraConnections;
            if (!(connectivity > 0f))
                extraConnections = 0;
            else if (connectivity >= 1f)
                extraConnections = possibleExtras;
            else
                extraConnections = (int)(possibleExtras * connectivity);

            // Collect non-connected pairs sorted by distance
            int pairCount = 0;
            int[] pairA = new int[possibleExtras];
            int[] pairB = new int[possibleExtras];
            float[] pairDistances = new float[possibleExtras];

            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    if (connections[i * count + j] == 0)
                    {
                        pairA[pairCount] = i;
                        pairB[pairCount] = j;
                        pairDistances[pairCount] = distances[i * count + j];
                        pairCount++;
                    }
                }
            }

            // Sort by distance with a bottom-up merge sort. It is stable -- a tie keeps the left run's
            // entry -- so it yields the same order a bubble sort does, and with it the same layouts. A
            // bubble sort here costs pairCount^2 / 2 comparisons; at the 100-room cap that is close to
            // twelve million iterations of an Udon loop inside one call, and Udon halts a behaviour whose
            // single call runs for about ten seconds.
            int[] mergedA = new int[pairCount];
            int[] mergedB = new int[pairCount];
            float[] mergedDistances = new float[pairCount];

            for (int run = 1; run < pairCount; run *= 2)
            {
                for (int start = 0; start < pairCount; start += run * 2)
                {
                    int mid = start + run;
                    if (mid > pairCount) mid = pairCount;

                    int end = start + run * 2;
                    if (end > pairCount) end = pairCount;

                    int left = start;
                    int right = mid;
                    int dest = start;

                    while (left < mid && right < end)
                    {
                        if (pairDistances[right] < pairDistances[left])
                        {
                            mergedDistances[dest] = pairDistances[right];
                            mergedA[dest] = pairA[right];
                            mergedB[dest] = pairB[right];
                            right++;
                        }
                        else
                        {
                            mergedDistances[dest] = pairDistances[left];
                            mergedA[dest] = pairA[left];
                            mergedB[dest] = pairB[left];
                            left++;
                        }

                        dest++;
                    }

                    while (left < mid)
                    {
                        mergedDistances[dest] = pairDistances[left];
                        mergedA[dest] = pairA[left];
                        mergedB[dest] = pairB[left];
                        left++;
                        dest++;
                    }

                    while (right < end)
                    {
                        mergedDistances[dest] = pairDistances[right];
                        mergedA[dest] = pairA[right];
                        mergedB[dest] = pairB[right];
                        right++;
                        dest++;
                    }
                }

                // The merged pass becomes the input of the next, wider one
                float[] swapDistances = pairDistances;
                pairDistances = mergedDistances;
                mergedDistances = swapDistances;

                int[] swapA = pairA;
                pairA = mergedA;
                mergedA = swapA;

                int[] swapB = pairB;
                pairB = mergedB;
                mergedB = swapB;
            }

            // Add extra connections (prefer shorter distances with some randomness)
            int added = 0;
            for (int i = 0; i < pairCount && added < extraConnections; i++)
            {
                // Higher chance for shorter distances
                float chance = 1f - (float)i / pairCount;
                if (rng.NextFloat() < chance * 0.5f + 0.5f)
                {
                    int a = pairA[i];
                    int b = pairB[i];
                    connections[a * count + b] = 1;
                    connections[b * count + a] = 1;
                    added++;
                }
            }

            return connections;
        }

        /// <summary>
        /// Assigns connected room indices to each room.
        /// </summary>
        private static void AssignConnectedRooms(DungeonRoom[] rooms, int[] connections)
        {
            int count = rooms.Length;

            for (int i = 0; i < count; i++)
            {
                // Count connections
                int connCount = 0;
                for (int j = 0; j < count; j++)
                {
                    if (connections[i * count + j] > 0)
                        connCount++;
                }

                // Assign connected rooms
                int[] connected = new int[connCount];
                int idx = 0;
                for (int j = 0; j < count; j++)
                {
                    if (connections[i * count + j] > 0)
                    {
                        connected[idx] = j;
                        idx++;
                    }
                }

                rooms[i].ConnectedRooms = connected;
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
            int[] connections,
            int width)
        {
            int count = rooms.Length;

            // Count corridors needed
            int corridorCount = 0;
            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    if (connections[i * count + j] > 0)
                        corridorCount++;
                }
            }

            DungeonCorridor[] corridors = new DungeonCorridor[corridorCount];
            int idx = 0;

            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    if (connections[i * count + j] > 0)
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

            int startX = start.x;
            int startY = start.y;
            int endX = end.x;
            int endY = end.y;

            // Choose horizontal-first or vertical-first randomly
            bool horizontalFirst = rng.NextBool();

            int cornerX = horizontalFirst ? endX : startX;
            int cornerY = horizontalFirst ? startY : endY;

            DungeonCorridor corridor = new DungeonCorridor();
            corridor.FromRoom = fromIdx;
            corridor.ToRoom = toIdx;
            corridor.Path = GeneratePathPoints(startX, startY, cornerX, cornerY, endX, endY);
            corridor.Width = width;

            return corridor;
        }

        /// <summary>
        /// Generates individual tiles along a path from start through corner to end, both ends included.
        /// Each leg is axis-aligned, so the tile count is exactly the two leg lengths plus the start tile.
        /// </summary>
        private static Vector2Int[] GeneratePathPoints(
            int startX,
            int startY,
            int cornerX,
            int cornerY,
            int endX,
            int endY)
        {
            int firstLeg = Mathf.Abs(cornerX - startX) + Mathf.Abs(cornerY - startY);
            int secondLeg = Mathf.Abs(endX - cornerX) + Mathf.Abs(endY - cornerY);

            Vector2Int[] path = new Vector2Int[firstLeg + secondLeg + 1];
            int idx = 0;

            int x = startX;
            int y = startY;

            path[idx] = new Vector2Int(x, y);
            idx++;

            // First leg: start to corner
            while (x != cornerX)
            {
                x += cornerX > x ? 1 : -1;
                path[idx] = new Vector2Int(x, y);
                idx++;
            }

            while (y != cornerY)
            {
                y += cornerY > y ? 1 : -1;
                path[idx] = new Vector2Int(x, y);
                idx++;
            }

            // Second leg: corner to end
            while (x != endX)
            {
                x += endX > x ? 1 : -1;
                path[idx] = new Vector2Int(x, y);
                idx++;
            }

            while (y != endY)
            {
                y += endY > y ? 1 : -1;
                path[idx] = new Vector2Int(x, y);
                idx++;
            }

            return path;
        }

        #endregion

        #region Critical Path

        /// <summary>
        /// Finds the longest path through the dungeon (critical path).
        /// </summary>
        private static int[] FindCriticalPath(DungeonRoom[] rooms, int[] connections)
        {
            int count = rooms.Length;
            if (count < 2)
            {
                return count == 1 ? new int[] { 0 } : new int[0];
            }

            // BFS to find farthest room from room 0 (start candidate)
            int startRoom = FindFarthestRoom(0, count, connections);

            // BFS to find farthest room from start (boss candidate)
            int bossRoom = FindFarthestRoom(startRoom, count, connections);

            // Find path from start to boss using BFS
            int[] path = FindPath(startRoom, bossRoom, count, connections);

            // Mark rooms on critical path
            for (int i = 0; i < path.Length; i++)
            {
                rooms[path[i]].IsOnCriticalPath = true;
            }

            return path;
        }

        /// <summary>
        /// Flags the corridors that join consecutive rooms of the critical path.
        /// </summary>
        private static void MarkCriticalCorridors(DungeonCorridor[] corridors, int[] criticalPath)
        {
            // Nothing else ever wrote DungeonCorridor.IsOnCriticalPath, so it read false for every corridor
            // of every layout and a caller styling the main route by it styled nothing.
            for (int c = 0; c < corridors.Length; c++)
            {
                DungeonCorridor corridor = corridors[c];
                int fromRoom = corridor.FromRoom;
                int toRoom = corridor.ToRoom;

                for (int step = 0; step < criticalPath.Length - 1; step++)
                {
                    int a = criticalPath[step];
                    int b = criticalPath[step + 1];

                    if ((a == fromRoom && b == toRoom) || (a == toRoom && b == fromRoom))
                    {
                        corridor.IsOnCriticalPath = true;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Finds the farthest room from a starting room using BFS.
        /// </summary>
        private static int FindFarthestRoom(int start, int count, int[] connections)
        {
            int[] distances = new int[count];
            bool[] visited = new bool[count];

            for (int i = 0; i < count; i++)
                distances[i] = -1;

            // Simple BFS using array as queue
            int[] queue = new int[count];
            int queueStart = 0, queueEnd = 0;

            queue[queueEnd] = start;
            queueEnd++;
            visited[start] = true;
            distances[start] = 0;

            int farthest = start;
            int maxDist = 0;

            while (queueStart < queueEnd)
            {
                int current = queue[queueStart];
                queueStart++;

                for (int next = 0; next < count; next++)
                {
                    if (connections[current * count + next] > 0 && !visited[next])
                    {
                        visited[next] = true;
                        distances[next] = distances[current] + 1;
                        queue[queueEnd] = next;
                        queueEnd++;

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
        private static int[] FindPath(int from, int to, int roomCount, int[] connections)
        {
            if (from == to)
                return new int[] { from };

            int[] parent = new int[roomCount];
            bool[] visited = new bool[roomCount];

            for (int i = 0; i < roomCount; i++)
                parent[i] = -1;

            int[] queue = new int[roomCount];
            int queueStart = 0, queueEnd = 0;

            queue[queueEnd] = from;
            queueEnd++;
            visited[from] = true;

            while (queueStart < queueEnd)
            {
                int current = queue[queueStart];
                queueStart++;

                if (current == to)
                    break;

                for (int next = 0; next < roomCount; next++)
                {
                    if (connections[current * roomCount + next] > 0 && !visited[next])
                    {
                        visited[next] = true;
                        parent[next] = current;
                        queue[queueEnd] = next;
                        queueEnd++;
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
        /// Calculates the area encompassing all rooms and corridors and stores it on the layout.
        /// </summary>
        private static void AssignBounds(DungeonLayout layout)
        {
            DungeonRoom[] rooms = layout.Rooms;
            DungeonCorridor[] corridors = layout.Corridors;

            if (rooms.Length == 0)
            {
                layout.BoundsMin = new Vector2Int(0, 0);
                layout.BoundsSize = new Vector2Int(0, 0);
                return;
            }

            int minX = int.MaxValue, minY = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue;

            // Include rooms
            for (int i = 0; i < rooms.Length; i++)
            {
                DungeonRoom room = rooms[i];
                int roomX = room.Position.x;
                int roomY = room.Position.y;
                int roomMaxX = roomX + room.Size.x;
                int roomMaxY = roomY + room.Size.y;

                if (roomX < minX) minX = roomX;
                if (roomY < minY) minY = roomY;
                if (roomMaxX > maxX) maxX = roomMaxX;
                if (roomMaxY > maxY) maxY = roomMaxY;
            }

            // Include corridors
            for (int i = 0; i < corridors.Length; i++)
            {
                DungeonCorridor corridor = corridors[i];
                Vector2Int[] path = corridor.Path;
                int corridorWidth = corridor.Width;

                for (int j = 0; j < path.Length; j++)
                {
                    int pointX = path[j].x;
                    int pointY = path[j].y;

                    if (pointX < minX) minX = pointX;
                    if (pointY < minY) minY = pointY;
                    if (pointX + corridorWidth > maxX) maxX = pointX + corridorWidth;
                    if (pointY + corridorWidth > maxY) maxY = pointY + corridorWidth;
                }
            }

            layout.BoundsMin = new Vector2Int(minX, minY);
            layout.BoundsSize = new Vector2Int(maxX - minX, maxY - minY);
        }

        /// <summary>
        /// Creates a minimal layout for edge cases.
        /// </summary>
        private static DungeonLayout CreateMinimalLayout(DungeonRoom[] rooms)
        {
            DungeonLayout layout = new DungeonLayout();
            layout.Rooms = rooms;
            layout.Corridors = new DungeonCorridor[0];

            if (rooms.Length > 0)
            {
                // A lone room is the whole critical path, so it carries the flag the path's rooms carry
                // in a full layout.
                rooms[0].Type = RoomType.Start;
                rooms[0].ConnectedRooms = new int[0];
                rooms[0].IsOnCriticalPath = true;
                layout.CriticalPath = new int[] { 0 };
            }
            else
            {
                layout.CriticalPath = new int[0];
            }

            AssignBounds(layout);

            return layout;
        }

        #endregion
    }
}
