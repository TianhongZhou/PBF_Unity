using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class PBF : MonoBehaviour
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct Particle
    {
        public Vector3 position;
        public float pad0;

        public Vector3 velocity;
        public float pad1;

        public Vector3 predictedPosition;
        public float pad2;

        public Vector3 deltaP;
        public float pad3;

        public float lambda;
        public Vector3 pad4;

        public Vector4 color;
    }

    struct Uint2
    {
        public uint x;
        public uint y;

        public Uint2(uint x, uint y)
        {
            this.x = x;
            this.y = y;
        }
    }

    [System.Serializable]
    public struct BoundsBox
    {
        public Vector3 min;
        public Vector3 max;
        public Vector3 color;
    }

    public int particleCount = 20000;
    public Vector3[] forces;
    public int maxNeighborCount = 128;
    public int solverIterations = 5;
    public float rhoRest = 6378f;
    public float radius = 0.1f;
    public float epsilon = 600f;
    public float viscosity = 5e-5f;
    public Vector3[] boundingBox = new Vector3[2];
    public Vector3 minBBox => boundingBox[0];
    public Vector3 maxBBox => boundingBox[1];
    public BoundsBox[] spawnRegions;
    public int injectParticlesCount = 3000;
    public bool injectParticles = true;
    public GameObject meshPrefab;
    public ComputeShader renderShader;
    public bool renderParticles = true;
    public Material pointMaterial;
    public Material waterMaterial;

    private int maxParticleCount = 50000;
    private int currentParticleCount = 0;

    private List<GameObject> meshsAdded = new List<GameObject>();
    private List<Matrix4x4> worldToLocalArray = new List<Matrix4x4>();
    private List<Matrix4x4> localToWorldArray = new List<Matrix4x4>();

    private ComputeBuffer particleBuffer;
    private ComputeBuffer forceBuffer;
    private int dimX, dimY, dimZ, gridCellCount;
    private float h;
    private ComputeBuffer gridHashBuffer;
    private ComputeBuffer gridCellStartBuffer;
    private ComputeBuffer neighborIndicesBuffer;
    private ComputeBuffer neighborCountBuffer;
    private ComputeBuffer worldToLocalBuffer;
    private ComputeBuffer localToWorldBuffer;
    private ComputeBuffer voxelBuffer;

    private SDF sdf;
    private Field[] voxels = null;

    void Awake()
    {
        if (boundingBox == null || boundingBox.Length != 2)
        {
            boundingBox = new Vector3[2];
        }
        if (boundingBox[0] == Vector3.zero && boundingBox[1] == Vector3.zero)
        {
            boundingBox[0] = new Vector3(-1.5f, 0f, -1.5f);
            boundingBox[1] = new Vector3(1.5f, 10f, 1.5f);
        }
        if (spawnRegions.Length == 0)
        {
            spawnRegions = new BoundsBox[1];
            spawnRegions[0] = new BoundsBox
            {
                min = new Vector3(-1f, 1f, -1f),
                max = new Vector3(1f, 3f, 1f),
                color = new Vector3(0f, 0f, 1f)
            };
        }
    }

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        InitParticles();
        InitForces();
        InitGrid();

        sdf = GetComponent<SDF>();
    }

    void InitGrid()
    {
        h = radius;
        dimX = Mathf.CeilToInt((maxBBox.x - minBBox.x) / h);
        dimY = Mathf.CeilToInt((maxBBox.y - minBBox.y) / h);
        dimZ = Mathf.CeilToInt((maxBBox.z - minBBox.z) / h);
        gridCellCount = dimX * dimY * dimZ;
        gridHashBuffer = new ComputeBuffer(maxParticleCount, sizeof(uint) * 2);
        gridCellStartBuffer = new ComputeBuffer(gridCellCount, sizeof(int));
        neighborIndicesBuffer = new ComputeBuffer(maxParticleCount * maxNeighborCount, sizeof(uint));
        neighborCountBuffer = new ComputeBuffer(maxParticleCount, sizeof(uint));

        worldToLocalBuffer = new ComputeBuffer(1, sizeof(float) * 16);
        localToWorldBuffer = new ComputeBuffer(1, sizeof(float) * 16);
        voxelBuffer = new ComputeBuffer(1, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Field)));
    }

    void InitForces()
    {
        forceBuffer = new ComputeBuffer(forces.Length, sizeof(float) * 3);
        forceBuffer.SetData(forces);
    }

    void InitParticles()
    {
        Particle[] particles = new Particle[maxParticleCount];
        int boxes = spawnRegions.Length;
        int initCount = particleCount;

        if (initCount > maxParticleCount)
        {
            Debug.LogWarning("50000 Particles at most");
            return;
        }

        for (int i = 0; i < initCount; i++)
        {
            int boxIdx = i % boxes;
            Vector3 min = spawnRegions[boxIdx].min;
            Vector3 max = spawnRegions[boxIdx].max;

            particles[i].position = new Vector3(
                Random.Range(min.x, max.x),
                Random.Range(min.y, max.y),
                Random.Range(min.z, max.z)
            );
            particles[i].velocity = Vector3.zero;
            particles[i].predictedPosition = particles[i].position;
            Vector3 rgb = spawnRegions[boxIdx].color;
            particles[i].color = new Vector4(rgb.x, rgb.y, rgb.z, 1f);
            particles[i].deltaP = Vector3.zero;
            particles[i].lambda = 0;
        }

        currentParticleCount = initCount;

        particleBuffer = new ComputeBuffer(maxParticleCount, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Particle)));
        particleBuffer.SetData(particles);
    }

    void OnRenderObject()
    {
        if (renderParticles == true)
        {
            pointMaterial.SetPass(0);
            pointMaterial.SetBuffer("_Particles", particleBuffer);
            Graphics.DrawProceduralNow(MeshTopology.Points, currentParticleCount);
        }
    }

    void OnDestroy()
    {
        if (particleBuffer != null) particleBuffer.Release(); 
        if (forceBuffer != null) forceBuffer.Release();
        if (gridHashBuffer != null) gridHashBuffer.Release();
        if (gridCellStartBuffer != null) gridCellStartBuffer.Release();
        if (neighborIndicesBuffer != null) neighborIndicesBuffer.Release();
        if (neighborCountBuffer != null) neighborCountBuffer.Release();
        if (worldToLocalBuffer != null) worldToLocalBuffer.Release();
        if (localToWorldBuffer != null) localToWorldBuffer.Release();
        if (voxelBuffer != null) voxelBuffer.Release();
    }

    // Update is called once per frame
    void Update()
    {
        if (meshsAdded.Count > 0)
        {
            for (int i = 0; i < meshsAdded.Count; i++)
            {
                var go = meshsAdded[i];
                worldToLocalArray[i] = go.transform.worldToLocalMatrix;
                localToWorldArray[i] = go.transform.localToWorldMatrix;
            }

            worldToLocalBuffer.SetData(worldToLocalArray);
            localToWorldBuffer.SetData(localToWorldArray);
        }

        if (Input.GetMouseButtonDown(0))
        {
            float mouseX = GetMouseX();
            Vector3 spawnPos = new Vector3(mouseX, 3f, 0f);
            if (injectParticles)
            {
                Particle[] meshParticles = FillCubeWithParticles(spawnPos, injectParticlesCount);
                InjectParticles(meshParticles);
            }
            else
            {
                GameObject obj = Instantiate(meshPrefab, spawnPos, Quaternion.identity);
                obj.SetActive(true);
                meshsAdded.Add(obj);

                worldToLocalArray.Add(obj.transform.worldToLocalMatrix);
                localToWorldArray.Add(obj.transform.localToWorldMatrix);

                if (worldToLocalBuffer != null)
                {
                    worldToLocalBuffer.Release();
                    localToWorldBuffer.Release();
                }

                worldToLocalBuffer = new ComputeBuffer(worldToLocalArray.Count, sizeof(float) * 16);
                worldToLocalBuffer.SetData(worldToLocalArray);
                localToWorldBuffer = new ComputeBuffer(localToWorldArray.Count, sizeof(float) * 16);
                localToWorldBuffer.SetData(localToWorldArray);

                if (voxels == null)
                {
                    voxels = sdf.GetFields();
                    voxelBuffer = new ComputeBuffer(voxels.Length, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Field)));
                    voxelBuffer.SetData(voxels);
                }
            }
        }

        if (forceBuffer == null || forceBuffer.count != forces.Length)
        {
            if (forceBuffer != null)
                forceBuffer.Release();

            forceBuffer = new ComputeBuffer(forces.Length, sizeof(float) * 3);
        }
        forceBuffer.SetData(forces);

        DispatchApplyForcePredictPosition();

        DispatchComputeGridHash();
        DispatchBuildGridCellStart();
        DispatchFindNeighbors();

        for (int i = 0; i < solverIterations; i++)
        {
            DispatchComputeLambda();
            DispatchComputeDeltaPCollision();
            DispatchUpdatePredictPosition();
        }

        DispatchUpdateVelocityVorticityPosition();
    }

    float GetMouseX()
    {
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        Plane plane = new Plane(Vector3.forward, new Vector3(0, 0, 0));
        if (plane.Raycast(ray, out float enter))
        {
            Vector3 hitPoint = ray.GetPoint(enter);
            return hitPoint.x;
        }
        return 0f;
    }

    Particle[] FillCubeWithParticles(Vector3 center, int targetCount)
    {
        float cubeSize = 0.5f;
        float volume = cubeSize * cubeSize * cubeSize;
        float spacing = Mathf.Pow(volume / targetCount, 1f / 3f);

        List<Particle> particles = new List<Particle>();

        Vector3 half = Vector3.one * (cubeSize / 2f);

        for (float x = -half.x; x < half.x; x += spacing)
            for (float y = -half.y; y < half.y; y += spacing)
                for (float z = -half.z; z < half.z; z += spacing)
                {
                    Vector3 pos = center + new Vector3(x, y, z);
                    particles.Add(new Particle
                    {
                        position = pos,
                        predictedPosition = pos,
                        velocity = Vector3.zero,
                        deltaP = Vector3.zero,
                        lambda = 0,
                        color = new Vector4(0, 0, 0, 1)
                    });

                    if (particles.Count >= targetCount)
                        break;
                }

        return particles.ToArray();
    }

    void InjectParticles(Particle[] newParticles)
    {
        int newCount = newParticles.Length;

        if (currentParticleCount + newCount > maxParticleCount)
        {
            Debug.LogWarning("Particle buffer overflow!");
            return;
        }

        particleBuffer.SetData(newParticles, 0, currentParticleCount, newCount);
        currentParticleCount += newCount;
    }

    void DispatchComputeLambda()
    {
        int kernel = renderShader.FindKernel("ComputeLambda");
        renderShader.SetBuffer(kernel, "particles", particleBuffer);
        renderShader.SetBuffer(kernel, "neighborIndices", neighborIndicesBuffer);
        renderShader.SetBuffer(kernel, "neighborCounts", neighborCountBuffer);
        renderShader.SetFloat("h", h);
        renderShader.SetFloat("rho0", rhoRest);
        renderShader.SetFloat("epsilon", epsilon);
        renderShader.Dispatch(kernel, Mathf.CeilToInt(currentParticleCount / 512f), 1, 1);
    }

    void DispatchComputeDeltaPCollision()
    {
        int kernel = renderShader.FindKernel("ComputeDeltaP");
        renderShader.SetBuffer(kernel, "particles", particleBuffer);
        renderShader.SetBuffer(kernel, "neighborIndices", neighborIndicesBuffer);
        renderShader.SetBuffer(kernel, "neighborCounts", neighborCountBuffer);
        renderShader.SetFloat("h", h);
        renderShader.SetFloat("rho0", rhoRest);
        renderShader.SetVector("minBBox", minBBox);
        renderShader.SetVector("maxBBox", maxBBox);
        renderShader.SetBuffer(kernel, "voxels", voxelBuffer);
        renderShader.SetInt("res", sdf.resolution);
        renderShader.SetBuffer(kernel, "worldToLocalMatrices", worldToLocalBuffer);
        renderShader.SetBuffer(kernel, "localToWorldMatrices", localToWorldBuffer);
        renderShader.SetInt("matrixCount", worldToLocalArray.Count);
        renderShader.SetVector("minBB", sdf.GetMinBB());
        renderShader.SetVector("maxBB", sdf.GetMaxBB());
        renderShader.Dispatch(kernel, Mathf.CeilToInt(currentParticleCount / 512f), 1, 1);
    }

    void DispatchUpdatePredictPosition()
    {
        int kernel = renderShader.FindKernel("UpdatePredictPosition");
        renderShader.SetBuffer(kernel, "particles", particleBuffer);
        renderShader.Dispatch(kernel, Mathf.CeilToInt(currentParticleCount / 512f), 1, 1);
    }

    void DispatchComputeGridHash()
    {
        int kernel = renderShader.FindKernel("ComputeGridHash");
        renderShader.SetBuffer(kernel, "particles", particleBuffer);
        renderShader.SetBuffer(kernel, "gridHash", gridHashBuffer);
        renderShader.SetVector("minBBox", minBBox);
        renderShader.SetFloat("h", h);
        renderShader.SetInt("dimX", dimX);
        renderShader.SetInt("dimY", dimY);
        renderShader.SetInt("dimZ", dimZ);
        renderShader.Dispatch(kernel, Mathf.CeilToInt(currentParticleCount / 512f), 1, 1);

        Uint2[] cpuHash = new Uint2[currentParticleCount];
        gridHashBuffer.GetData(cpuHash);
        System.Array.Sort(cpuHash, (a, b) => a.x.CompareTo(b.x));
        gridHashBuffer.SetData(cpuHash);
    }

    void DispatchBuildGridCellStart()
    {
        int[] defaultStart = new int[gridCellCount];
        for (int i = 0; i < defaultStart.Length; i++) defaultStart[i] = -1;
        gridCellStartBuffer.SetData(defaultStart);

        int kernel = renderShader.FindKernel("BuildGridCellStartIndices");
        renderShader.SetBuffer(kernel, "sortedHash", gridHashBuffer);
        renderShader.SetBuffer(kernel, "gridCellStart", gridCellStartBuffer);
        renderShader.SetFloat("particleCount", currentParticleCount);
        renderShader.Dispatch(kernel, Mathf.CeilToInt(currentParticleCount / 512f), 1, 1);
    }

    void DispatchFindNeighbors()
    {
        int kernel = renderShader.FindKernel("FindNeighbors");
        renderShader.SetBuffer(kernel, "particles", particleBuffer);
        renderShader.SetBuffer(kernel, "sortedHash", gridHashBuffer);
        renderShader.SetBuffer(kernel, "gridCellStart", gridCellStartBuffer);
        renderShader.SetBuffer(kernel, "neighborIndices", neighborIndicesBuffer);
        renderShader.SetBuffer(kernel, "neighborCounts", neighborCountBuffer);
        renderShader.SetInt("dimX", dimX);
        renderShader.SetInt("dimY", dimY);
        renderShader.SetInt("dimZ", dimZ);
        renderShader.SetVector("minBBox", minBBox);
        renderShader.SetFloat("h", h);
        renderShader.SetFloat("particleCount", currentParticleCount);
        renderShader.SetInt("maxNeighborCount", maxNeighborCount);
        renderShader.Dispatch(kernel, Mathf.CeilToInt(currentParticleCount / 512f), 1, 1);
    }

    void DispatchApplyForcePredictPosition()
    {
        int kernel = renderShader.FindKernel("ApplyForcePredictPosition");
        renderShader.SetFloat("deltaTime", Time.deltaTime);
        renderShader.SetBuffer(kernel, "forces", forceBuffer);
        renderShader.SetInt("forceCount", forces.Length);
        renderShader.SetBuffer(kernel, "particles", particleBuffer);
        renderShader.Dispatch(kernel, Mathf.CeilToInt(currentParticleCount / 512f), 1, 1);
    }

    void DispatchUpdateVelocityVorticityPosition()
    {
        int kernel = renderShader.FindKernel("UpdateVelocityVorticityPosition");
        renderShader.SetBuffer(kernel, "particles", particleBuffer);
        renderShader.SetBuffer(kernel, "neighborIndices", neighborIndicesBuffer);
        renderShader.SetBuffer(kernel, "neighborCounts", neighborCountBuffer);
        renderShader.SetFloat("h", h);
        renderShader.SetFloat("deltaTime", Time.deltaTime);
        renderShader.SetFloat("c", viscosity);
        renderShader.Dispatch(kernel, Mathf.CeilToInt(currentParticleCount / 512f), 1, 1);
    }
}
