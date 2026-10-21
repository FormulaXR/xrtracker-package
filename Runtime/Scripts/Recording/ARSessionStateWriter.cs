#if HAS_AR_FOUNDATION
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using AOT;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace IV.FormulaTracker.Recording
{
	/// <summary>
	/// Records the AR session state per recorded frame, so a device capture can be checked
	/// to have an AR state for every frame it holds. The file keeps its older name,
	/// anchor_history.jsonl, because the capture tools read it by that name. It carries no
	/// anchor, and playback does not read it.
	/// Snapshots are taken on the Unity main thread. JSONL lines are written from the C++
	/// recorder's writer thread via FT_SetFrameRecordedCallback, ensuring the line index
	/// pairs exactly with the recorded color frame index.
	/// </summary>
	internal static class ARSessionStateWriter
	{
		// Snapshots produced on main thread, consumed on writer thread.
		private struct Snapshot
		{
			public ARSessionState SessionState;
			public NotTrackingReason NotTrackingReason;
		}

		private static readonly ConcurrentQueue<Snapshot> s_pending = new();
		private static readonly object s_writerLock = new();

		// Keep the delegate alive (GC pinning) for the lifetime of the subscription.
		private static FTBridge.FT_FrameRecordedCallback s_callback;
		private static StreamWriter s_writer;
		private static bool s_active;

		/// <summary>True while recording is active and the writer is open.</summary>
		public static bool IsActive => s_active;

		/// <summary>
		/// Open anchor_history.jsonl in the recording dir and subscribe to the
		/// frame-committed callback. Called when FT_StartRecording is invoked.
		/// </summary>
		public static void Start(string outputDir)
		{
			if (s_active)
				Stop();

			try
			{
				Directory.CreateDirectory(outputDir);
				var path = Path.Combine(outputDir, "anchor_history.jsonl");
				s_writer = new StreamWriter(path, append: false) { AutoFlush = true };
			}
			catch (Exception e)
			{
				Debug.LogError($"[ARSessionStateWriter] Failed to open file: {e.Message}");
				s_writer = null;
				return;
			}

			while (s_pending.TryDequeue(out _)) { }

			s_callback = OnFrameRecorded;
			FTBridge.FT_SetFrameRecordedCallback(s_callback);

			s_active = true;
		}

		/// <summary>
		/// Unsubscribe, drain remaining snapshots (best-effort), close the file.
		/// Called when FT_StopRecording is invoked.
		/// </summary>
		public static void Stop()
		{
			if (!s_active)
				return;

			s_active = false;
			FTBridge.FT_SetFrameRecordedCallback(null);
			s_callback = null;

			lock (s_writerLock)
			{
				try { s_writer?.Flush(); }
				catch (Exception e) { Debug.LogWarning($"[ARSessionStateWriter] Flush failed: {e.Message}"); }
				try { s_writer?.Dispose(); }
				catch (Exception e) { Debug.LogWarning($"[ARSessionStateWriter] Dispose failed: {e.Message}"); }
				s_writer = null;
			}

			while (s_pending.TryDequeue(out _)) { }
		}

		/// <summary>
		/// Snapshot the AR session state for the current main-thread frame.
		/// Must be called once per FT_FeedFrame on the Unity main thread —
		/// XRTrackerManager.BeforeTrackingStep is the canonical site.
		/// </summary>
		public static void CaptureSnapshot()
		{
			if (!s_active) return;

			s_pending.Enqueue(new Snapshot
			{
				SessionState = ARSession.state,
				NotTrackingReason = ARSession.notTrackingReason,
			});
		}

		// Fires on the C++ recorder's writer thread — must not touch Unity API.
		[MonoPInvokeCallback(typeof(FTBridge.FT_FrameRecordedCallback))]
		private static void OnFrameRecorded(int frameIndex, long timestampNs)
		{
			if (!s_pending.TryDequeue(out var snap))
			{
				// Out of sync — more frames committed than snapshots taken.
				// Emit a placeholder so line index == frame index.
				WriteLine(frameIndex, timestampNs, default, missing: true);
				return;
			}
			WriteLine(frameIndex, timestampNs, snap, missing: false);
		}

		private static void WriteLine(int frameIndex, long ts, Snapshot s, bool missing)
		{
			lock (s_writerLock)
			{
				var w = s_writer;
				if (w == null) return;
				try
				{
					var ci = CultureInfo.InvariantCulture;
					w.Write("{\"schema_version\":3,\"frame\":");
					w.Write(frameIndex.ToString(ci));
					w.Write(",\"ts_ns\":");
					w.Write(ts.ToString(ci));
					w.Write(",\"missing\":");
					w.Write(missing ? "1" : "0");
					w.Write(",\"session_state\":");
					w.Write(((int)s.SessionState).ToString(ci));
					w.Write(",\"not_tracking_reason\":");
					w.Write(((int)s.NotTrackingReason).ToString(ci));
					w.Write(",\"session_tracking\":");
					w.Write(s.SessionState == ARSessionState.SessionTracking ? "1" : "0");
					w.Write("}\n");
				}
				catch (Exception e)
				{
					Debug.LogWarning($"[ARSessionStateWriter] Write failed at frame {frameIndex}: {e.Message}");
				}
			}
		}
	}
}
#endif
