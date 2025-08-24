using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public class AStarManager : Singleton<AStarManager>
{
    [Header("Grid Settings")]
    [SerializeField] private int gridWidth = 200;
    [SerializeField] private int gridHeight = 200;
    [SerializeField] private float cellSize = 0.5f;
    [SerializeField] private LayerMask obstacleLayer;
    
    [Header("Performance Settings")]
    [SerializeField] private int batchSize = 64;
    [SerializeField] private int maxPathLength = 128;
    [SerializeField] private float pathUpdateInterval = 0.2f; 
    [SerializeField] private int maxActivePathsPerAgent = 1; // 이건 그냥 나두자
    
    [Header("Debug")]
    [SerializeField] private bool showDebugGizmos = false;
    
    private NativeArray<PathNode> grid;
    private Queue<PathRequest> requestQueue;
    private Dictionary<int, PathData> activePaths;
    private Dictionary<int, float> lastRequestTime;
    private NativeArray<PathRequest> batchRequests;
    private NativeArray<PathResult> batchResults;
    private NativeArray<int2> pathBuffer;
    private JobHandle currentJobHandle;
    private float lastBatchTime;
    private int requestIdCounter = 1;


    private bool batchUpdate;

    private class PathData
    {
        public int requestId;
        public List<Vector3> path;
        public Action<List<Vector3>> callback;
        public float requestTime;
        public bool isProcessing;
    }

    bool isInitialized = false;

    public void Awake()
    {
        base.Awake();
        Initialize();
        isInitialized = true;
    }
    
    
    private void Initialize()
    {
        isInitialized = true;
        requestQueue = new Queue<PathRequest>();
        activePaths = new Dictionary<int, PathData>();
        lastRequestTime = new Dictionary<int, float>();
        
        grid = new NativeArray<PathNode>(gridWidth * gridHeight, Allocator.Persistent);
        batchRequests = new NativeArray<PathRequest>(batchSize, Allocator.Persistent);
        batchResults = new NativeArray<PathResult>(batchSize, Allocator.Persistent);
        pathBuffer = new NativeArray<int2>(batchSize * maxPathLength, Allocator.Persistent);

        batchUpdate = false;
        obstacleLayer = 1 << LayerMask.NameToLayer("Obstacle");
            
        GenerateGrid();
    }

    private void GenerateGrid()
    {
        for (int i = 0; i < grid.Length; i++)
        {
            int x = i % gridWidth;
            int y = i / gridWidth;
            
            grid[i] = new PathNode
            {
                position = new int2(x, y),
                gCost = 0,
                hCost = 0,
                fCost = 0,
                parentIndex = -1,
                isWalkable = true,
                heapIndex = -1
            };
        }
        
        Vector2 halfCellSize = Vector2.one * cellSize * 0.5f;
        
        for (int x = 0; x < gridWidth; x++)
        {
            for (int y = 0; y < gridHeight; y++)
            {
                Vector2 worldPos = new Vector2(x * cellSize + cellSize * 0.5f, y * cellSize + cellSize * 0.5f);
                bool walkable = !Physics2D.OverlapBox(worldPos, halfCellSize, 0f, obstacleLayer);
                
                int index = x + y * gridWidth;
                PathNode node = grid[index];
                node.isWalkable = walkable;
                grid[index] = node;
            }
        }
    }

    public void RequestPath(Vector3 start, Vector3 goal, int agentId, Action<List<Vector3>> callback) {
        
        if (lastRequestTime.TryGetValue(agentId, out float lastTime))
        {
            if (Time.time - lastTime < pathUpdateInterval * 0.5f)
                return;
        }
        lastRequestTime[agentId] = Time.time;
        
        if (activePaths.TryGetValue(agentId, out PathData existingPath))
        {
            if (existingPath.isProcessing)
                return;
        }
        // Debug.Log("check");
        int2 startGrid = WorldToGrid(start);
        int2 goalGrid = WorldToGrid(goal);
        
        startGrid = FindNearestWalkablePosition(startGrid);
        goalGrid = FindNearestWalkablePosition(goalGrid);
        
        PathRequest request = new PathRequest
        {
            requestId = requestIdCounter++,
            agentId = agentId,
            start = startGrid,
            goal = goalGrid
        };

        activePaths[agentId] = new PathData
        {
            requestId = request.requestId,
            callback = callback,
            requestTime = Time.time,
            isProcessing = true,
            path = new List<Vector3>()
        };
        // Debug.Log(agentId);
        requestQueue.Enqueue(request);
    }

    private int2 FindNearestWalkablePosition(int2 pos)
    {
        if (IsValidPosition(pos) && IsWalkable(pos))
            return pos;
        
        for (int radius = 1; radius < 10; radius++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                for (int y = -radius; y <= radius; y++)
                {
                    if (math.abs(x) != radius && math.abs(y) != radius)
                        continue;
                        
                    int2 checkPos = pos + new int2(x, y);
                    if (IsValidPosition(checkPos) && IsWalkable(checkPos))
                        return checkPos;
                }
            }
        }
        
        return pos;
    }

    private bool IsWalkable(int2 pos)
    {
        int index = pos.x + pos.y * gridWidth;
        return grid[index].isWalkable;
    }

    private void Update()
    {
        if(!isInitialized) return;
        CleanupOldPaths();
        
        if (Time.time - lastBatchTime > 0.016f && (requestQueue.Count > 0 || batchUpdate)) {
            
            ProcessBatch();
            lastBatchTime = Time.time;
        }
    }

    private void CleanupOldPaths()
    {
        List<int> toRemove = new List<int>();
        float currentTime = Time.time;

        if (activePaths == null) return;
        
        
        foreach (var kvp in activePaths)
        {
            if (currentTime - kvp.Value.requestTime > 5f)
            {
                toRemove.Add(kvp.Key);
            }
        }
        
        foreach (int agentId in toRemove)
        {

            activePaths.Remove(agentId);
            lastRequestTime.Remove(agentId);
        }
    }

    private void ProcessBatch()
    {
        currentJobHandle.Complete();
        ProcessResults();

        int count = math.min(batchSize, requestQueue.Count);
        if (count == 0) return;

        batchUpdate = false;
        for (int i = 0; i < batchSize; i++)
        {
            if (i < count) {
                batchRequests[i] = requestQueue.Dequeue();
                batchUpdate = true;
            }
            else
                batchRequests[i] = default; 
        }
        
        AStar job = new AStar
        {
            requests = batchRequests,
            grid = grid,
            gridWidth = gridWidth,
            gridHeight = gridHeight,
            maxPathLength = maxPathLength,
            results = batchResults,
            pathBuffer = pathBuffer
        };

        currentJobHandle = job.Schedule(count, math.max(1, count / 4));
    }

    private void ProcessResults()
    {
        for (int i = 0; i < batchSize; i++)
        {
            PathResult result = batchResults[i];
            // Debug.Log(result.agentId+" " + result.requestId);
            if (result.agentId == 0 || result.requestId == 0)
                continue;

            if (activePaths.TryGetValue(result.agentId, out PathData pathData))
            {
                if (pathData.requestId != result.requestId)
                    continue;
                    
                List<Vector3> path = new List<Vector3>();
                
                if (result.success && result.pathLength > 0)
                {
                    List<int2> gridPath = new List<int2>();
                    
                    for (int j = 0; j < result.pathLength; j++)
                    {
                        gridPath.Add(pathBuffer[result.pathStartIndex + j]);
                    }
                    
                    List<int2> smoothedPath = SmoothPath(gridPath);
                    foreach (int2 gridPos in smoothedPath)
                    {
                        path.Add(GridToWorld(gridPos));
                    }
                }

                pathData.path = path;
                pathData.isProcessing = false;
                pathData.callback?.Invoke(path);
            }
            
            batchResults[i] = default;
        }
    }

    private List<int2> SmoothPath(List<int2> path)
    {
        if (path.Count <= 2)
            return path;
        
        List<int2> smoothed = new List<int2>();
        smoothed.Add(path[0]);
        
        int currentIndex = 0;
        while (currentIndex < path.Count - 1)
        {
            int farthestVisible = currentIndex + 1;
            
            for (int i = currentIndex + 2; i < path.Count; i++)
            {
                if (HasLineOfSight(path[currentIndex], path[i]))
                {
                    farthestVisible = i;
                }
                else
                {
                    break;
                }
            }
            
            smoothed.Add(path[farthestVisible]);
            currentIndex = farthestVisible;
        }
        
        return smoothed;
    }

    private bool HasLineOfSight(int2 start, int2 end)
    {
        int x0 = start.x;
        int y0 = start.y;
        int x1 = end.x;
        int y1 = end.y;
        
        int dx = math.abs(x1 - x0);
        int dy = math.abs(y1 - y0);
        int x = x0;
        int y = y0;
        int n = 1 + dx + dy;
        int x_inc = (x1 > x0) ? 1 : -1;
        int y_inc = (y1 > y0) ? 1 : -1;
        int error = dx - dy;
        dx *= 2;
        dy *= 2;
        
        for (; n > 0; --n)
        {
            if (!IsValidPosition(new int2(x, y)) || !IsWalkable(new int2(x, y)))
                return false;
            
            if (error > 0)
            {
                x += x_inc;
                error -= dy;
            }
            else
            {
                y += y_inc;
                error += dx;
            }
        }
        
        return true;
    }

    private int2 WorldToGrid(Vector3 worldPos)
    {
        return new int2(
            Mathf.FloorToInt(worldPos.x / cellSize),
            Mathf.FloorToInt(worldPos.y / cellSize)
        );
    }

    private Vector3 GridToWorld(int2 gridPos)
    {
        return new Vector3(
            gridPos.x * cellSize + cellSize * 0.5f,
            gridPos.y * cellSize + cellSize * 0.5f,
            0
        );
    }

    private bool IsValidPosition(int2 pos)
    {
        return pos.x >= 0 && pos.x < gridWidth && pos.y >= 0 && pos.y < gridHeight;
    }

    public Vector3 GetRandomWalkablePosition(Vector3 center, float radius, int maxAttempts = 30)
    {
        int2 centerGrid = WorldToGrid(center);
        int gridRadius = Mathf.CeilToInt(radius / cellSize);
   
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            
            int x = UnityEngine.Random.Range(-gridRadius, gridRadius + 1);
            int y = UnityEngine.Random.Range(-gridRadius, gridRadius + 1);
            int2 randomPos = centerGrid + new int2(x, y);
       
            
            float distance = math.distance(centerGrid, randomPos) * cellSize;
            if (distance > radius)
                continue;
            
            if (IsValidPosition(randomPos) && IsWalkable(randomPos))
                return GridToWorld(randomPos);
            
        }
        return center;
    }
    public void UpdateObstacle(Vector3 worldPos, float radius, bool isWalkable)
    {
        int2 gridPos = WorldToGrid(worldPos);
        int gridRadius = Mathf.CeilToInt(radius / cellSize);
        
        for (int x = -gridRadius; x <= gridRadius; x++)
        {
            for (int y = -gridRadius; y <= gridRadius; y++)
            {
                int2 checkPos = gridPos + new int2(x, y);
                if (!IsValidPosition(checkPos))
                    continue;
                    
                float distance = math.distance(gridPos, checkPos);
                if (distance <= gridRadius)
                {
                    int index = checkPos.x + checkPos.y * gridWidth;
                    PathNode node = grid[index];
                    node.isWalkable = isWalkable;
                    grid[index] = node;
                }
            }
        }
    }

    protected override void OnDestroy()
    {
        currentJobHandle.Complete();
        
        if (grid.IsCreated) grid.Dispose();
        if (batchRequests.IsCreated) batchRequests.Dispose();
        if (batchResults.IsCreated) batchResults.Dispose();
        if (pathBuffer.IsCreated) pathBuffer.Dispose();
        
        base.OnDestroy();
    }

    private void OnDrawGizmos()
    {
        if (!showDebugGizmos || !grid.IsCreated)
            return;
            
        for (int x = 0; x < gridWidth; x++)
        {
            for (int y = 0; y < gridHeight; y++)
            {
                int index = x + y * gridWidth;
                if (!grid[index].isWalkable)
                {
                    Gizmos.color = Color.red;
                    Vector3 worldPos = GridToWorld(new int2(x, y));
                    Gizmos.DrawCube(worldPos, Vector3.one * cellSize * 0.9f);
                }
            }
        }
    }


}