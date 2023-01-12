using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CyDecal.Runtime.Scripts.Core;
using UnityEngine;
using UnityEngine.Events;

namespace CyDecal.Runtime.Scripts
{
    /// <summary>
    ///     Mesh decal projector. <br />
    ///     This decal projector has the following characteristics. <br />
    ///         1. Fast, spike-free projection of mesh decals. <br />
    ///         2. Draw mesh decals with fewer draw calls. <br />
    ///         3. It works in both URP and BRP. <br />
    ///         4. All user-defined materials can be used. <br />
    ///         5. Mesh decals can be skin animated. <br />
    /// </summary>
    public sealed class CyDecalProjector : MonoBehaviour
    {
        public enum State
        {
            NotLaunch,
            Launching,
            LaunchingCompleted,
            LaunchingCanceled
        }

        [SerializeField] private float width; // Width of the decal box.
        [SerializeField] private float height; // Height of the decal box.
        [SerializeField] private float depth; // Depth of the decal box.
        [SerializeField] private GameObject receiverObject; // The receiver object that will be pasted decal.
        [SerializeField] private Material decalMaterial; // The decal material that will be pasted to the receiver object.

        [Tooltip("When this is checked, the decal projection process is started at the instance is created.")] [SerializeField]
        private bool launchOnAwake; // When an instance is created, the decal projection process also starts automatically.。

        [SerializeField] private UnityEvent<State> onFinishedLaunch; //　The event is called when decal projection is finished.

        private readonly Vector4[] _clipPlanes = new Vector4[(int)ClipPlane.Num]; 
        private List<ConvexPolygonInfo> _broadPhaseConvexPolygonInfos = new List<ConvexPolygonInfo>();
        private List<ConvexPolygonInfo> _convexPolygonInfos;
        private CyDecalSpace _decalSpace;
        private bool _executeLaunchingOnWorkerThread;
        /// <summary>
        ///     State of decal projector.
        /// </summary>
        public State NowState { get; private set; } = State.NotLaunch;

        /// <summary>
        ///     The list of decal mesh that has been generated by the projector. 
        /// </summary>
        public List<CyDecalMesh> DecalMeshes { get; } = new List<CyDecalMesh>();

        private void Start()
        {
            if (launchOnAwake) Launch(null);
        }

        private void OnDestroy()
        {
            // It may be deleted without completing the projection
            // So we`ll delete it here too.
            OnFinished(State.LaunchingCanceled);
        }

        private void OnDrawGizmosSelected()
        {
            var cache = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            // Draw the decal box.
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(width, height, depth));
            Gizmos.matrix = cache;
            Gizmos.color = Color.white;
            // Draw the arrow of the projection's direction.
            var arrowStart = transform.position;
            var arrowEnd = transform.position + transform.forward * depth;
            Gizmos.DrawLine(arrowStart, arrowEnd);
            Vector3 arrowTangent;
            if (Mathf.Abs(transform.forward.y) > 0.999f)
                arrowTangent = Vector3.Cross(transform.forward, Vector3.right);
            else
                arrowTangent = Vector3.Cross(transform.forward, Vector3.up);
            var rotAxis = Vector3.Cross(transform.forward, arrowTangent);
            var rotQuat = Quaternion.AngleAxis(45.0f, rotAxis.normalized);
            var arrowLeft = rotQuat * transform.forward * depth * -0.2f;
            Gizmos.DrawLine(arrowEnd, arrowEnd + arrowLeft);
            rotQuat = Quaternion.AngleAxis(-45.0f, rotAxis.normalized);
            var arrowRight = rotQuat * transform.forward * depth * -0.2f;
            Gizmos.DrawLine(arrowEnd, arrowEnd + arrowRight);
            Gizmos.matrix = cache;
        }
        
        private void OnFinished(State finishedState)
        {
            if (onFinishedLaunch == null) return;

            onFinishedLaunch.Invoke(finishedState);
            NowState = finishedState;
            onFinishedLaunch = null;
        }
        
        private static Matrix4x4[][] CalculateMatricesPallet(SkinnedMeshRenderer[] skinnedMeshRenderers)
        {
            var boneMatricesPallet = new Matrix4x4[skinnedMeshRenderers.Length][];
            var skindMeshRendererNo = 0;
            foreach (var skinnedMeshRenderer in skinnedMeshRenderers)
            {
                if (!skinnedMeshRenderer) continue;
                if (skinnedMeshRenderer.rootBone != null)
                {
                    var mesh = skinnedMeshRenderer.sharedMesh;
                    var numBone = skinnedMeshRenderer.bones.Length;

                    var boneMatrices = new Matrix4x4[numBone];
                    for (var boneNo = 0; boneNo < numBone; boneNo++)
                        boneMatrices[boneNo] = skinnedMeshRenderer.bones[boneNo].localToWorldMatrix
                                               * mesh.bindposes[boneNo];

                    boneMatricesPallet[skindMeshRendererNo] = boneMatrices;
                }

                skindMeshRendererNo++;
            }

            return boneMatricesPallet;
        }

        /// <summary>
        ///     Execute projection decal to mesh.
        /// </summary>
        /// <remarks>
        ///     This process is performed over multiple frames.
        ///     Projection completion can be monitored using callback functions or by checking the IsFinishedLaunch property.
        /// </remarks>
        /// <returns></returns>
        private IEnumerator ExecuteLaunch()
        {
            InitializeOriginAxisInDecalSpace();
            
            CyDecalSystem.CollectEditDecalMeshes(DecalMeshes, receiverObject, decalMaterial);

            var skinnedMeshRenderers = receiverObject.GetComponentsInChildren<SkinnedMeshRenderer>();
            skinnedMeshRenderers = skinnedMeshRenderers.Where(s => s.name != "CyDecalRenderer").ToArray();
            
            if (CyDecalSystem.ReceiverObjectTrianglePolygonsPool.Contains(receiverObject) == false)
            {
                // new receiver object.
                _convexPolygonInfos = new List<ConvexPolygonInfo>();
                // Collect triangle polygon data.
                yield return CyDecalSystem.BuildTrianglePolygonsFromReceiverObject(
                    receiverObject.GetComponentsInChildren<MeshFilter>(),
                    receiverObject.GetComponentsInChildren<MeshRenderer>(),
                    skinnedMeshRenderers,
                    _convexPolygonInfos);
                CyDecalSystem.ReceiverObjectTrianglePolygonsPool.RegisterTrianglePolygons(receiverObject,
                    _convexPolygonInfos);
            }

            if (!receiverObject)
            {
                // Receiver object is already dead.
                // So the process will be finished.
                OnFinished(State.LaunchingCanceled);
                yield break;
            }

            #region Prepare to run on worker threads.

            _convexPolygonInfos = CyDecalSystem.GetTrianglePolygonsFromPool(
                receiverObject);
            // Calculate bone matrix pallet.
            var boneMatricesPallet = CalculateMatricesPallet(skinnedMeshRenderers);

            var transform1 = transform;
            var projectorPosition = transform1.position;
            // basePosition is center of the decal box.
            var centerPositionOfDecalBox = projectorPosition + transform1.forward * (depth * 0.5f);

            for (var polyNo = 0; polyNo < _convexPolygonInfos.Count; polyNo++)
                _convexPolygonInfos[polyNo].ConvexPolygon.PrepareToRunOnWorkerThread();

            #endregion // Prepare to run on worker threads.

            #region Run worker thread.

            // Split Convex Polygon.
            _executeLaunchingOnWorkerThread = true;
            ThreadPool.QueueUserWorkItem(RunActionByWorkerThread, new Action(() =>
            {
                var localToWorldMatrices = new Matrix4x4[3];
                var boneWeights = new BoneWeight[3];
                for (var polyNo = 0; polyNo < _convexPolygonInfos.Count; polyNo++)
                    _convexPolygonInfos[polyNo].ConvexPolygon.CalculatePositionsAndNormalsInWorldSpace(
                        boneMatricesPallet, localToWorldMatrices, boneWeights);

                _broadPhaseConvexPolygonInfos = CyBroadPhaseConvexPolygonsDetection.Execute(
                    projectorPosition,
                    _decalSpace.Ez,
                    width,
                    height,
                    depth,
                    _convexPolygonInfos);

                BuildClipPlanes(centerPositionOfDecalBox);
                SplitConvexPolygonsByPlanes();
                AddTrianglePolygonsToDecalMeshFromConvexPolygons(centerPositionOfDecalBox);
                _executeLaunchingOnWorkerThread = false;
            }));

            #endregion // Run worker thread. 

            // Waiting to worker thread.
            while (_executeLaunchingOnWorkerThread) yield return null;

            foreach (var cyDecalMesh in DecalMeshes) cyDecalMesh.ExecutePostProcessingAfterWorkerThread();
            OnFinished(State.LaunchingCompleted);
            _convexPolygonInfos = null;

            yield return null;
        }

        /// <summary>
        ///     This function is called by worker thread.
        /// </summary>
        /// <param name="action"></param>
        private static void RunActionByWorkerThread(object action)
        {
            ((Action)action)();
        }

        /// <summary>
        ///     Create and add CyDecalProjector to the GameObject. 
        /// </summary>
        /// <param name="owner">Game object to which the component will be added.</param>
        /// <param name="receiverObject">Receiver object to which decal is applied.</param>
        /// <param name="decalMaterial">Decal material applied to receiver object.</param>
        /// <param name="width">Width of projector. It means to projection range.</param>
        /// <param name="height">Height of projector. It means to projection range.</param>
        /// <param name="depth">Depth of projector. It means to projection range.</param>
        /// <param name="launchOnAwake">
        ///     If it is true, the decal projection is started at same time as additional component.
        ///     If it is false, the decal projection is started by explicitly calling the Launch method.
        /// </param>
        /// <param name="onCompletedLaunch">Callback function called when decal projection is complete.</param>
        public static CyDecalProjector CreateAndLaunch(
            GameObject owner,
            GameObject receiverObject,
            Material decalMaterial,
            float width,
            float height,
            float depth,
            bool launchOnAwake,
            UnityAction<State> onCompletedLaunch)
        {
            var projector = owner.AddComponent<CyDecalProjector>();
            projector.width = width;
            projector.height = height;
            projector.depth = depth;
            projector.receiverObject = receiverObject;
            projector.decalMaterial = decalMaterial;
            projector.launchOnAwake = false;
            projector.onFinishedLaunch = new UnityEvent<State>();

            if (launchOnAwake) 
                projector.Launch(onCompletedLaunch);
            else if (onCompletedLaunch != null) projector.onFinishedLaunch.AddListener(onCompletedLaunch);

            return projector;
        }

        /// <summary>
        ///     Start projection decal.
        /// </summary>
        /// <remarks>
        ///     This processing is async, so the projection decal takes several frames to finish. 
        ///     If you want to monitor the decal projection process, should be using a callback function.
        /// </remarks>
        public void Launch(UnityAction<State> onFinishedLaunch)
        {
            if (NowState != State.NotLaunch)
                Debug.LogError("This function can be called only once, but it was called multiply.");

            NowState = State.Launching;
            if (onFinishedLaunch != null) this.onFinishedLaunch.AddListener(onFinishedLaunch);
            // Request the launching of the decal.
            CyDecalSystem.DecalProjectorLauncher.Request(
                this,
                () =>
                {
                    if (receiverObject)
                        StartCoroutine(ExecuteLaunch());
                    else
                        // Receiver object has been dead, so process is terminated.
                        OnFinished(State.LaunchingCanceled);
                });
        }
        
        private void InitializeOriginAxisInDecalSpace()
        {
            var trans = transform;
            _decalSpace = new CyDecalSpace(trans.right, trans.up, trans.forward * -1.0f);
        }
        
        private void AddTrianglePolygonsToDecalMeshFromConvexPolygons(Vector3 originPosInDecalSpace)
        {
            var convexPolygons = new List<CyConvexPolygon>();
            foreach (var convexPolyInfo in _broadPhaseConvexPolygonInfos)
            {
                if (convexPolyInfo.IsOutsideClipSpace) continue;

                convexPolygons.Add(convexPolyInfo.ConvexPolygon);
            }

            foreach (var cyDecalMesh in DecalMeshes)
                cyDecalMesh.AddTrianglePolygonsToDecalMesh(
                    convexPolygons,
                    originPosInDecalSpace,
                    _decalSpace.Ez,
                    _decalSpace.Ex,
                    _decalSpace.Ey,
                    width,
                    height);
        }
        
        private void SplitConvexPolygonsByPlanes()
        {
            // Convex polygons will be split by clip planes.
            foreach (var clipPlane in _clipPlanes)
            foreach (var convexPolyInfo in _broadPhaseConvexPolygonInfos)
            {
                // It is outside the clip planes, so skip it.
                if (convexPolyInfo.IsOutsideClipSpace) continue;

                convexPolyInfo.ConvexPolygon.SplitAndRemoveByPlane(
                    clipPlane, out var isOutsideClipSpace);
                convexPolyInfo.IsOutsideClipSpace = isOutsideClipSpace;
            }
        }
        
        private void BuildClipPlanes(Vector3 basePoint)
        {
            var basePointToNearClipDistance = depth * 0.5f;
            var basePointToFarClipDistance = depth * 0.5f;
            var decalSpaceTangentWs = _decalSpace.Ex;
            var decalSpaceBiNormalWs = _decalSpace.Ey;
            var decalSpaceNormalWs = _decalSpace.Ez;
            // Build left plane.
            _clipPlanes[(int)ClipPlane.Left] = new Vector4
            {
                x = decalSpaceTangentWs.x,
                y = decalSpaceTangentWs.y,
                z = decalSpaceTangentWs.z,
                w = width / 2.0f - Vector3.Dot(decalSpaceTangentWs, basePoint)
            };
            // Build right plane.
            _clipPlanes[(int)ClipPlane.Right] = new Vector4
            {
                x = -decalSpaceTangentWs.x,
                y = -decalSpaceTangentWs.y,
                z = -decalSpaceTangentWs.z,
                w = width / 2.0f + Vector3.Dot(decalSpaceTangentWs, basePoint)
            };
            // Build bottom plane.
            _clipPlanes[(int)ClipPlane.Bottom] = new Vector4
            {
                x = decalSpaceBiNormalWs.x,
                y = decalSpaceBiNormalWs.y,
                z = decalSpaceBiNormalWs.z,
                w = height / 2.0f - Vector3.Dot(decalSpaceBiNormalWs, basePoint)
            };
            // Build top plane.
            _clipPlanes[(int)ClipPlane.Top] = new Vector4
            {
                x = -decalSpaceBiNormalWs.x,
                y = -decalSpaceBiNormalWs.y,
                z = -decalSpaceBiNormalWs.z,
                w = height / 2.0f + Vector3.Dot(decalSpaceBiNormalWs, basePoint)
            };
            // Build front plane.
            _clipPlanes[(int)ClipPlane.Front] = new Vector4
            {
                x = -decalSpaceNormalWs.x,
                y = -decalSpaceNormalWs.y,
                z = -decalSpaceNormalWs.z,
                w = basePointToNearClipDistance + Vector3.Dot(decalSpaceNormalWs, basePoint)
            };
            // Build back plane.
            _clipPlanes[(int)ClipPlane.Back] = new Vector4
            {
                x = decalSpaceNormalWs.x,
                y = decalSpaceNormalWs.y,
                z = decalSpaceNormalWs.z,
                w = basePointToFarClipDistance - Vector3.Dot(decalSpaceNormalWs, basePoint)
            };
        }
        
        private enum ClipPlane
        {
            Left,
            Right,
            Bottom,
            Top,
            Front,
            Back,
            Num
        }
    }
}
