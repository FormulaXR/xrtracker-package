using System.Collections.Generic;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// What the check of an assembly part says about the part. A part is judged only while the camera is near the part's
	/// check view and still, and only from the evidence of a whole hold, never from a single frame.
	/// </summary>
	public enum PartPresence
	{
		/// <summary>
		/// The part is not judged. The part has no check view, the camera is not near it, the camera is not still, the
		/// parent is not tracked, the hold has not lasted long enough yet, or the evidence of the hold is mixed.
		/// </summary>
		Unknown = 0,

		/// <summary>Near the check view and with the camera still, the part read present on most of the hold.</summary>
		Present = 1,

		/// <summary>Near the check view and with the camera still, the part read absent on most of the hold.</summary>
		Absent = 2,
	}

	/// <summary>What became of a call into the plugin that a plugin of an older build may not export.</summary>
	internal enum NativeCallOutcome
	{
		/// <summary>The plugin did what was asked.</summary>
		Done,

		/// <summary>The plugin has the export and refused the call.</summary>
		Refused,

		/// <summary>The plugin has no such export, or no plugin is loaded.</summary>
		Missing,
	}

	/// <summary>The settings a part's presence is judged with, as the part carries them.</summary>
	internal readonly struct PresenceSettings
	{
		public PresenceSettings(float viewDistance, float viewAngleDegrees, float stillDistance, float stillAngleDegrees,
			float stillWindowSeconds, float holdSeconds)
		{
			ViewDistance = viewDistance;
			ViewAngleDegrees = viewAngleDegrees;
			StillDistance = stillDistance;
			StillAngleDegrees = stillAngleDegrees;
			StillWindowSeconds = stillWindowSeconds;
			HoldSeconds = holdSeconds;
		}

		/// <summary>How far, in metres, the camera may be from the check view and still be near it.</summary>
		public float ViewDistance { get; }

		/// <summary>How far, in degrees, the camera's viewing direction may turn from the check view's and still be near it.</summary>
		public float ViewAngleDegrees { get; }

		/// <summary>How far, in metres, the camera may move over the stillness window and still be still.</summary>
		public float StillDistance { get; }

		/// <summary>How far, in degrees, the camera's viewing direction may turn over the stillness window and still be still.</summary>
		public float StillAngleDegrees { get; }

		/// <summary>How long, in seconds, the camera must have stayed within the stillness bounds.</summary>
		public float StillWindowSeconds { get; }

		/// <summary>How long, in seconds, the evidence of one judgement is gathered.</summary>
		public float HoldSeconds { get; }
	}

	/// <summary>
	/// Judges whether a part is present from a hold at its check view. Every pose it reads is the camera's or the view's in
	/// the frame of the part, so the judgement does not depend on whether the camera or the object moves in the world.
	/// </summary>
	/// <remarks>
	/// <para>The camera is near the view when its position is within the view distance and its viewing direction (its
	/// forward axis, as for the start viewpoint) within the view angle. Roll about the viewing direction is not read,
	/// because a hand-held device is held at any roll. The camera is still when, over the whole stillness window, it
	/// stayed within the stillness distance and angle of where it is now.</para>
	/// <para>Each step near the view and still adds its duration to the hold, as passing or not. The hold keeps the last
	/// hold seconds. Once it is full, the part is <see cref="PartPresence.Present"/> when at least
	/// <see cref="PRESENT_SHARE"/> of the hold passed and <see cref="PartPresence.Absent"/> when at most
	/// <see cref="ABSENT_SHARE"/> did, and <see cref="PartPresence.Unknown"/> otherwise. Leaving the view, moving, or a
	/// step that cannot be judged empties the hold, so a judgement always rests on one unbroken hold.</para>
	/// </remarks>
	internal sealed class PartPresenceJudge
	{
		/// <summary>The share of a full hold that must pass for the part to read present.</summary>
		internal const float PRESENT_SHARE = 0.8f;

		/// <summary>The share of a full hold that may pass at most for the part to read absent.</summary>
		internal const float ABSENT_SHARE = 0.2f;

		// Frame durations are summed, so a window that is full by design can read a hair short.
		private const float TIME_EPSILON = 1e-4f;

		private readonly List<(float time, Vector3 position, Vector3 forward)> _cameraHistory = new();
		private readonly List<(float seconds, bool passes)> _hold = new();
		private float _clock;

		/// <summary>The last judgement.</summary>
		public PartPresence Presence { get; private set; }

		/// <summary>Whether the camera was near the check view on the last step.</summary>
		public bool IsAtView { get; private set; }

		/// <summary>Whether the camera was still on the last step.</summary>
		public bool IsStill { get; private set; }

		/// <summary>How many seconds of evidence the current hold holds.</summary>
		public float HoldSeconds { get; private set; }

		/// <summary>The share of the current hold that passed, or zero when the hold is empty.</summary>
		public float PassingShare { get; private set; }

		/// <summary>Forgets every pose and every piece of evidence.</summary>
		public void Reset()
		{
			_cameraHistory.Clear();
			_hold.Clear();
			Presence = PartPresence.Unknown;
			IsAtView = false;
			IsStill = false;
			HoldSeconds = 0f;
			PassingShare = 0f;
		}

		/// <summary>
		/// Takes one completed tracking step and returns the judgement after it.
		/// </summary>
		/// <param name="settings">The part's presence settings.</param>
		/// <param name="deltaSeconds">The duration of the step.</param>
		/// <param name="judgeable">False when this step says nothing about the part, which empties everything.</param>
		/// <param name="cameraInPart">The camera's pose in the frame of the part.</param>
		/// <param name="viewInPart">The check view's pose in the frame of the part.</param>
		/// <param name="passes">Whether the part's own reading on this step says present.</param>
		public PartPresence Step(in PresenceSettings settings, float deltaSeconds, bool judgeable, Pose cameraInPart, Pose viewInPart,
			bool passes)
		{
			if (!judgeable || !(deltaSeconds > 0f))
			{
				Reset();
				return Presence;
			}

			_clock += deltaSeconds;
			Vector3 forward = cameraInPart.rotation * Vector3.forward;
			Vector3 viewForward = viewInPart.rotation * Vector3.forward;
			IsAtView = Vector3.Distance(cameraInPart.position, viewInPart.position) <= settings.ViewDistance
				&& Vector3.Angle(forward, viewForward) <= settings.ViewAngleDegrees;
			IsStill = UpdateStillness(settings, cameraInPart.position, forward);

			if (!IsAtView || !IsStill)
			{
				_hold.Clear();
				HoldSeconds = 0f;
				PassingShare = 0f;
				Presence = PartPresence.Unknown;
				return Presence;
			}

			_hold.Add((deltaSeconds, passes));
			float total = 0f;
			foreach ((float seconds, bool _) in _hold)
				total += seconds;
			while (_hold.Count > 1 && total - _hold[0].seconds >= settings.HoldSeconds - TIME_EPSILON)
			{
				total -= _hold[0].seconds;
				_hold.RemoveAt(0);
			}

			float passing = 0f;
			foreach ((float seconds, bool held) in _hold)
			{
				if (held)
					passing += seconds;
			}
			HoldSeconds = total;
			PassingShare = total > 0f ? passing / total : 0f;
			if (total < settings.HoldSeconds - TIME_EPSILON)
				Presence = PartPresence.Unknown;
			else if (PassingShare >= PRESENT_SHARE)
				Presence = PartPresence.Present;
			else if (PassingShare <= ABSENT_SHARE)
				Presence = PartPresence.Absent;
			else
				Presence = PartPresence.Unknown;
			return Presence;
		}

		private bool UpdateStillness(in PresenceSettings settings, Vector3 position, Vector3 forward)
		{
			_cameraHistory.Add((_clock, position, forward));
			while (_cameraHistory.Count > 1 && _clock - _cameraHistory[1].time >= settings.StillWindowSeconds - TIME_EPSILON)
				_cameraHistory.RemoveAt(0);

			if (_clock - _cameraHistory[0].time < settings.StillWindowSeconds - TIME_EPSILON)
				return false;
			foreach ((float _, Vector3 earlier, Vector3 earlierForward) in _cameraHistory)
			{
				if (Vector3.Distance(earlier, position) > settings.StillDistance
					|| Vector3.Angle(earlierForward, forward) > settings.StillAngleDegrees)
					return false;
			}
			return true;
		}
	}

	/// <summary>The rules of the free check that do not depend on the plugin.</summary>
	internal static class FreeCheckRules
	{
		/// <summary>The width of the pull back to the seat, as a multiple of the part's tolerance.</summary>
		internal const float SEAT_PULL_WIDTH_OF_TOLERANCE = 1f;

		/// <summary>How far the part may settle at most, as a multiple of the part's tolerance.</summary>
		internal const float SETTLE_LIMIT_OF_TOLERANCE = 2f;

		/// <summary>
		/// The distance in metres and the angle in degrees of a seat offset the plugin reports, which is a rotation vector in
		/// radians followed by a translation in metres.
		/// </summary>
		internal static void Magnitudes(float[] offset, out float distance, out float angleDegrees)
		{
			distance = new Vector3(offset[3], offset[4], offset[5]).magnitude;
			angleDegrees = new Vector3(offset[0], offset[1], offset[2]).magnitude * Mathf.Rad2Deg;
		}

		/// <summary>Whether a part that settled this far is still in its seat.</summary>
		internal static bool IsInSeat(float distance, float angleDegrees, float distanceTolerance, float angleToleranceDegrees) =>
			distance <= distanceTolerance && angleDegrees <= angleToleranceDegrees;
	}
}
