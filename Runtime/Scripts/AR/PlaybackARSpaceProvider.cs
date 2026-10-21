#if HAS_AR_FOUNDATION
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using IV.FormulaTracker.Recording;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Playback-side provider. A recording carries the camera trajectory of the AR session
	/// it was taken in, and this provider reports that session as tracking. Any anchor
	/// history an older recording carries is not read.
	/// </summary>
	internal class PlaybackARSpaceProvider : IARSpaceProvider
	{
		private readonly SequenceReader _reader;

		public PlaybackARSpaceProvider(SequenceReader reader)
		{
			_reader = reader;
		}

		// IARSpaceProvider implementation -----------------------------------

		public bool Detect() => true;
		public bool IsAvailable => _reader != null && _reader.HasExtrinsics;
		public bool IsReliable => IsAvailable;
		public ARSessionState SessionState => ARSessionState.SessionTracking;
		public NotTrackingReason NotTrackingReason => NotTrackingReason.None;

		public bool TryGetSessionSpace(
			out Matrix4x4 worldFromSession,
			out string identity,
			out int epoch)
		{
			worldFromSession = Matrix4x4.identity;
			identity = _reader?.RecordingIdentity;
			if (string.IsNullOrEmpty(identity))
				identity = _reader?.SequenceId ?? string.Empty;
			epoch = 0;
			return IsAvailable && !string.IsNullOrEmpty(identity);
		}
	}
}
#endif
