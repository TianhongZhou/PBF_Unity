using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using static SDF;

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct Field
{
    public Vector4 normal;
    public float distance;
    public int inside;
    public Vector2 pad;
}

public class SDF : MonoBehaviour
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct Triangle
    {
        public Vector4 v0;
        public Vector4 v1;
        public Vector4 v2;
        public Vector4 v21;
        public Vector4 v32;
        public Vector4 v13;
        public Vector4 nor;
    }

    public int resolution = 256;
    public Mesh mesh;
    public ComputeShader computeShader;

    private Field[] voxels;
    private ComputeBuffer voxelBuffer;
    private ComputeBuffer triangleBuffer;
    private Vector3 minBB = Vector3.positiveInfinity;
    private Vector3 maxBB = Vector3.zero;
    private int triangleCount = 0;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Awake()
    {
        int total = resolution * resolution * resolution;
        voxelBuffer = new ComputeBuffer(total, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Field)));
        Field[] voxelData = new Field[total];
        for (int i = 0; i < total; i++)
        {
            voxelData[i].inside = 0;
            voxelData[i].normal = Vector4.zero;
            voxelData[i].distance = float.MaxValue;
        }
        voxelBuffer.SetData(voxelData);

        GetTriangleBuffer();

        DispatchVoxelization();
        DispatchDistanceComputing();

        voxels = new Field[resolution * resolution * resolution];
        voxelBuffer.GetData(voxels);
    }

    void OnDestroy()
    {
        if (voxelBuffer != null) voxelBuffer.Release();
        if (triangleBuffer != null) triangleBuffer.Release();
    }

    void GetTriangleBuffer()
    {
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;

        List<Triangle> triList = new List<Triangle>();
        for (int i = 0; i < triangles.Length / 3; i++)
        {
            int i0 = triangles[i * 3];
            int i1 = triangles[i * 3 + 1];
            int i2 = triangles[i * 3 + 2];

            Vector3 v0 = vertices[i0];
            Vector3 v1 = vertices[i1];
            Vector3 v2 = vertices[i2];
            Vector3 normal = Vector3.Cross(v1 - v0, v0 - v2);
            if (normal.normalized.sqrMagnitude < 1e-6f)
            {
                continue;
            }

            triList.Add(new Triangle
            {
                v0 = new Vector4(v0.x, v0.y, v0.z, 0f),
                v1 = new Vector4(v1.x, v1.y, v1.z, 0f),
                v2 = new Vector4(v2.x, v2.y, v2.z, 0f),
                v21 = new Vector4(v1.x - v0.x, v1.y - v0.y, v1.z - v0.z, 0f),
                v32 = new Vector4(v2.x - v1.x, v2.y - v1.y, v2.z - v1.z, 0f),
                v13 = new Vector4(v0.x - v2.x, v0.y - v2.y, v0.z - v2.z, 0f),
                nor = new Vector4(normal.x, normal.y, normal.z, 0f)
            });

            minBB = Vector3.Min(minBB, v0);
            minBB = Vector3.Min(minBB, v1);
            minBB = Vector3.Min(minBB, v2);
            maxBB = Vector3.Max(maxBB, v0);
            maxBB = Vector3.Max(maxBB, v1);
            maxBB = Vector3.Max(maxBB, v2);
        }

        triangleCount = triList.Count;
        triangleBuffer = new ComputeBuffer(triangleCount, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Triangle)));
        triangleBuffer.SetData(triList);
    }

    void DispatchVoxelization()
    {
        int kernel = computeShader.FindKernel("Voxelization");
        computeShader.SetBuffer(kernel, "voxels", voxelBuffer);
        computeShader.SetBuffer(kernel, "triangles", triangleBuffer);
        computeShader.SetVector("minBB", minBB);
        computeShader.SetVector("maxBB", maxBB);
        computeShader.SetInt("res", resolution);
        computeShader.SetInt("triangleCount", triangleCount);
        computeShader.Dispatch(kernel, 1, Mathf.CeilToInt(resolution / 32f), Mathf.CeilToInt(resolution / 32f));
    }

    void DispatchDistanceComputing()
    {
        int kernel = computeShader.FindKernel("ComputeDistance");
        computeShader.SetBuffer(kernel, "voxels", voxelBuffer);
        computeShader.SetBuffer(kernel, "triangles", triangleBuffer);
        computeShader.SetVector("minBB", minBB);
        computeShader.SetVector("maxBB", maxBB);
        computeShader.SetInt("res", resolution);
        computeShader.SetInt("triangleCount", triangleCount);
        computeShader.Dispatch(kernel, Mathf.CeilToInt(resolution / 8f), Mathf.CeilToInt(resolution / 8f), Mathf.CeilToInt(resolution / 8f));
    }

    public Field[] GetFields()
    {
        return voxels;
    }

    public Vector3 GetMinBB()
    {
        return minBB;
    }

    public Vector3 GetMaxBB()
    {
        return maxBB;
    }
}
