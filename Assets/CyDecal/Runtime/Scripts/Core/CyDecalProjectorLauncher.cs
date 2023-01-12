using System;
using System.Collections.Generic;

namespace CyDecal.Runtime.Scripts.Core
{
    internal interface ICyDecalProjectorLauncher
    {
        void Update();
    }

    /// <summary>
    ///     Launcher of decal projector.
    /// </summary>
    /// <remarks>
    ///     The decal projector is activated via this class.
    ///     The decal projector is queued in the queue and it is launched at the appropriate time.
    /// </remarks>
    public sealed class CyDecalProjectorLauncher : ICyDecalProjectorLauncher
    {
        private readonly Queue<LaunchRequest> _launchRequestQueues = new Queue<LaunchRequest>();
        private LaunchRequest _currentRequest;

        void ICyDecalProjectorLauncher.Update()
        {
            if (!IsCurrentRequestIsFinished())
                // The current request is still running, so returned.
                return;

            ProcessNextRequest();
        }

        /// <summary>
        ///     Queueing startup requests to the queue.
        /// </summary>
        public void Request(CyDecalProjector projector, Action onLaunch)
        {
            _launchRequestQueues.Enqueue(new LaunchRequest(projector, onLaunch));
        }

        private bool IsCurrentRequestIsFinished()
        {
            return _currentRequest == null // The request is empty.
                   || !_currentRequest.Projector // Projector that threw the request is dead.
                   || _currentRequest.Projector.NowState ==
                   CyDecalProjector.State.LaunchingCompleted; // Launching is completed.
        }

        private void ProcessNextRequest()
        {
            while (_launchRequestQueues.Count > 0)
            {
                _currentRequest = _launchRequestQueues.Peek();
                _launchRequestQueues.Dequeue();
                if (!_currentRequest.Projector) continue; // This request was dead, so skipped.
                _currentRequest.OnLaunch();
                break;
            }
        }

        public int GetWaitingRequestCount()
        {
            return _launchRequestQueues.Count;
        }

        private class LaunchRequest
        {
            public LaunchRequest(CyDecalProjector projector, Action onLaunch)
            {
                Projector = projector;
                OnLaunch = onLaunch;
            }

            public CyDecalProjector Projector { get; }
            public Action OnLaunch { get; }
        }
    }
}
