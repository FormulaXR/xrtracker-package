#if HAS_AR_FOUNDATION
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.XR.CoreUtils;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Live ARFoundation-backed provider. Wraps ARSession and the XR Origin and exposes the
	/// session state and the session space behind IARSpaceProvider. It creates no anchor.
	/// </summary>
	internal class ARFoundationSpaceProvider : IARSpaceProvider
	{
		private ARSession _arSession;
		private XROrigin _xrOrigin;

		private int _sessionEpoch;
		private ARSessionState _lastSessionState;
		private bool _hasLastSessionState;

		public bool IsAvailable { get; private set; }
		public bool IsReliable => IsAvailable && ARSession.state == ARSessionState.SessionTracking && ARSession.notTrackingReason == NotTrackingReason.None;
		public ARSessionState SessionState => ARSession.state;
		public NotTrackingReason NotTrackingReason => ARSession.notTrackingReason;

		public bool TryGetSessionSpace(
			out Matrix4x4 worldFromSession,
			out string identity,
			out int epoch)
		{
			worldFromSession = Matrix4x4.identity;
			identity = string.Empty;
			epoch = _sessionEpoch;
			if (!IsAvailable || _arSession == null || _xrOrigin == null ||
				_xrOrigin.TrackablesParent == null)
				return false;

			ARSessionState state = ARSession.state;
			if (_hasLastSessionState && state == ARSessionState.SessionInitializing &&
				_lastSessionState != ARSessionState.SessionInitializing)
			{
				++_sessionEpoch;
			}
			_lastSessionState = state;
			_hasLastSessionState = true;

			Transform trackables = _xrOrigin.TrackablesParent;
			worldFromSession = trackables.localToWorldMatrix;
			identity = $"ar-session:{_arSession.GetInstanceID()}:" +
				$"trackables:{trackables.GetInstanceID()}";
			epoch = _sessionEpoch;
			return ConversionUtils.IsUsablePose(
				worldFromSession.GetPosition(), worldFromSession.rotation);
		}

		public bool Detect()
		{
			_arSession = Object.FindAnyObjectByType<ARSession>();
			IsAvailable = _arSession != null && _arSession.enabled;
			_xrOrigin = Object.FindAnyObjectByType<XROrigin>();
			return IsAvailable;
		}
	}
}
#endif
