using System;

namespace IV.FormulaTracker
{
	/// <summary>
	/// What one driven tracking step did, and the serials that identify it.
	/// </summary>
	internal readonly struct DeterministicReplayStepOutcome
	{
		private DeterministicReplayStepOutcome(
			bool dispatched, long dispatchSerial, long completionSerial,
			long publicationSerial, int result, bool published,
			FTTrackStepToken token, string refusal)
		{
			Dispatched = dispatched;
			DispatchSerial = dispatchSerial;
			CompletionSerial = completionSerial;
			PublicationSerial = publicationSerial;
			Result = result;
			Published = published;
			Token = token;
			Refusal = refusal;
		}

		/// <summary>True when a native tracking step actually ran.</summary>
		public bool Dispatched { get; }

		/// <summary>The manager's dispatch serial for this step.</summary>
		public long DispatchSerial { get; }

		/// <summary>The manager's completion serial after this step.</summary>
		public long CompletionSerial { get; }

		/// <summary>The manager's publication serial after this step.</summary>
		public long PublicationSerial { get; }

		/// <summary>The value the native step returned.</summary>
		public int Result { get; }

		/// <summary>True when the completion reached subscribers.</summary>
		public bool Published { get; }

		/// <summary>What the native step reported about the frame it consumed.</summary>
		public FTTrackStepToken Token { get; }

		/// <summary>Why no step ran, or empty when one did.</summary>
		public string Refusal { get; }

		internal static DeterministicReplayStepOutcome Refused(string reason) =>
			new DeterministicReplayStepOutcome(
				false, 0, 0, 0, int.MinValue, false, default, reason);

		internal static DeterministicReplayStepOutcome Executed(
			long dispatchSerial, long completionSerial, long publicationSerial,
			int result, bool published, FTTrackStepToken token) =>
			new DeterministicReplayStepOutcome(
				true, dispatchSerial, completionSerial, publicationSerial,
				result, published, token, string.Empty);
	}

	/// <summary>
	/// Why a driven frame was refused or mis-owned.
	/// </summary>
	internal enum FrameOwnershipFault
	{
		None = 0,
		/// <summary>A step was attempted before the ledger was armed.</summary>
		NotArmed,
		/// <summary>A step was attempted after the last frame had been published.</summary>
		AfterTerminal,
		/// <summary>The frame is not the one the ledger expected next.</summary>
		UnexpectedFrame,
		/// <summary>A dispatch was opened while one was still open.</summary>
		DispatchInFlight,
		/// <summary>A completion arrived with no dispatch open.</summary>
		CompletionWithoutDispatch,
		/// <summary>A completion named a frame other than the open dispatch's.</summary>
		CompletionFrameMismatch,
		/// <summary>The native step reported a frame other than the one requested.</summary>
		ConsumedFrameMismatch,
		/// <summary>The native step read a number of images other than one.</summary>
		ImageLoadCountWrong,
		/// <summary>The native step serial did not advance by exactly one.</summary>
		StepSerialGap,
		/// <summary>The native step returned a failure.</summary>
		StepRejected,
		/// <summary>The step completed but never reached subscribers.</summary>
		NotPublished,
	}

	/// <summary>
	/// Holds one deterministic replay to exactly one dispatch, one native step and
	/// one publication per recorded frame, in order.
	/// </summary>
	/// <remarks>
	/// <para>The defect this exists to catch does not announce itself. A recorded
	/// frame that receives two tracking steps produces one row either way, because
	/// the second step is deduplicated by frame number, and the trajectory it
	/// perturbed looks like an ordinary tracking difference afterwards. So the
	/// count is checked where it is still a count, before it becomes a
	/// pose.</para>
	/// <para>This type holds no engine or native state, so the ordering cases it
	/// decides — duplicate dispatch, a skipped frame, a rejected step, a completion
	/// that names the wrong frame, a step before arming or after the terminal frame
	/// — are testable without either.</para>
	/// </remarks>
	internal sealed class FrameOwnershipLedger
	{
		private readonly int _lastFrame;
		private bool _armed;
		private int _expectedFrame;
		private bool _dispatchOpen;
		private int _openFrame = -1;
		private long _previousStepSerial;
		private bool _hasStepSerial;

		public FrameOwnershipLedger(int firstFrame, int lastFrame)
		{
			if (lastFrame < firstFrame)
				throw new ArgumentOutOfRangeException(nameof(lastFrame));
			_lastFrame = lastFrame;
			_expectedFrame = firstFrame;
		}

		/// <summary>Frames whose step has been published, in order, so far.</summary>
		public int PublishedFrames { get; private set; }

		/// <summary>Dispatches opened so far.</summary>
		public int Dispatches { get; private set; }

		/// <summary>The frame the ledger will accept next.</summary>
		public int ExpectedFrame => _expectedFrame;

		/// <summary>True once every frame from first to last has been published.</summary>
		public bool IsComplete => _armed && _expectedFrame > _lastFrame;

		/// <summary>True while a dispatch is open and unaccounted for.</summary>
		public bool HasOpenDispatch => _dispatchOpen;

		public void Arm() => _armed = true;

		/// <summary>
		/// Opens a dispatch for one frame. The frame must be the expected one, no
		/// other dispatch may be open, and the replay must be armed and unfinished.
		/// </summary>
		public bool TryBeginDispatch(int frame, out FrameOwnershipFault fault)
		{
			if (!_armed)
			{
				fault = FrameOwnershipFault.NotArmed;
				return false;
			}
			if (_expectedFrame > _lastFrame)
			{
				fault = FrameOwnershipFault.AfterTerminal;
				return false;
			}
			if (_dispatchOpen)
			{
				fault = FrameOwnershipFault.DispatchInFlight;
				return false;
			}
			if (frame != _expectedFrame)
			{
				fault = FrameOwnershipFault.UnexpectedFrame;
				return false;
			}

			_dispatchOpen = true;
			_openFrame = frame;
			++Dispatches;
			fault = FrameOwnershipFault.None;
			return true;
		}

		/// <summary>
		/// Closes the open dispatch against what the native step reported. The step
		/// must be the next one, must have read exactly the requested frame, must
		/// have succeeded, and must have been published.
		/// </summary>
		public bool TryCompleteDispatch(
			int frame, long stepSerial, int consumedFrame, int imagesLoaded,
			int result, bool published, out FrameOwnershipFault fault)
		{
			if (!_dispatchOpen)
			{
				fault = FrameOwnershipFault.CompletionWithoutDispatch;
				return false;
			}
			if (frame != _openFrame)
				return Close(false, out fault, FrameOwnershipFault.CompletionFrameMismatch);
			if (_hasStepSerial && stepSerial != _previousStepSerial + 1)
				return Close(false, out fault, FrameOwnershipFault.StepSerialGap);
			if (imagesLoaded != 1)
				return Close(false, out fault, FrameOwnershipFault.ImageLoadCountWrong);
			if (consumedFrame != frame)
				return Close(false, out fault, FrameOwnershipFault.ConsumedFrameMismatch);
			if (result != 0)
				return Close(false, out fault, FrameOwnershipFault.StepRejected);
			if (!published)
				return Close(false, out fault, FrameOwnershipFault.NotPublished);

			_previousStepSerial = stepSerial;
			_hasStepSerial = true;
			++PublishedFrames;
			_expectedFrame = frame + 1;
			return Close(true, out fault, FrameOwnershipFault.None);
		}

		private bool Close(bool accepted, out FrameOwnershipFault fault, FrameOwnershipFault reason)
		{
			_dispatchOpen = false;
			_openFrame = -1;
			fault = reason;
			return accepted;
		}
	}

	/// <summary>
	/// Builds the account of a driven step from what the host can see, for a plugin
	/// that publishes none of its own.
	/// </summary>
	/// <remarks>
	/// <para>A plugin that exports the pin and the token states, from inside the step,
	/// which frame the step read. One that does not leaves only the camera's cursor
	/// to go on, and the cursor names the frame the camera will read next. A step
	/// that was given its frame by moving the cursor onto it and that read it leaves
	/// the cursor one frame on, or on the same frame when that was the last frame of
	/// the range. A step that read nothing leaves it where it was only if it also
	/// returned a failure, because an unpaused camera reads before it returns.</para>
	/// <para>So the account is a consequence the ledger can still contradict. A cursor
	/// that was not on the requested frame when the step began, or that is anywhere
	/// but one frame on afterwards, is reported as a step that read no image, and the
	/// ledger then refuses the frame instead of absorbing it into the trajectory.</para>
	/// </remarks>
	internal static class SequenceStepAccount
	{
		/// <summary>The account of one step that was run on a named frame.</summary>
		/// <param name="stepSerial">A number that rises by one for every driven step.</param>
		/// <param name="requestedFrame">The frame the host moved the cursor onto.</param>
		/// <param name="cursorBefore">The cursor when the step began.</param>
		/// <param name="cursorAfter">The cursor when the step returned.</param>
		/// <param name="result">What the step returned.</param>
		internal static FTTrackStepToken Infer(
			long stepSerial, int requestedFrame, int cursorBefore, int cursorAfter, int result)
		{
			bool onRequestedFrame = cursorBefore == requestedFrame;
			bool movedOn = cursorAfter == requestedFrame + 1 || cursorAfter == requestedFrame;
			bool read = result == 0 && onRequestedFrame && movedOn;
			return new FTTrackStepToken
			{
				stepSerial = stepSerial,
				requestedFrame = requestedFrame,
				loadedFrame = read ? requestedFrame : -1,
				frameIndexBefore = cursorBefore,
				frameIndexAfter = cursorAfter,
				imagesLoaded = read ? 1 : 0,
				result = result,
			};
		}
	}
}
