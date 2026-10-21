namespace IV.FormulaTracker
{
	/// <summary>
	/// What a body is doing about the world-frame coast on one frame.
	/// </summary>
	internal enum WorldFrameCoastPhase
	{
		/// <summary>Not coasting and owing no re-entry write.</summary>
		Inactive = 0,

		/// <summary>Held in the world frame because it is out of view.</summary>
		Coasting = 1,

		/// <summary>
		/// Back in view and owing the tracker one seed from the held pose. Stays
		/// here until that write succeeds.
		/// </summary>
		ReentryPending = 2,
	}

	/// <summary>
	/// What the caller must do for a body this frame.
	/// </summary>
	internal readonly struct WorldFrameCoastDecision
	{
		internal WorldFrameCoastDecision(WorldFrameCoastPhase phase,
			bool holdInWorldFrame, bool seedTracker, bool suppressStore)
		{
			Phase = phase;
			HoldInWorldFrame = holdInWorldFrame;
			SeedTracker = seedTracker;
			SuppressStore = suppressStore;
		}

		/// <summary>The phase this frame leaves the body in.</summary>
		public WorldFrameCoastPhase Phase { get; }

		/// <summary>
		/// Hold the body in the world frame: spend no part of the loss budget,
		/// attempt no correction, and tag the displayed pose as coasted.
		/// </summary>
		public bool HoldInWorldFrame { get; }

		/// <summary>Attempt the one gated write that hands the held pose over.</summary>
		public bool SeedTracker { get; }

		/// <summary>
		/// This frame's solve must not update the stored reference. It ran before
		/// the seed reached the tracker, so it is still a solve from the pose the
		/// tracker froze at, not from the pose the body is actually at.
		/// </summary>
		public bool SuppressStore { get; }
	}

	/// <summary>
	/// The visibility-split coast, as a transition with no engine or tracker
	/// dependencies.
	/// </summary>
	/// <remarks>
	/// <para>Out of view is not a tracking failure. The evidence is absent because
	/// the camera is pointed elsewhere, the AR session still knows where the world
	/// is, and the composition already holds the body there — so the coast spends
	/// none of the budget the failure path spends and writes nothing to the tracker
	/// until the body comes back. Failing while in view is a different situation
	/// and keeps the behaviour it already had.</para>
	/// <para>Every input is an existing online state. Nothing here is a threshold,
	/// and nothing here is a timer.</para>
	/// </remarks>
	internal static class WorldFrameCoast
	{
		/// <summary>
		/// Decides what a body does this frame and advances its phase.
		/// </summary>
		/// <param name="phase">The body's phase, updated in place.</param>
		/// <param name="coastEnabled">Whether the coast is enabled at all.</param>
		/// <param name="worldPoseHealthy">
		/// Whether the AR session's own state says its world pose can be trusted
		/// this frame. An unlimited coast rests entirely on this being true.
		/// </param>
		/// <param name="visibilityMeasured">
		/// Whether the visibility predicate could be evaluated at all. A frame that
		/// could not measure it cannot justify holding, so it ends the coast.
		/// </param>
		/// <param name="shouldBeVisible">
		/// Whether any part of the body could be inside the view.
		/// </param>
		public static WorldFrameCoastDecision Decide(
			ref WorldFrameCoastPhase phase,
			bool coastEnabled,
			bool worldPoseHealthy,
			bool visibilityMeasured,
			bool shouldBeVisible)
		{
			// Disabled, or the world pose is no longer trustworthy: the coast is
			// abandoned and the body falls back to the behaviour it has without one.
			// A pending seed is abandoned with it — the held pose is expressed in a
			// world frame the session has stopped vouching for, so handing it to the
			// tracker would be worse than leaving the tracker where it is.
			if (!coastEnabled || !worldPoseHealthy)
				return Settle(ref phase, WorldFrameCoastPhase.Inactive);

			// The predicate could not be evaluated. Holding without being able to
			// say the body is out of view would be patience bought on no evidence.
			if (!visibilityMeasured)
				return Settle(ref phase, WorldFrameCoastPhase.Inactive);

			if (!shouldBeVisible)
			{
				// Out of view and the world pose is healthy. Any seed still owed from
				// an earlier return is moot: the body left again before the tracker
				// took it, and the pose owed on the next return is the one held now.
				phase = WorldFrameCoastPhase.Coasting;
				return new WorldFrameCoastDecision(phase,
					holdInWorldFrame: true, seedTracker: false, suppressStore: false);
			}

			// In view. A body that was coasting now owes one seed, and keeps owing it
			// until a write actually lands.
			if (phase == WorldFrameCoastPhase.Coasting)
				phase = WorldFrameCoastPhase.ReentryPending;

			if (phase == WorldFrameCoastPhase.ReentryPending)
			{
				return new WorldFrameCoastDecision(phase,
					holdInWorldFrame: false, seedTracker: true, suppressStore: true);
			}

			return new WorldFrameCoastDecision(phase,
				holdInWorldFrame: false, seedTracker: false, suppressStore: false);
		}

		/// <summary>
		/// Records the outcome of the write the decision asked for.
		/// </summary>
		/// <remarks>
		/// Only a write the tracker accepted discharges the debt. A refusal — the
		/// transient busy answer above all — leaves the body owing the same seed, so
		/// the next frame asks again. Counting an attempt rather than a success
		/// would report a re-entry that never happened.
		/// </remarks>
		/// <returns>True when this write discharged the seed.</returns>
		public static bool RecordSeedResult(ref WorldFrameCoastPhase phase, int nativeStatus)
		{
			if (phase != WorldFrameCoastPhase.ReentryPending)
				return false;
			if (nativeStatus != FTErrorCode.OK)
				return false;

			phase = WorldFrameCoastPhase.Inactive;
			return true;
		}

		/// <summary>
		/// Clears a body's coast phase. Used where the tracker's own state is
		/// replaced or re-expressed, so nothing is left owing against a pose that no
		/// longer means what it meant.
		/// </summary>
		public static void Clear(ref WorldFrameCoastPhase phase)
		{
			phase = WorldFrameCoastPhase.Inactive;
		}

		private static WorldFrameCoastDecision Settle(
			ref WorldFrameCoastPhase phase, WorldFrameCoastPhase next)
		{
			phase = next;
			return new WorldFrameCoastDecision(phase,
				holdInWorldFrame: false, seedTracker: false, suppressStore: false);
		}
	}
}
