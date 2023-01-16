#if UNITY_EDITOR
// If this symbol is defined, the time of the BuildFromSkinMeshRenderer method is measured. 
// It is defined for debugging. 
// #define MEASUREMENT_METHOD_BuildFromSkinMeshRenderer
#endif

using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace CyDecal.Runtime.Scripts.Core
{
    /// <summary>
    ///     Create Triangle polygons info from the mesh renderer or skinned mesh renderer. 
    /// </summary>
    public class CyTrianglePolygonsFactory : IDisposable
    {
        private static readonly int VertexCountOfTrianglePolygon = 3;
        private static readonly int MaxWorkingVertexCount = 65536;
        private static readonly int MaxWorkingTriangleCount = 65536;
        private readonly List<BoneWeight> _workingBoneWeights = new List<BoneWeight>(MaxWorkingVertexCount);
        private readonly List<int> _workingTrianglesForCalcPolygonCount = new List<int>(MaxWorkingTriangleCount);

        private NativeArray<int> _workingTriangles =
            new NativeArray<int>(MaxWorkingTriangleCount, Allocator.Persistent);

        private NativeArray<Vector3> _workingVertexNormals =
            new NativeArray<Vector3>(MaxWorkingVertexCount, Allocator.Persistent);

        private NativeArray<Vector3> _workingVertexPositions =
            new NativeArray<Vector3>(MaxWorkingVertexCount, Allocator.Persistent);

        private bool _disposed;
        public static int MaxGeneratedPolygonPerFrame { get; set; } = 100000; //
#if MEASUREMENT_METHOD_BuildFromSkinMeshRenderer
        public static float[] Time_BuildFromSkinMeshRenderer { get; set; } = new float[3];
#endif

        public void Dispose()
        {
            if (_disposed) return;
            _workingVertexPositions.Dispose();
            _workingVertexNormals.Dispose();
            _workingTriangles.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
        
        internal IEnumerator BuildFromReceiverObject(
            MeshFilter[] meshFilters,
            MeshRenderer[] meshRenderers,
            SkinnedMeshRenderer[] skinnedMeshRenderers,
            List<ConvexPolygonInfo> trianglePolygonInfos)
        {
            CalculateCapacityConvexPolygonInfos(meshFilters, skinnedMeshRenderers, trianglePolygonInfos);
            yield return BuildFromMeshFilter(meshFilters, meshRenderers, trianglePolygonInfos);
            yield return BuildFromSkinMeshRenderer(skinnedMeshRenderers, trianglePolygonInfos);

            yield return null;
        }
        
        private static int GetNumPolygonsFromSkinModelRenderers(SkinnedMeshRenderer[] skinnedMeshRenderers)
        {
            var numPolygon = 0;

            foreach (var renderer in skinnedMeshRenderers)
            {
                if (!renderer || renderer.sharedMesh == null) return -1;
                var mesh = renderer.sharedMesh;
                numPolygon += mesh.triangles.Length / 3;
            }

            return numPolygon;
        }
        
        private static int GetNumPolygonsFromMeshFilters(MeshFilter[] meshFilters)
        {
            var numPolygon = 0;
            foreach (var meshFilter in meshFilters)
            {
                if (!meshFilter || meshFilter.sharedMesh == null) return -1;
                var mesh = meshFilter.sharedMesh;
                var numPoly = mesh.triangles.Length / 3;
                numPolygon += numPoly;
            }

            return numPolygon;
        }
        
        private static void CalculateCapacityConvexPolygonInfos(MeshFilter[] meshFilters,
            SkinnedMeshRenderer[] skinnedMeshRenderers, List<ConvexPolygonInfo> convexPolygonInfos)
        {
            var capacity = 0;
            capacity += GetNumPolygonsFromMeshFilters(meshFilters);
            capacity += GetNumPolygonsFromSkinModelRenderers(skinnedMeshRenderers);
            if (capacity > 0) convexPolygonInfos.Capacity = capacity;
        }
        
        private IEnumerator BuildFromMeshFilter(MeshFilter[] meshFilters, MeshRenderer[] meshRenderers,
            List<ConvexPolygonInfo> convexPolygonInfos)
        {
            var numBuildConvexPolygon = GetNumPolygonsFromMeshFilters(meshFilters);
            if (numBuildConvexPolygon < 0) yield break;
            var newConvexPolygonInfos = new ConvexPolygonInfo[numBuildConvexPolygon];

            // Calculate size of some buffers and store the count of the polygons.
            var bufferSize = 0;
            var polygonCounts = new List<int>();
            foreach (var meshFilter in meshFilters)
            {
                if (!meshFilter || meshFilter.sharedMesh == null)
                    continue;
                var mesh = meshFilter.sharedMesh;
                var subMeshCount = mesh.subMeshCount;
                for (var meshNo = 0; meshNo < subMeshCount; meshNo++)
                {
                    mesh.GetTriangles(_workingTrianglesForCalcPolygonCount, meshNo);
                    var numPoly = _workingTrianglesForCalcPolygonCount.Count / 3;
                    bufferSize += numPoly * VertexCountOfTrianglePolygon;
                    polygonCounts.Add(numPoly);
                }
            }

            // Allocate some buffers.
            var positionBuffer = new Vector3[bufferSize];
            var boneWeightBuffer = new BoneWeight[bufferSize];
            var normalBuffer = new Vector3[bufferSize];
            var lineBuffer = new CyLine[bufferSize];
            var localPositionBuffer = new Vector3[bufferSize];
            var localNormalBuffer = new Vector3[bufferSize];
            var startOffsetOfBuffer = 0;

            var rendererNo = 0;
            var newConvexPolygonNo = 0;
            var indexOfPolygonCounts = 0;
            foreach (var meshFilter in meshFilters)
            {
                if (!meshFilter || meshFilter.sharedMesh == null)
                    // Mesh filter is deleted, so process is terminated.
                    yield break;
                var mesh = meshFilter.sharedMesh;
                using var meshDataArray = Mesh.AcquireReadOnlyMeshData(mesh);
                var meshData = meshDataArray[0];
                meshData.GetVertices(_workingVertexPositions);
                meshData.GetNormals(_workingVertexNormals);
                var subMeshCount = meshData.subMeshCount;
                for (var meshNo = 0; meshNo < subMeshCount; meshNo++)
                {
                    meshData.GetIndices(_workingTriangles, meshNo);
                    var numPoly = polygonCounts[indexOfPolygonCounts++];
                    for (var i = 0; i < numPoly; i++)
                    {
                        if ((newConvexPolygonNo + 1) % MaxGeneratedPolygonPerFrame == 0)
                            // Maximum number of polygons processed per frame is MaxGeneratedPolygonPerFrame.
                            yield return null;
                        if (!meshFilter || meshFilter.sharedMesh == null)
                            // Mesh filter is deleted, so process is terminated.
                            yield break;
                        var v0_no = _workingTriangles[i * 3];
                        var v1_no = _workingTriangles[i * 3 + 1];
                        var v2_no = _workingTriangles[i * 3 + 2];

                        localPositionBuffer[startOffsetOfBuffer] = _workingVertexPositions[v0_no];
                        localPositionBuffer[startOffsetOfBuffer + 1] = _workingVertexPositions[v1_no];
                        localPositionBuffer[startOffsetOfBuffer + 2] = _workingVertexPositions[v2_no];

                        localNormalBuffer[startOffsetOfBuffer] = _workingVertexNormals[v0_no];
                        localNormalBuffer[startOffsetOfBuffer + 1] = _workingVertexNormals[v1_no];
                        localNormalBuffer[startOffsetOfBuffer + 2] = _workingVertexNormals[v2_no];

                        boneWeightBuffer[startOffsetOfBuffer] = default;
                        boneWeightBuffer[startOffsetOfBuffer + 1] = default;
                        boneWeightBuffer[startOffsetOfBuffer + 2] = default;
                        newConvexPolygonInfos[newConvexPolygonNo] = new ConvexPolygonInfo
                        {
                            ConvexPolygon = new CyConvexPolygon(
                                positionBuffer,
                                normalBuffer,
                                boneWeightBuffer,
                                lineBuffer,
                                localPositionBuffer,
                                localNormalBuffer,
                                meshRenderers[rendererNo],
                                startOffsetOfBuffer,
                                VertexCountOfTrianglePolygon,
                                rendererNo,
                                VertexCountOfTrianglePolygon)
                        };
                        newConvexPolygonNo++;
                        startOffsetOfBuffer += VertexCountOfTrianglePolygon;
                    }
                }

                rendererNo++;
            }

            convexPolygonInfos.AddRange(newConvexPolygonInfos);
        }
        
        private IEnumerator BuildFromSkinMeshRenderer(SkinnedMeshRenderer[] skinnedMeshRenderers,
            List<ConvexPolygonInfo> trianglePolygonInfos)
        {
#if MEASUREMENT_METHOD_BuildFromSkinMeshRenderer
            var sw = new Stopwatch();
            sw.Start();
#endif
            var numBuildConvexPolygon = GetNumPolygonsFromSkinModelRenderers(skinnedMeshRenderers);
            if (numBuildConvexPolygon < 0) yield break;

            var newConvexPolygonInfos = new ConvexPolygonInfo[numBuildConvexPolygon];
            var boneWeights = new BoneWeight[3];
            var newConvexPolygonNo = 0;

            // Calculate size of some buffers and store the count of the polygons.
            var bufferSize = 0;
            var polygonCounts = new List<int>();
            for (var rendererNo = 0; rendererNo < skinnedMeshRenderers.Length; rendererNo++)
            {
                var skinnedMeshRenderer = skinnedMeshRenderers[rendererNo];
                if (!skinnedMeshRenderer || skinnedMeshRenderer.sharedMesh == null)
                    // The skinned mesh renderer is deleted, so skip.
                    continue;
                var mesh = skinnedMeshRenderer.sharedMesh;
                var subMeshCount = mesh.subMeshCount;
                for (var meshNo = 0; meshNo < subMeshCount; meshNo++)
                {
                    mesh.GetTriangles(_workingTrianglesForCalcPolygonCount, meshNo);
                    var numPoly = _workingTrianglesForCalcPolygonCount.Count / 3;
                    bufferSize += numPoly * VertexCountOfTrianglePolygon;
                    polygonCounts.Add(numPoly);
                }
            }

            // Allocate some buffers.
            var positionBuffer = new Vector3[bufferSize];
            var localPositionBuffer = new Vector3[bufferSize];
            var boneWeightBuffer = new BoneWeight[bufferSize];
            var normalBuffer = new Vector3[bufferSize];
            var localNormalBuffer = new Vector3[bufferSize];
            var lineBuffer = new CyLine[bufferSize];
            var startOffsetOfBuffer = 0;
#if MEASUREMENT_METHOD_BuildFromSkinMeshRenderer
            sw.Stop();
            Time_BuildFromSkinMeshRenderer[0] = sw.ElapsedMilliseconds;
            sw = new Stopwatch();
            sw.Start();
#endif
            var indexOfPolygonCount = 0;
            for (var rendererNo = 0; rendererNo < skinnedMeshRenderers.Length; rendererNo++)
            {
                var skinnedMeshRenderer = skinnedMeshRenderers[rendererNo];
                if (!skinnedMeshRenderer || skinnedMeshRenderer.sharedMesh == null)
                    // The skinned mesh renderer is deleted, so process is terminated.
                    yield break;
                var mesh = skinnedMeshRenderer.sharedMesh;

                using var meshDataArray = Mesh.AcquireReadOnlyMeshData(mesh);
                var meshData = meshDataArray[0];
                meshData.GetVertices(_workingVertexPositions);
                meshData.GetNormals(_workingVertexNormals);
                var subMeshCount = meshData.subMeshCount;
                mesh.GetBoneWeights(_workingBoneWeights);
                for (var meshNo = 0; meshNo < subMeshCount; meshNo++)
                {
                    meshData.GetIndices(_workingTriangles, meshNo);
                    var numPoly = polygonCounts[indexOfPolygonCount++];
                    for (var i = 0; i < numPoly; i++)
                    {
                        if ((newConvexPolygonNo + 1) % MaxGeneratedPolygonPerFrame == 0)
                            // Maximum number of polygons processed per frame is MaxGeneratedPolygonPerFrame.
                            yield return null;
                        if (!skinnedMeshRenderer || skinnedMeshRenderer.sharedMesh == null)
                            // The skinned mesh renderer is deleted, so process is terminated.
                            yield break;
                        var v0No = _workingTriangles[i * 3];
                        var v1No = _workingTriangles[i * 3 + 1];
                        var v2No = _workingTriangles[i * 3 + 2];

                        // Calculate world matrix.
                        if (skinnedMeshRenderer.rootBone != null)
                        {
                            boneWeights[0] = _workingBoneWeights[v0No];
                            boneWeights[1] = _workingBoneWeights[v1No];
                            boneWeights[2] = _workingBoneWeights[v2No];
                            boneWeightBuffer[startOffsetOfBuffer] = boneWeights[0];
                            boneWeightBuffer[startOffsetOfBuffer + 1] = boneWeights[1];
                            boneWeightBuffer[startOffsetOfBuffer + 2] = boneWeights[2];
                        }
                        else
                        {
                            boneWeightBuffer[startOffsetOfBuffer] = default;
                            boneWeightBuffer[startOffsetOfBuffer + 1] = default;
                            boneWeightBuffer[startOffsetOfBuffer + 2] = default;
                        }

                        localPositionBuffer[startOffsetOfBuffer] = _workingVertexPositions[v0No];
                        localPositionBuffer[startOffsetOfBuffer + 1] = _workingVertexPositions[v1No];
                        localPositionBuffer[startOffsetOfBuffer + 2] = _workingVertexPositions[v2No];

                        localNormalBuffer[startOffsetOfBuffer] = _workingVertexNormals[v0No];
                        localNormalBuffer[startOffsetOfBuffer + 1] = _workingVertexNormals[v1No];
                        localNormalBuffer[startOffsetOfBuffer + 2] = _workingVertexNormals[v2No];


                        newConvexPolygonInfos[newConvexPolygonNo] = new ConvexPolygonInfo
                        {
                            ConvexPolygon = new CyConvexPolygon(
                                positionBuffer,
                                normalBuffer,
                                boneWeightBuffer,
                                lineBuffer,
                                localPositionBuffer,
                                localNormalBuffer,
                                skinnedMeshRenderer,
                                startOffsetOfBuffer,
                                3,
                                rendererNo,
                                VertexCountOfTrianglePolygon)
                        };
                        newConvexPolygonNo++;
                        startOffsetOfBuffer += VertexCountOfTrianglePolygon;
                    }
                }
            }

            trianglePolygonInfos.AddRange(newConvexPolygonInfos);
#if MEASUREMENT_METHOD_BuildFromSkinMeshRenderer
            sw.Stop();
            Time_BuildFromSkinMeshRenderer[1] = sw.ElapsedMilliseconds;
#endif
        }
    }
}
