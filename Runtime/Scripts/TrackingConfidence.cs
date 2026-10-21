namespace IV.FormulaTracker
{
	/// <summary>
	/// How much of a body's displayed pose the tracker can currently vouch for.
	/// </summary>
	/// <remarks>
	/// <para>Three states, and they answer a question the switch of the pipeline
	/// does not. <see cref="TrackedBody.IsTracking"/> says whether the solver is
	/// running, and it stays true while a pose is held. It does not separate a dip
	/// the tracker is about to recover from, a body the camera is simply not pointed
	/// at, and a body the tracker has actually lost, and an application has to treat
	/// those three differently.</para>
	/// <para>The state reads the same score and the same start, confirm and stop
	/// bars that decide <see cref="TrackedBody.IsTracking"/>.</para>
	/// <para><see cref="Lost"/> is reported, never acted on. On the stationary AR
	/// path the body keeps holding the last world pose whatever this says; the
	/// tracker does not reset itself, because a reset would throw away a world
	/// registration the session may still recover. What the application does about
	/// a lost body is the application's decision, and this is how it learns it has
	/// one to make.</para>
	/// </remarks>
	public enum TrackingConfidence
	{
		/// <summary>
		/// The tracker cannot vouch for the pose. Either it has not found the body
		/// yet, or the evidence has been missing for long enough that what is
		/// displayed is no longer supported by anything the camera has seen.
		/// </summary>
		Lost = 0,

		/// <summary>
		/// The pose is still worth displaying, but this frame did not verify it.
		/// The augmentation stays up and the full pose is still produced; what is
		/// reduced is confidence, not output.
		/// </summary>
		Degraded = 1,

		/// <summary>
		/// This frame's solve was accepted. The displayed pose is what the tracker
		/// currently sees.
		/// </summary>
		Tracked = 2,
	}

	/// <summary>
	/// Why a body is in the state it is in.
	/// </summary>
	/// <remarks>
	/// The reason is what makes <see cref="TrackingConfidence.Degraded"/> useful
	/// to an application: a body held because the camera is pointed elsewhere and
	/// a body held because the tracker cannot recognise it are the same state and
	/// call for opposite responses.
	/// </remarks>
	public enum TrackingConfidenceReason
	{
		/// <summary>This frame's solve was accepted.</summary>
		Accepted = 0,

		/// <summary>
		/// The solver is not running on this body. It has not been found yet, or it
		/// was reset.
		/// </summary>
		NotStarted = 1,

		/// <summary>
		/// The body is out of the camera's view and is being held in the world
		/// frame. No evidence is expected, so none is missing.
		/// </summary>
		OutOfView = 2,

		/// <summary>
		/// Inside the settling window that follows a start, during which the solve
		/// is still converging.
		/// </summary>
		Settling = 3,

		/// <summary>
		/// The solver is running and this frame's output was not accepted, but the
		/// holding bar is still being cleared.
		/// </summary>
		Unverified = 4,

		/// <summary>
		/// The holding bar has been missed on the declared count of consecutive
		/// frames.
		/// </summary>
		BelowHold = 5,

		/// <summary>
		/// The body is lost and has not yet cleared the entry bar for the declared
		/// count of consecutive frames.
		/// </summary>
		BelowAcquire = 6,

		/// <summary>
		/// A stationary body under AR has an accepted fit, but the pose on screen has not yet come within the still distance of the settle pull
		/// (1.5 pixels, measured on screen) of that fit. The drawn pose is still easing onto the fit from the guide, or from the pose the body was
		/// held at, so the pose is not yet vouched for as shown.
		/// </summary>
		DisplayCatchingUp = 7,
	}

	/// <summary>
	/// What the state machine carries from one frame to the next.
	/// </summary>
	internal struct TrackingConfidenceMemory
	{
		/// <summary>The state the last decision left the body in.</summary>
		public TrackingConfidence State { get; internal set; }

		/// <summary>Consecutive frames the holding bar has been missed on.</summary>
		public int BelowHoldFrames { get; internal set; }

		/// <summary>
		/// Consecutive frames the entry bar has been cleared on since the body was
		/// last lost.
		/// </summary>
		public int AboveAcquireFrames { get; internal set; }
	}

	/// <summary>
	/// One frame's outcome: the state, what it was before, and why.
	/// </summary>
	internal readonly struct TrackingConfidenceTransition
	{
		internal TrackingConfidenceTransition(TrackingConfidence previous,
			TrackingConfidence state, TrackingConfidenceReason reason)
		{
			Previous = previous;
			State = state;
			Reason = reason;
		}

		/// <summary>The state this frame leaves the body in.</summary>
		public TrackingConfidence State { get; }

		/// <summary>The state the previous frame left it in.</summary>
		public TrackingConfidence Previous { get; }

		/// <summary>Why the body is in <see cref="State"/>.</summary>
		public TrackingConfidenceReason Reason { get; }

		/// <summary>
		/// Whether this frame crossed an edge. The only frame on which a consumer
		/// should be told anything.
		/// </summary>
		public bool Changed => State != Previous;
	}

	/// <summary>
	/// The three-state machine, as a transition with no engine or tracker
	/// dependencies.
	/// </summary>
	/// <remarks>
	/// <para>Every input is an existing online quantity: whether the solver is
	/// running, whether this frame's solve was accepted, whether the body is being
	/// held out of view, the two quality scalars, and the band and frame counts
	/// those scalars are already read against. Nothing here is a timer and nothing
	/// here is a number this file chose.</para>
	/// <para>The transitions are all in <see cref="Decide"/>, in the order they are
	/// tested. That order is the specification: an earlier branch wins, and the
	/// ordering carries three claims worth stating.</para>
	/// <list type="number">
	/// <item>A body whose solver is not running is lost, whatever else is true.</item>
	/// <item>A start clears a loss. The settling window that follows a start is
	/// reached before the sticky-loss branch, so a body that has just cleared the
	/// entry bar to start is not made to clear it again.</item>
	/// <item>Looking away does not clear a loss. The out-of-view branch sits after
	/// the sticky-loss branch, so a body lost while in view stays lost when the
	/// camera turns away, and its progress towards re-acquisition is frozen rather
	/// than granted.</item>
	/// </list>
	/// </remarks>
	internal static class TrackingConfidenceMachine
	{
		/// <summary>
		/// Decides a body's state for this frame and advances its memory.
		/// </summary>
		/// <param name="memory">The body's counters, updated in place.</param>
		/// <param name="isTracking">Whether the solver is running on this body.</param>
		/// <param name="settling">
		/// Whether the body is inside the settling window that follows a start.
		/// </param>
		/// <param name="poseAccepted">
		/// Whether this frame's solve became the body's pose.
		/// </param>
		/// <param name="heldOutOfView">
		/// Whether the body is being held in the world frame because it is outside
		/// the camera's view.
		/// </param>
		/// <param name="acquireSignal">
		/// The scalar the entry decision reads, per
		/// <see cref="QualityBand.SignalFor"/>.
		/// </param>
		/// <param name="holdSignal">The scalar the holding decision reads.</param>
		/// <param name="band">The thresholds those scalars are read against.</param>
		/// <param name="framesToLose">
		/// Consecutive below-hold frames that make a loss.
		/// </param>
		/// <param name="framesToAcquire">
		/// Consecutive above-acquire frames that end one.
		/// </param>
		/// <param name="confirmBar">
		/// The score the frame that completes those consecutive frames must also reach, as the
		/// last frame of a start must. Negative infinity asks for nothing more than the entry bar.
		/// </param>
		public static TrackingConfidenceTransition Decide(
			ref TrackingConfidenceMemory memory,
			bool isTracking,
			bool settling,
			bool poseAccepted,
			bool heldOutOfView,
			float acquireSignal,
			float holdSignal,
			QualityBand band,
			int framesToLose,
			int framesToAcquire,
			float confirmBar = float.NegativeInfinity)
		{
			TrackingConfidence previous = memory.State;

			// No solve is running. This is the detection phase, and the state a
			// manual reset leaves behind.
			if (!isTracking)
			{
				memory.BelowHoldFrames = 0;
				memory.AboveAcquireFrames = 0;
				return Settle(ref memory, previous,
					TrackingConfidence.Lost, TrackingConfidenceReason.NotStarted);
			}

			// The settling window after a start. The body cleared the entry bar to
			// get here, so this is where a loss ends; what it has not yet done is
			// converge, so it is not yet vouched for either.
			if (settling)
			{
				memory.BelowHoldFrames = 0;
				memory.AboveAcquireFrames = 0;
				return Settle(ref memory, previous,
					TrackingConfidence.Degraded, TrackingConfidenceReason.Settling);
			}

			// Already lost. Leaving takes the entry bar, held for the declared count,
			// because the entry bar is at or above the holding bar by construction: a
			// body that only clears the holding bar has not shown enough to be
			// re-acquired on.
			if (previous == TrackingConfidence.Lost)
			{
				memory.BelowHoldFrames = 0;

				if (heldOutOfView)
				{
					// No evidence is arriving, so none can re-acquire. The count is
					// frozen rather than cleared: looking away neither earns progress
					// towards recovery nor undoes progress already earned.
					return Settle(ref memory, previous,
						TrackingConfidence.Lost, TrackingConfidenceReason.OutOfView);
				}

				if (!band.Clears(QualityDecision.Acquire, acquireSignal))
				{
					memory.AboveAcquireFrames = 0;
					return Settle(ref memory, previous,
						TrackingConfidence.Lost, TrackingConfidenceReason.BelowAcquire);
				}

				if (memory.AboveAcquireFrames < int.MaxValue)
					++memory.AboveAcquireFrames;

				if (memory.AboveAcquireFrames < framesToAcquire)
				{
					return Settle(ref memory, previous,
						TrackingConfidence.Lost, TrackingConfidenceReason.BelowAcquire);
				}

				// The last frame of the run has a bar of its own, as the last frame of a start does. A
				// frame that misses it leaves the count where it is, so the next frame that clears it ends the loss.
				if (!(acquireSignal >= confirmBar))
				{
					return Settle(ref memory, previous,
						TrackingConfidence.Lost, TrackingConfidenceReason.BelowAcquire);
				}

				memory.AboveAcquireFrames = 0;
				return Settle(ref memory, previous,
					poseAccepted ? TrackingConfidence.Tracked : TrackingConfidence.Degraded,
					poseAccepted ? TrackingConfidenceReason.Accepted
						: TrackingConfidenceReason.Unverified);
			}

			// Out of view and held in the world frame. The evidence is absent because
			// the camera is pointed elsewhere, which is not a failure to see the body;
			// the holding count is frozen so none of the loss budget is spent, and
			// none of it is refunded either.
			if (heldOutOfView)
			{
				return Settle(ref memory, previous,
					TrackingConfidence.Degraded, TrackingConfidenceReason.OutOfView);
			}

			// The holding decision. A stretch of below-hold frames, not one of them,
			// is what makes a loss.
			if (!band.Clears(QualityDecision.Hold, holdSignal))
			{
				if (memory.BelowHoldFrames < int.MaxValue)
					++memory.BelowHoldFrames;

				if (memory.BelowHoldFrames >= framesToLose)
				{
					memory.AboveAcquireFrames = 0;
					return Settle(ref memory, previous,
						TrackingConfidence.Lost, TrackingConfidenceReason.BelowHold);
				}

				return Settle(ref memory, previous,
					TrackingConfidence.Degraded, TrackingConfidenceReason.Unverified);
			}

			memory.BelowHoldFrames = 0;
			return Settle(ref memory, previous,
				poseAccepted ? TrackingConfidence.Tracked : TrackingConfidence.Degraded,
				poseAccepted ? TrackingConfidenceReason.Accepted
					: TrackingConfidenceReason.Unverified);
		}

		/// <summary>
		/// Turns the answer of a frame that would read <see cref="TrackingConfidence.Tracked"/> into
		/// <see cref="TrackingConfidence.Degraded"/> with the reason <see cref="TrackingConfidenceReason.DisplayCatchingUp"/>, and leaves any other
		/// answer as it is.
		/// </summary>
		/// <remarks>
		/// It changes the answer and the state in the memory, and no counter. The counters that <see cref="Decide"/> advanced stay as they are, so
		/// the frames after this one decide exactly as they would have without it.
		/// </remarks>
		internal static TrackingConfidenceTransition HoldForDisplay(
			ref TrackingConfidenceMemory memory,
			TrackingConfidenceTransition transition)
		{
			if (transition.State != TrackingConfidence.Tracked)
				return transition;

			return Settle(ref memory, transition.Previous,
				TrackingConfidence.Degraded, TrackingConfidenceReason.DisplayCatchingUp);
		}

		/// <summary>
		/// Puts a body in the state another body is already in.
		/// </summary>
		/// <remarks>
		/// A rigidly attached child's pose is exactly as vouched for as its parent's,
		/// so the child does not run the machine on its own quality scalars — those
		/// describe the parent's evidence anyway. It takes the parent's answer, and
		/// it takes it through here so the edge is computed the one way.
		/// </remarks>
		public static TrackingConfidenceTransition Mirror(
			ref TrackingConfidenceMemory memory,
			TrackingConfidence state,
			TrackingConfidenceReason reason)
		{
			TrackingConfidence previous = memory.State;
			memory.BelowHoldFrames = 0;
			memory.AboveAcquireFrames = 0;
			memory.State = state;
			return new TrackingConfidenceTransition(previous, state, reason);
		}

		/// <summary>
		/// Clears a body's memory. Used where the tracker's own state is replaced,
		/// so no count is left standing against evidence that no longer applies.
		/// </summary>
		public static void Clear(ref TrackingConfidenceMemory memory)
		{
			memory.State = TrackingConfidence.Lost;
			memory.BelowHoldFrames = 0;
			memory.AboveAcquireFrames = 0;
		}

		private static TrackingConfidenceTransition Settle(
			ref TrackingConfidenceMemory memory,
			TrackingConfidence previous,
			TrackingConfidence next,
			TrackingConfidenceReason reason)
		{
			memory.State = next;
			return new TrackingConfidenceTransition(previous, next, reason);
		}
	}
}
