using System.Threading;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Records whether every native write a frame depends on actually landed.
	///
	/// The native side refuses a write when a tracking step or a probe holds the
	/// shared objects on another thread, and returns <see cref="FTErrorCode.BUSY"/>
	/// rather than pretending to succeed. A refusal nobody notices is worse than a
	/// race: the frame goes on to submit its image and step over it, so the tracker
	/// solves a new image against the previous frame's camera pose, or against a
	/// body pose that was never updated. Every number after that is plausible and
	/// wrong.
	///
	/// The guard accumulates rather than being checked per call, for the same
	/// reason ownership is taken rather than asked about: a list of call sites each
	/// remembering to return false is correct only until the next write is added.
	/// Any failure in the open decision drops that decision's step, which is one
	/// frame of latency in the worst case and no wrong poses in any case.
	/// </summary>
	internal static class FrameWriteGuard
	{
		private static readonly object Sync = new object();
		private static bool _failed;
		private static bool _frameOpen;
		private static int _ownerThreadId;

		/// <summary>
		/// Opens the native-write decision for one submitted frame. The caller owns
		/// the native-access lock and must close it through <see cref="ConsumeFailure"/>
		/// before releasing that lock. All current call paths do this on Unity's main
		/// thread; the recorded owner prevents a later or worker-thread write from
		/// being charged to this frame.
		/// </summary>
		internal static bool BeginFrame()
		{
			lock (Sync)
			{
				if (_frameOpen)
				{
					_failed = true;
					return false;
				}
				_frameOpen = true;
				_ownerThreadId = Thread.CurrentThread.ManagedThreadId;
				_failed = false;
				return true;
			}
		}

		/// <summary>
		/// Opens a frame only when the feeder did not already do so. Sequence
		/// capture has no managed feed call, while injected capture opens the frame
		/// before FT_FeedFrame so that a refused feed belongs to this same decision.
		/// </summary>
		internal static bool BeginFrameIfNeeded()
		{
			lock (Sync)
			{
				if (_frameOpen)
				{
					if (_ownerThreadId == Thread.CurrentThread.ManagedThreadId)
						return true;
					_failed = true;
					return false;
				}
				_frameOpen = true;
				_ownerThreadId = Thread.CurrentThread.ManagedThreadId;
				_failed = false;
				return true;
			}
		}

		/// <summary>
		/// Records a native write's result. Returns true when the write landed.
		/// </summary>
		internal static bool Record(int result, string call)
		{
			bool ownedFrame;
			bool openFrame;
			lock (Sync)
			{
				openFrame = _frameOpen;
				ownedFrame = _frameOpen &&
					_ownerThreadId == Thread.CurrentThread.ManagedThreadId;
				if (!ownedFrame)
				{
					if (_frameOpen)
						_failed = true;
				}
				else if (result != FTErrorCode.OK)
					_failed = true;
			}
			if (ownedFrame && result == FTErrorCode.OK)
				return true;

			if (!ownedFrame)
			{
				Debug.LogWarning(openFrame
					? $"{call} failed with {result} outside its owning frame; the open decision will be dropped."
					: $"{call} failed with {result} outside a frame decision.");
			}
			else if (result != FTErrorCode.BUSY)
			{
				Debug.LogWarning($"{call} failed with {result}; the frame will be dropped.");
			}
			return false;
		}

		/// <summary>
		/// Records a native write that is deliberately outside a tracking-frame
		/// decision, such as setup or a post-step correction. It must never poison
		/// a later frame; its caller owns retry or lifecycle handling.
		/// </summary>
		internal static bool RecordOutOfFrame(int result, string call)
		{
			if (result == FTErrorCode.OK)
				return true;
			Debug.LogWarning($"{call} failed with {result} outside a frame decision.");
			return false;
		}

		/// <summary>
		/// Returns whether any required write has failed since the last call, and
		/// clears the record. Called once per frame, where the decision to step is
		/// taken.
		/// </summary>
		internal static bool ConsumeFailure()
		{
			lock (Sync)
			{
				if (!_frameOpen)
					return true;
				if (_ownerThreadId != Thread.CurrentThread.ManagedThreadId)
				{
					_failed = true;
					return true;
				}
				bool failed = _failed;
				_failed = false;
				_frameOpen = false;
				_ownerThreadId = 0;
				return failed;
			}
		}

		/// <summary>
		/// Closes an owned attempt after an exception or an explicit cancellation.
		/// Cancellation is a failed decision: it may never be replaced silently.
		/// </summary>
		internal static bool AbortFrame()
		{
			lock (Sync)
			{
				if (!_frameOpen)
					return true;
				if (_ownerThreadId != Thread.CurrentThread.ManagedThreadId)
				{
					_failed = true;
					return false;
				}
				_failed = true;
				_frameOpen = false;
				_ownerThreadId = 0;
				return true;
			}
		}

		/// <summary>
		/// Drops any recorded failure without acting on it. For teardown and for
		/// tests, which must not inherit a previous fixture's state.
		/// </summary>
		internal static void Clear()
		{
			lock (Sync)
			{
				_failed = false;
				_frameOpen = false;
				_ownerThreadId = 0;
			}
		}
	}
}
