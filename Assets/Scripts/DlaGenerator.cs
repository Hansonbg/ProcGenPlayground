using System;
using UnityEngine;
using UnityEngine.UI;
using Random = UnityEngine.Random;
using UnityEngine.Rendering;


[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class DlaGenerator : MonoBehaviour
{
    [Header("UI Display Targets")]
    public RawImage dlaDisplayTarget;
    public RawImage heightMapDisplayTarget;
    
    [Header("Map size / particles")] [Range(4, 4096)]
    public int size = 1024;


    [Header("DLA Growth Settings")]
    [Range(0.01f, 1f)]
    public float particleDensity = 0.15f;
    public bool progressiveLoosening = true;
    [Range(0f, 1f)]
    public float loosenFactor = 0.85f;
    [Range(10, 50000)]
    public int maxSteps = 500;

    [Header("Height settings")]
    public float heightScale = 80f;   // overall vertical exaggeration

    [Header("Randomness")] 
    public int seed = 12345;

    public bool useRandomSeed;

    //---------------------------------------------------------//
    
    Texture2D  _preview;     // optional preview texture
    float[,]   _heightMap;   // blurred DLA -> floats
    bool[,]    _dla;
    private int _filledCount;
    private int _lastSize;
    private float _lastDensity;
    private float _lastLoosen;
    private int _lastMaxSteps;
    private bool _lastProgressiveLoosening;
    private int _lastSeed;
    private bool _lastUseRandomSeed;
    private float _lastHeightScale;
    private RenderTexture _rtA, _rtB;
    [SerializeField] private Material blurMat;
    
    void Start()
    {
        InitializeSeed();
    }
    
    void OnValidate()
    {
        size = Mathf.ClosestPowerOfTwo(size);

        if (Application.isPlaying && size != _lastSize || Mathf.Abs(particleDensity - _lastDensity) > 0.001f ||
            Mathf.Abs(loosenFactor - _lastLoosen) > 0.001f || maxSteps != _lastMaxSteps || seed != _lastSeed ||
            useRandomSeed != _lastUseRandomSeed || progressiveLoosening != _lastProgressiveLoosening ||
            Mathf.Abs(heightScale - _lastHeightScale) > 0.001f)
        {
            _lastSize = size;
            _lastDensity = particleDensity;
            _lastLoosen = loosenFactor;
            _lastMaxSteps = maxSteps;
            _lastSeed = seed;
            _lastUseRandomSeed = useRandomSeed;
            _lastProgressiveLoosening = progressiveLoosening;
            _lastHeightScale = heightScale;
           
            InitializeSeed();
            GenerateDla();
            BuildMesh();
            UpdateDisplay();
        }
    }

    private void UpdateDisplay()
    {
        if (dlaDisplayTarget != null)
        {
            dlaDisplayTarget.texture = PreviewDla(_dla);
        }
    }
    private void InitializeSeed()
    {
        if (useRandomSeed)
            seed = Random.Range(-100000, 100000);
        
        Random.InitState(seed);
    }
    
    /* ---------------- generation ---------------- */
    
    float[,] InitHeightMapFromBool(bool[,] dla)
    {
        int dims = dla.GetLength(0);
        float[,] result = new float[dims, dims];

        for (int y = 0; y < dims; y++)
        for (int x = 0; x < dims; x ++)
            result[x, y] = dla[x, y] ? 1f : 0f;
        
        return result;
    }
    
    float[,] UpscaleFloatMap(float[,] heightMap, int newSize)
    {
        int oldSize = heightMap.GetLength(0);
        float[,] result = new float[newSize, newSize];

        // Lerp the input dla into a float map of equal size.
        for (int y = 0; y < newSize; y++)
        for (int x = 0; x < newSize; x++)
        {
           // Map for sub-pixel (floating point) precision in the original
           float gx = x / 2f;
           float gy = y / 2f;
           
           // Find the integer representation of that sub-pixel precision
           // (top left corner)
           int ix = Mathf.FloorToInt(gx);
           int iy = Mathf.FloorToInt(gy);

           // Get the floating point delta between sub-pixel precision and its integer 
           // equivalent
           float tx = gx - ix;
           float ty = gy - iy;
           
           // Clamped right and bottom neighbors in integer space
           int ix1 = Mathf.Min(ix + 1, oldSize - 1);
           int iy1 = Mathf.Min(iy + 1, oldSize - 1);
          
           // Find float values for the nearest neighbor vertices
           // of the upscaled points in the old
           float v00 = heightMap[ix, iy];
           float v10 = heightMap[ix1, iy];
           float v01 = heightMap[ix, iy1];
           float v11 = heightMap[ix1, iy1];
           
           // Lerp across the top and bottom x rows
           float lerpX1 = Mathf.Lerp(v00, v10, tx);
           float lerpX2 = Mathf.Lerp(v01, v11, tx);
           
           // Assign the Lerp of those previous x rows across the y delta
           result[x,y] = Mathf.Lerp(lerpX1, lerpX2, ty);
        }
        
        return result;
    }

    void AddValues(float[,] oldHeightMap, bool[,] dla)
    {
        if (oldHeightMap.GetLength(0) != dla.GetLength(0))
        {
            throw new ArgumentException("Mismatched dimensions in AddValues()");
        }
        
        int mapSize = oldHeightMap.GetLength(0);
        
        for (int y = 0; y < mapSize; y++)
        for (int x = 0; x < mapSize; x++)
        {
            oldHeightMap[x, y] = dla[x, y] ? 1f + oldHeightMap[x, y] : oldHeightMap[x, y];
        }
    }
    
    
    void GenerateDla()
    {
        int currSize = 4;
        int center = currSize / 2;
        float currDensity = particleDensity; 
        bool[,] currMap = new bool[currSize, currSize];
        float[,] inProgressHeightMap = new float[currSize, currSize];
        _filledCount = 1;
        _dla = new bool[size, size];
        _heightMap = new float[size, size];
        
            
        currMap[center,center] = true;

        while (currSize <= size)
        {
            int target = Mathf.RoundToInt(currSize * currSize * currDensity);
            int toPlace = Mathf.Max(0, target - _filledCount); 
            int totalAttempts = 0;
            int maxAttempts = target * 10;

            while (toPlace > 0 && totalAttempts < maxAttempts)
            {
                if (RunWalker(currMap, currSize))
                {
                    _filledCount++;
                    toPlace--;
                    totalAttempts = 0;
                }
                else
                {
                    totalAttempts++;     
                }
                
            }

            // If it's the first pass, init from the bool map
            if (currSize == 4)
                inProgressHeightMap = InitHeightMapFromBool(currMap);
            
            
            // Upscale and process
            currSize *= 2;
            if (currSize <= size)
            {
                inProgressHeightMap = UpscaleFloatMap(inProgressHeightMap, currSize); 
                currMap = UpscaleDla(currMap, currSize);
                AddValues(inProgressHeightMap, currMap);
            }

            if (progressiveLoosening) 
                currDensity *= loosenFactor;
        }
        
        _dla = currMap;
        _heightMap = inProgressHeightMap;
    }
    
    bool RunWalker(bool[,] map, int n)
    {
        int x = Random.Range(0, n);
        int y = Random.Range(0, n);
        
        for (int step = 0; step < maxSteps; step++)
        {
            if (HasNeighbor(map, x, y) && !map[x, y])
            {
                map[x, y] = true;
                _filledCount++;
                return true;
            }
            
            // random move (8-dir) with bounds
            switch (Random.Range(0,8))
            {
                case 0: if (y+1 < n) y++; break;        // up
                case 1: if (y-1 >= 0) y--; break;        // down
                case 2: if (x+1 < n) x++; break;         // right
                case 3: if (x-1 >= 0) x--; break;         // left
                case 4: if (x+1 < n && y+1 < n) { x++; y++; } break; // up-right
                case 5: if (x-1 >= 0 && y+1 < n) { x--; y++; } break; // up-left
                case 6: if (x+1 < n && y-1 >= 0) { x++; y--; } break; // down-right
                case 7: if (x-1 >= 0 && y-1 >= 0) { x--; y--; } break; // down-left

            }
        }
        return false;   // walker gave up
    }

bool HasNeighbor(bool[,] m, int x, int y)
{
    int w = m.GetLength(0);
    int h = m.GetLength(1);
    for (int dx=-1; dx<=1; dx++)
    for (int dy=-1; dy<=1; dy++)
    {
        if (dx == 0 && dy == 0) continue;
        int nx = x + dx, ny = y + dy;
        if (nx >= 0 && ny >= 0 && nx < w && ny < h && m[nx, ny])
            return true;
    } 
    return false;
}

bool[,] UpscaleDla(bool[,] oldMap, int newSize)
    {
        int oldSize = oldMap.GetLength(0);
        bool[,] upscaledMap = new bool[newSize, newSize];

        // The first pass translates existing points on the low-scale map
        // to corresponding points on the size-doubled map, without connecting them
        for (int y = 0; y < oldSize; ++y)
        for (int x = 0; x < oldSize; ++x)
        {
            if (!oldMap[x, y])
                continue;

            bool upLeftExists = (x - 1 >= 0 && y - 1 >= 0 && oldMap[x - 1, y - 1]);
            bool leftExists = (x - 1 >= 0) && (oldMap[x - 1, y]);
            bool upExists = (y - 1 >= 0) && (oldMap[x, y - 1]); 
            
            int hx = 2 * x + 1;
            int hy = 2 * y + 1;

            // Skip copying points that were surrounded on the top and left in the low-res
            // map and decrement the count there
            if (upLeftExists && leftExists && upExists)
                _filledCount--;
            else
                upscaledMap[hx, hy] = true; 
        }
        
        // Second pass checks whether a point in the old map had a connection to the
        // right and the bottom, and if so, adds a corresponding point in the upscaled
        // map
        int randomSwitch;
        
        for (int y = 0; y < oldSize; ++y)
        for (int x = 0; x < oldSize; ++x)
        {
            if (!oldMap[x, y])
                continue;
            
            int hx = 2 * x + 1;
            int hy = 2 * y + 1;
           
            bool downExists = (y + 1 < oldSize && oldMap[x, y + 1]);
            bool rightExists = (x + 1 < oldSize && oldMap[x + 1, y]);
           
            // If the right pixel existed, we will favor adding a right connection
            // with a chance of it turning into an upright or down-right connection instead.
            if (rightExists) {
                randomSwitch = Random.Range(0, 8);
                if (randomSwitch is >= 0 and < 4)
                {
                    // extend the right connection
                    upscaledMap[hx + 1, hy] = true; // E
                    _filledCount++;
                } else if (randomSwitch is >= 4 and < 6 && hy > 0)
                {
                    // add an upward right connection
                    upscaledMap[hx + 1, hy - 1] = true;
                    _filledCount++;
                }
                else if (hx + 1 < newSize && hy + 1 < newSize)
                {
                    // add a down-right connection
                    upscaledMap[hx + 1, hy + 1] = true;
                    _filledCount++;
                }
            }

            if (downExists)
            {
                randomSwitch = Random.Range(0, 8);

                if (randomSwitch is >= 0 and < 4 && hy + 1 < newSize)
                {
                    // extend the down connection
                    upscaledMap[hx, hy + 1] = true; // S
                    _filledCount++;
                } else if (randomSwitch is >= 4 and < 6 && hx > 0 && hy + 1 < newSize)
                {
                    // instead, wiggle down and left
                    upscaledMap[hx - 1, hy + 1] = true;
                    _filledCount++;
                }
                else if (hx + 1 < newSize && hy + 1 < newSize)
                {
                    // instead, wiggle down and right
                    upscaledMap[hx + 1, hy + 1] = true;
                    _filledCount++;
                }
            }
        }
        
        // Third pass checks whether a right and bottom connection exists. If a right connection
        // exists, then we do not add diagonal connections there. If it does not exist, then we
        // check for diagonal connections in the old map and add them. Run two passes to enforce slightly
        // more connectivity
        for (int passes = 0; passes < 2; passes++)
        for (int y = 0; y < oldSize; ++y)
        for (int x = 0; x < oldSize; ++x)
        {
            int hx = 2 * x + 1;
            int hy = 2 * y + 1;

            if (!upscaledMap[hx, hy])
                continue;

            // A right connection does not exist
            if (x + 1 < oldSize && !upscaledMap[hx + 1, hy])
            {
                bool downRightExists = (y + 1 < oldSize) && (oldMap[x + 1, y + 1]);
                bool upRightExists = (y - 1 >= 0) && (oldMap[x + 1, y - 1]);
                randomSwitch = Random.Range(0, 8);
                
                // Both an upper right and lower right connection exist in the old map, then
                // choose one of them. Otherwise, check for the case where only one of those diagonals
                // previously existed.
                if (upRightExists && downRightExists)
                {
                    // down-right branch
                    if (Random.Range(0,200) % 2 == 0)
                    {
                        if (randomSwitch is >= 0 and < 4)
                        {
                            // stick with the down-right path
                            upscaledMap[hx + 1, hy + 1] = true;
                            _filledCount++;
                        } else if (randomSwitch is >= 4 and < 6)
                        {
                            // add a right connection instead
                            upscaledMap[hx + 1, hy] = true;
                            _filledCount++;
                        }
                        else
                        {
                            // add a down connection instead
                            upscaledMap[hx, hy + 1] = true;
                            _filledCount++;
                        }
                    }
                    // up-right branch
                    else
                    {
                        if (randomSwitch is >= 0 and < 4 && hy > 0)
                        {
                            // stick with the up and right path
                            upscaledMap[hx + 1, hy - 1] = true;
                            _filledCount++;
                        } else if (randomSwitch is >= 4 and < 6 && hy > 0)
                        {
                            // add an up connection
                            upscaledMap[hx, hy - 1] = true;
                            _filledCount++;
                        } else
                        {
                            // add a right connection instead
                            upscaledMap[hx + 1, hy] = true;
                            _filledCount++;
                        }
                    }
                } else if (downRightExists)
                {
                    if (randomSwitch is >= 0 and < 4)
                    {
                        // stick with the down-right path
                        upscaledMap[hx + 1, hy + 1] = true;
                        _filledCount++;
                    } else if (randomSwitch is >= 4 and < 6)
                    {
                        // add a right connection instead
                        upscaledMap[hx + 1, hy] = true;
                        _filledCount++;
                    }
                    else
                    {
                        // add a down connection instead
                        upscaledMap[hx, hy + 1] = true;
                        _filledCount++;
                    }
                }
                else if (upRightExists)
                {
                    if (randomSwitch is >= 0 and < 4 && hy - 1 >= 0)
                    {
                        // stick with the up and right path
                        upscaledMap[hx + 1, hy - 1] = true;
                        _filledCount++;
                    } else if (randomSwitch is >= 4 and < 6 && hy > 0)
                    {
                        // add an up connection
                        upscaledMap[hx, hy - 1] = true;
                        _filledCount++;
                    } else
                    {
                        // add a right connection instead
                        upscaledMap[hx + 1, hy] = true;
                        _filledCount++;
                    }
                }
            }
        }
        
        return upscaledMap;
    }

    // Mesh building function.
    void BuildMesh()
    {
        float max = float.MinValue;
        float half = size * 0.5f;
        var mesh = new Mesh();
        
        if(size*size > 65000)
            mesh.indexFormat = IndexFormat.UInt32;
        
        var verts = new Vector3[size * size];
        var uvs   = new Vector2[verts.Length];
        var tris  = new int[(size - 1) * (size - 1) * 6];

        for (int y = 0; y < size; ++y)
        for (int x = 0; x < size; ++x)
        {
            if (_heightMap[x,y] > max)
                max = _heightMap[x,y];
               
            int i   = x + y * size;
            float h = _heightMap[x, y] * heightScale;
            verts[i] = new Vector3(x - half, h, y - half);
            uvs[i]   = new Vector2((float)x / size, (float)y / size);
        }

        int t = 0;
        for (int y = 0; y < size - 1; ++y)
        for (int x = 0; x < size - 1; ++x)
        {
            int i = x + y * size;
            tris[t++] = i;
            tris[t++] = i + size;
            tris[t++] = i + size + 1;
            tris[t++] = i;
            tris[t++] = i + size + 1;
            tris[t++] = i + 1;
        }
        
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.uv = uvs;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        
        #if UNITY_EDITOR
        UnityEditor.EditorApplication.delayCall += () =>
        {
            if (this != null)
                GetComponent<MeshFilter>().mesh = mesh;
        };
        #endif
        
        var colors = new Color[mesh.vertexCount]; 
        for(int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int i = x + y * size;
            float v = Mathf.Clamp01(_heightMap[x , y] / max);
            
            Color c = v < 0.5f ?
                Color.Lerp(new Color(0.13f, 0.55f, 0.13f), new Color(0.4f, 0.26f, 0.13f), v * 2f)
                : Color.Lerp(new Color(0.4f, 0.26f, 0.13f), Color.white, (v - 0.5f) * 2f);
            
            colors[i] = c;
        }

        mesh.colors = colors;
    }
    
    // Preview the DLA Texture on a canvas
    Texture2D PreviewDla(bool[,] map)
    {
        int w = map.GetLength(0);
        int h = map.GetLength(1);
        Texture2D tex = new Texture2D(w, h)
        {
            filterMode = FilterMode.Point
        };
        for (int x = 0; x < w; ++x)
        for (int y = 0; y < h; ++y)
            tex.SetPixel(x, y, map[x, y] ? Color.white : Color.black);
        tex.Apply();
        return tex;
    }
}
