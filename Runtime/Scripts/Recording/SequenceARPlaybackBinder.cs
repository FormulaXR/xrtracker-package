#if HAS_AR_FOUNDATION
using System;
using UnityEngine;

namespace IV.FormulaTracker.Recording
{
	/// <summary>
	/// Wires AR pose fusion to recorded data for desktop replay. When sequence
	/// mode starts, this binder builds a SequenceReader, swaps the playback
	/// provider into the active ARPoseFusion. XRTrackerManager requests the
	/// upcoming frame observation before coordinate resolution; this binder never
	/// submits a native camera pose.
	/// On sequence shutdown the live provider is restored.
	/// </summary>
	internal static class SequenceARPlaybackBinder
	{
		private static SequenceReader s_reader;
		private static PlaybackARSpaceProvider s_provider;
		private static IARSpaceProvider s_previousProvider;
		private static ARPoseFusion s_fusion;
		private static RecordedFusionFrameObservationSource s_observationSource;
		private static bool s_bound;

		public static SequenceReader Reader => s_reader;
		public static bool IsBound => s_bound;

		/// <summary>
		/// Try to bind playback AR data to the active ARPoseFusion. Called from
		/// XRTrackerManager after sequence mode finishes initializing. Safe to
		/// call when the recording has no extrinsics data — it will simply
		/// no-op and leave the live provider in place.
		/// </summary>
		public static void TryBind(string resolvedSequenceDirectory)
		{
			if (s_bound) Unbind();

			if (string.IsNullOrEmpty(resolvedSequenceDirectory)) return;
			var manager = TrackedBodyManager.Instance;
			if (manager == null) return;

			var fusion = manager.ARPoseFusion;
			if (fusion == null) return;

			s_reader = new SequenceReader(resolvedSequenceDirectory);
			if (!s_reader.IsValid)
			{
				s_reader = null;
				return;
			}

			// Only bind if the recording has a camera trajectory to replay; otherwise stay
			// on the live provider (which is a no-op without ARFoundation).
			if (!s_reader.HasExtrinsics)
			{
				return;
			}

			s_provider = new PlaybackARSpaceProvider(s_reader);
			s_observationSource = new RecordedFusionFrameObservationSource(s_reader, s_provider);
			s_fusion = fusion;
			s_previousProvider = fusion.CurrentProvider; // remember to restore on Unbind

			fusion.SetProvider(s_provider);
			s_bound = true;
			Debug.Log("[SequenceARPlaybackBinder] Bound playback AR provider (extrinsics=True)");
		}

		public static void Unbind()
		{
			if (s_bound && s_fusion != null && s_previousProvider != null)
			{
				// Restore the live provider that was running before we swapped in.
				s_fusion.SetProvider(s_previousProvider);
			}
			s_bound = false;
			s_observationSource = null;
			s_provider = null;
			s_previousProvider = null;
			s_fusion = null;
			s_reader = null;
		}

		public static bool TryGetObservation(
			int expectedFrameIndex,
			long timestampNs,
			out FusionFrameObservation observation)
		{
			if (!s_bound || s_observationSource == null)
			{
				observation = default;
				return false;
			}
			return s_observationSource.TryGetObservation(
				expectedFrameIndex,
				timestampNs,
				out observation);
		}
	}
}
#endif
