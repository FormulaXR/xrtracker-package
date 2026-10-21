#if HAS_AR_FOUNDATION
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace IV.FormulaTracker
{
	/// <summary>
	/// AR session state source used by <see cref="ARPoseFusion"/>.
	/// Two implementations:
	/// - <c>ARFoundationSpaceProvider</c>: live ARFoundation on device
	/// - <c>PlaybackARSpaceProvider</c>: recorded session replayed on desktop
	/// Immutable frame-context resolution consumes this provider without
	/// depending on whether the observations came from live AR or playback.
	/// </summary>
	internal interface IARSpaceProvider
	{
		/// <summary>Discover dependencies (ARSession etc.) if applicable. Returns availability.</summary>
		bool Detect();

		/// <summary>AR session is wired up and enabled.</summary>
		bool IsAvailable { get; }

		/// <summary>AR session is currently tracking properly (no SLAM failure).</summary>
		bool IsReliable { get; }

		/// <summary>For logging/debug only.</summary>
		ARSessionState SessionState { get; }
		NotTrackingReason NotTrackingReason { get; }

		/// <summary>
		/// Current Unity-world transform of the AR session's tracking space.
		/// </summary>
		/// <remarks>
		/// Live ARFoundation obtains it from the XR Origin's trackables parent. Playback
		/// recordings already express camera poses in their stable recorded session frame
		/// and therefore return identity. A caller that needs stationary continuity must
		/// cache in this space.
		/// </remarks>
		bool TryGetSessionSpace(
			out Matrix4x4 worldFromSession,
			out string identity,
			out int epoch);
	}
}
#endif
