using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using System.Collections.Generic;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class DLAGenerator : MonoBehaviour
{
    [Header("UI Display Target")]
    public RawImage displayTarget;
    
    [Header("Map size / particles")] [Range(4, 4096)]
    public int size = 1024;

    void OnValidate()
    {
        size = Mathf.ClosestPowerOfTwo(size);
    }

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
    public bool useRandomSeed = false;

    //---------------------------------------------------------//
    
    Texture2D  _preview;     // optional preview texture
    float[,]   _heightMap;   // blurred DLA -> floats
    bool[,]    _dla;
    private int filledCount;
    private float spawnRadius;

    void Start()
    {
        if (useRandomSeed)
            seed = Random.Range(int.MinValue, int.MaxValue);
        Random.InitState(seed);
        GenerateDLA();
        displayTarget.texture = PreviewTexture(_dla);
        displayTarget.rectTransform.sizeDelta = new Vector2(size, size);
    }

    /* ---------------- generation ---------------- */

    void GenerateDLA()
    {
        int currSize = 4;
        int center = currSize / 2;
        float currDensity = particleDensity; 
        bool[,] currMap = new bool[currSize, currSize];
        filledCount = 1;
        _dla = new bool[size, size];
        
            
        currMap[center,center] = true;

        while (currSize <= size)
        {
            int target = Mathf.RoundToInt(currSize * currSize * currDensity);
            int toPlace = Mathf.Max(0, target - filledCount); 
            int totalAttempts = 0;
            int maxAttempts = target * 10;

            while (toPlace > 0 && totalAttempts < maxAttempts)
            {
                if (RunWalker(currMap, currSize))
                {
                    filledCount++;
                    toPlace--;
                    totalAttempts = 0;
                }
                else
                {
                    totalAttempts++;     
                }
                
            }


            // upscale → returns brand-new arrays the *right* size
            currSize *= 2;
            if (currSize <= size)
            {
                currMap = UpscaleDLA(currMap, currSize);
            }

            if (progressiveLoosening) currDensity *= loosenFactor;
        }
        _dla = currMap;
    }
    
    bool RunWalker(bool[,] map, int n)
    {
        
        int x, y;
        x = Random.Range(0, n);
        y = Random.Range(0, n);
        
        for (int step = 0; step < maxSteps; step++)
        {
            if (HasNeighbor(map, x, y) && !map[x, y])
            {
                map[x, y] = true;
                filledCount++;
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

bool[,] UpscaleDLA(bool[,] oldMap, int newSize)
    {
        int oldSize = oldMap.GetLength(0);
        bool[,] upscaledMap = new bool[newSize, newSize];

        // First pass translates existing points on the low-scale map
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
                filledCount--;
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
            // with a chance of it turning into an up-right or down-right connection instead.
            if (rightExists) {
                randomSwitch = Random.Range(0, 8);
                if (randomSwitch >= 0 && randomSwitch < 4)
                {
                    // extend the right connection
                    upscaledMap[hx + 1, hy] = true; // E
                    filledCount++;
                } else if (randomSwitch >= 4 && randomSwitch < 6 && hy - 1 >= 0)
                {
                    // add an up-right connection
                    upscaledMap[hx + 1, hy - 1] = true;
                    filledCount++;
                }
                else if (hx + 1 < newSize && hy + 1 < newSize)
                {
                    // add a down-right connection
                    upscaledMap[hx + 1, hy + 1] = true;
                    filledCount++;
                }
            }

            if (downExists)
            {
                randomSwitch = Random.Range(0, 8);

                if (randomSwitch >= 0 && randomSwitch < 4 && hy + 1 < newSize)
                {
                    // extend the down connection
                    upscaledMap[hx, hy + 1] = true; // S
                    filledCount++;
                } else if (randomSwitch >= 4 && randomSwitch < 6 && hx - 1 >= 0 && hy + 1 < newSize)
                {
                    // instead wiggle down left
                    upscaledMap[hx - 1, hy + 1] = true;
                    filledCount++;
                }
                else if (hx + 1 < newSize && hy + 1 < newSize)
                {
                    // instead wiggle down right
                    upscaledMap[hx + 1, hy + 1] = true;
                    filledCount++;
                }
            }
        }
        
        // Third pass checks whether a right and bottom connection exists. If a right connection
        // exists, then we do not add diagonal connections there. If it does not exist, then we
        // check for diagonal connections in the old map and add them.
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
                
                // both an upper right and lower right connection exist in the old map, then
                // choose one of them. Otherwise, check for the case where only one of those diagonals
                // previously existed.
                if (upRightExists && downRightExists)
                {
                    // down-right branch
                    if (Random.Range(0,200) % 2 == 0)
                    {
                        if (randomSwitch >= 0 && randomSwitch < 4)
                        {
                            // stick with the down-right path
                            upscaledMap[hx + 1, hy + 1] = true;
                            filledCount++;
                        } else if (randomSwitch >= 4 && randomSwitch < 6)
                        {
                            // add a right connection instead
                            upscaledMap[hx + 1, hy] = true;
                            filledCount++;
                        }
                        else
                        {
                            // add a down connection instead
                            upscaledMap[hx, hy + 1] = true;
                            filledCount++;
                        }
                    }
                    // up-right branch
                    else
                    {
                        if (randomSwitch >= 0 && randomSwitch < 4 && hy - 1 >= 0)
                        {
                            // stick with the up-right path
                            upscaledMap[hx + 1, hy - 1] = true;
                            filledCount++;
                        } else if (randomSwitch >= 4 && randomSwitch < 6 && hy - 1 >= 0)
                        {
                            // add an up connection
                            upscaledMap[hx, hy - 1] = true;
                            filledCount++;
                        } else
                        {
                            // add a right connection instead
                            upscaledMap[hx + 1, hy] = true;
                            filledCount++;
                        }
                    }
                } else if (downRightExists)
                {
                    if (randomSwitch >= 0 && randomSwitch < 4)
                    {
                        // stick with the down-right path
                        upscaledMap[hx + 1, hy + 1] = true;
                        filledCount++;
                    } else if (randomSwitch >= 4 && randomSwitch < 6)
                    {
                        // add a right connection instead
                        upscaledMap[hx + 1, hy] = true;
                        filledCount++;
                    }
                    else
                    {
                        // add a down connection instead
                        upscaledMap[hx, hy + 1] = true;
                        filledCount++;
                    }
                }
                else if (upRightExists)
                {
                    if (randomSwitch >= 0 && randomSwitch < 4 && hy - 1 >= 0)
                    {
                        // stick with the up-right path
                        upscaledMap[hx + 1, hy - 1] = true;
                        filledCount++;
                    } else if (randomSwitch >= 4 && randomSwitch < 6 && hy - 1 >= 0)
                    {
                        // add an up connection
                        upscaledMap[hx, hy - 1] = true;
                        filledCount++;
                    } else
                    {
                        // add a right connection instead
                        upscaledMap[hx + 1, hy] = true;
                        filledCount++;
                    }
                }
            }
        }
        return upscaledMap;
    }

    /*
    // Mesh building function - unused for now.
    void BuildMesh()
    {
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

        GetComponent<MeshFilter>().mesh             = mesh;
        GetComponent<Renderer>().material.mainTexture = PreviewTexture(_dla);
    }
*/
    /* ---------------- optional preview ---------------- */

    Texture2D PreviewTexture(bool[,] map)
    {
        int w = map.GetLength(0);
        int h = map.GetLength(1);
        Texture2D tex = new Texture2D(w, h);
        tex.filterMode = FilterMode.Point;
        for (int x = 0; x < w; ++x)
        for (int y = 0; y < h; ++y)
            tex.SetPixel(x, y, map[x, y] ? Color.white : Color.black);
        tex.Apply();
        return tex;
    }
}
