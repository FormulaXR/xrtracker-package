using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Tells when the AR session moves its world frame between two steps, and gives the map that carries a pose from the old frame to the new one.
	/// </summary>
	/// <remarks>
	/// <para>A relocalisation moves the camera pose of the session by a distance and an angle that no hand can cover in one frame, while the session
	/// still reports tracking. The real object has not moved relative to the camera in that frame, so the pose of a body relative to the camera is
	/// the same on both sides of the jump, and only the world frame it is written in has changed.</para>
	/// <para>The map assumes the camera stood still for the one frame the jump took. It takes the camera pose before the jump to the camera pose
	/// after it, so a body pose in the old frame lands on the pose with the same camera-relative pose in the new frame.</para>
	/// </remarks>
	internal static class ArWorldJump
	{
		/// <summary>The translation step, in metres, below which the camera pose never counts as a jump whatever the time between the two steps.</summary>
		internal const float MIN_TRANSLATION_METRES = 0.25f;

		/// <summary>The speed, in metres per second, that a camera pose may move at before the step counts as a jump, when the time between the steps makes it larger than the minimum.</summary>
		internal const float MAX_TRANSLATION_METRES_PER_SECOND = 5f;

		/// <summary>The rotation step, in degrees, below which the camera pose never counts as a jump whatever the time between the two steps.</summary>
		internal const float MIN_ROTATION_DEGREES = 45f;

		/// <summary>The turning speed, in degrees per second, that a camera pose may move at before the step counts as a jump, when the time between the steps makes it larger than the minimum.</summary>
		internal const float MAX_ROTATION_DEGREES_PER_SECOND = 900f;

		/// <summary>
		/// The longest time between two steps that widens the limits. A stall of the frame stream is not a faster camera, so a longer gap is judged as
		/// a gap of this length.
		/// </summary>
		internal const float MAX_STEP_SECONDS = 0.5f;

		/// <summary>The largest step in metres that the camera pose may take over <paramref name="seconds"/> without being a jump.</summary>
		internal static float TranslationLimit(float seconds) =>
			Mathf.Max(MIN_TRANSLATION_METRES, MAX_TRANSLATION_METRES_PER_SECOND * ClampSeconds(seconds));

		/// <summary>The largest step in degrees that the camera pose may turn over <paramref name="seconds"/> without being a jump.</summary>
		internal static float RotationLimit(float seconds) =>
			Mathf.Max(MIN_ROTATION_DEGREES, MAX_ROTATION_DEGREES_PER_SECOND * ClampSeconds(seconds));

		private static float ClampSeconds(float seconds) =>
			float.IsNaN(seconds) ? 0f : Mathf.Clamp(seconds, 0f, MAX_STEP_SECONDS);

		/// <summary>Whether the camera pose moved between two steps by more than a hand can in the time between them, in distance or in angle.</summary>
		internal static bool IsJump(Vector3 previousPosition, Quaternion previousRotation, Vector3 position, Quaternion rotation, float seconds) =>
			Vector3.Distance(previousPosition, position) > TranslationLimit(seconds)
			|| Quaternion.Angle(previousRotation, rotation) > RotationLimit(seconds);

		/// <summary>The map from the old frame to the new one, given the camera pose before and after the jump.</summary>
		internal static Pose CarryBetween(Vector3 previousPosition, Quaternion previousRotation, Vector3 position, Quaternion rotation)
		{
			Quaternion turn = rotation * Quaternion.Inverse(previousRotation);
			return new Pose(position - turn * previousPosition, turn);
		}

		/// <summary>A position carried to the new frame.</summary>
		internal static Vector3 Carry(Pose carry, Vector3 position) => carry.rotation * position + carry.position;

		/// <summary>A rotation carried to the new frame.</summary>
		internal static Quaternion Carry(Pose carry, Quaternion rotation) => carry.rotation * rotation;

		/// <summary>A pose carried to the new frame.</summary>
		internal static Pose Carry(Pose carry, Pose pose) => new Pose(Carry(carry, pose.position), Carry(carry, pose.rotation));

		/// <summary>A pose written as a matrix of unit scale, carried to the new frame.</summary>
		internal static Matrix4x4 Carry(Pose carry, Matrix4x4 matrix) =>
			Matrix4x4.TRS(Carry(carry, matrix.GetPosition()), Carry(carry, matrix.rotation), Vector3.one);
	}

	/// <summary>
	/// Watches the camera pose that each tracking step is submitted with, and says on which step the AR session moved its world.
	/// </summary>
	/// <remarks>
	/// A step is compared with the step before it only when the time runs forward and the playback has not been seeked. After a seek, a restart of a
	/// looping sequence or a camera pose that repeats a timestamp, the two poses belong to different moments and say nothing about the session.
	/// </remarks>
	internal sealed class ArWorldJumpWatch
	{
		private bool _hasPrevious;
		private Vector3 _previousPosition;
		private Quaternion _previousRotation = Quaternion.identity;
		private long _previousTimestampNs;
		private long _previousSeekEpoch;

		/// <summary>Forgets the camera pose of the step before, so the next step is compared with nothing.</summary>
		internal void Reset() => _hasPrevious = false;

		/// <summary>
		/// Takes the camera pose of the step that is about to run. When it differs from the pose of the step before by more than a hand can move it in
		/// the time between them, the result is true and <paramref name="carry"/> is the map from the old frame to the new one.
		/// </summary>
		/// <param name="position">The camera position of this step in the session's world frame.</param>
		/// <param name="rotation">The camera rotation of this step in the session's world frame.</param>
		/// <param name="timestampNs">The time of the frame in nanoseconds, from the recording or from the live clock.</param>
		/// <param name="seekEpoch">A number that changes whenever the playback is seeked.</param>
		/// <param name="carry">The map from the old frame to the new one when the step is a jump, and the identity otherwise.</param>
		internal bool Observe(Vector3 position, Quaternion rotation, long timestampNs, long seekEpoch, out Pose carry)
		{
			carry = Pose.identity;
			bool compared = _hasPrevious && seekEpoch == _previousSeekEpoch && timestampNs > _previousTimestampNs;
			bool jumped = false;
			if (compared)
			{
				float seconds = (float)((timestampNs - _previousTimestampNs) / 1_000_000_000.0);
				jumped = ArWorldJump.IsJump(_previousPosition, _previousRotation, position, rotation, seconds);
				if (jumped)
					carry = ArWorldJump.CarryBetween(_previousPosition, _previousRotation, position, rotation);
			}
			_hasPrevious = true;
			_previousPosition = position;
			_previousRotation = rotation;
			_previousTimestampNs = timestampNs;
			_previousSeekEpoch = seekEpoch;
			return jumped;
		}
	}
}
