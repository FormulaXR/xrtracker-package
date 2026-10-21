using System;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>What one search step says about the walk it belongs to.</summary>
	internal enum SearchWalkVerdict
	{
		/// <summary>No walk is engaged for the body, so the step is judged as it always was.</summary>
		Off,

		/// <summary>The refinement is inside both limits and more steps may follow.</summary>
		Walking,

		/// <summary>The step was the last one the walk allows. It still counts toward a start.</summary>
		Spent,

		/// <summary>
		/// The refinement has gone further from the placed start pose than the walk allows, so the step
		/// cannot count toward a start.
		/// </summary>
		OutOfRange,
	}

	/// <summary>Where the walk of one searching body stands.</summary>
	internal struct SearchWalkState
	{
		/// <summary>Steps the walk has run, counting the latest one once it has been judged.</summary>
		public int Steps;

		/// <summary>
		/// The median on-screen displacement, in pixels, of the carried pose from the placed start pose
		/// after the latest step, or not a number when it could not be measured.
		/// </summary>
		public float Pixels;

		/// <summary>How many walks ended without a start and were begun again from the placed start pose.</summary>
		public int Restarts;

		/// <summary>Whether the next step starts again from the placed start pose.</summary>
		public bool SnapBackDue;
	}

	/// <summary>
	/// The rule for a limited walk while a body searches. The refinement of each step is carried into the
	/// next for at most a number of steps, and only while the carried pose stays within a number of pixels
	/// of the placed start pose. Past either limit, without a start, the next step starts again from the
	/// placed start pose.
	/// </summary>
	internal static class SearchWalk
	{
		private const float MIN_DEPTH = 1e-4f;

		/// <summary>
		/// Whether a walk is engaged. It needs both limits to be usable, the placed start pose to be held
		/// while searching, no pose fusion (the carried pose and the placed pose are compared in the camera
		/// frame) and a body that is refined before its start, because without refinement there is nothing
		/// to carry.
		/// </summary>
		internal static bool IsEngaged((int Steps, float Pixels)? limits, bool holdsStartPose, bool poseFusion,
			bool refinesBeforeAcceptance, out int maxSteps, out float maxPixels)
		{
			maxSteps = 0;
			maxPixels = 0f;
			if (!limits.HasValue || !holdsStartPose || poseFusion || !refinesBeforeAcceptance)
				return false;
			(int steps, float pixels) = limits.Value;
			if (steps < 1 || float.IsNaN(pixels) || !(pixels > 0f))
				return false;
			maxSteps = steps;
			maxPixels = pixels;
			return true;
		}

		/// <summary>The walk reads as it always has, with no distinction between the first start and a loss.</summary>
		internal const int SCOPE_UNSCOPED = 0;

		/// <summary>A body that has started before is lost and has no seat on its window.</summary>
		internal const int SCOPE_HELD = 1;

		/// <summary>A body that has started before has a recovery seat on its window.</summary>
		internal const int SCOPE_SEAT = 2;

		/// <summary>A body that has never started, while the recovery walk is on.</summary>
		internal const int SCOPE_NEVER_STARTED = 3;

		/// <summary>
		/// Which scope a body's walk is in. With the recovery walk off it is always the unscoped one, so a change of
		/// scope never happens and nothing here changes the walk. With it on, the scope follows whether the body has
		/// started before and whether a recovery seat is on its window.
		/// </summary>
		internal static int ScopeOf(bool recoveryWalk, bool startedBefore, bool seatPending)
		{
			if (!recoveryWalk)
				return SCOPE_UNSCOPED;
			if (!startedBefore)
				return SCOPE_NEVER_STARTED;
			return seatPending ? SCOPE_SEAT : SCOPE_HELD;
		}

		/// <summary>
		/// Whether the walk may engage for a body in the scope it is in. Unscoped, always. With the recovery walk on, only
		/// for a body that has started before and has no recovery seat on its window.
		/// </summary>
		internal static bool InScope(bool recoveryWalk, bool startedBefore, bool seatPending) =>
			ScopeOf(recoveryWalk, startedBefore, seatPending) == SCOPE_UNSCOPED ||
			ScopeOf(recoveryWalk, startedBefore, seatPending) == SCOPE_HELD;

		/// <summary>
		/// The state of a walk that begins again at once, because the scope it ran in ended or the pose it started from
		/// was replaced. The count of walks begun again is kept, and the next step publishes the retained pose.
		/// </summary>
		internal static SearchWalkState Restart(in SearchWalkState state) => new SearchWalkState
		{
			Steps = 0,
			Pixels = 0f,
			Restarts = state.Restarts,
			SnapBackDue = false,
		};

		/// <summary>
		/// Whether the next step leaves the tracker at its last solve instead of starting again from the
		/// placed start pose. The first step of a walk always starts from the placed pose.
		/// </summary>
		internal static bool CarriesNextStep(bool engaged, bool refining, in SearchWalkState state) =>
			engaged && refining && state.Steps > 0 && !state.SnapBackDue;

		/// <summary>The state of a walk that begins from the placed start pose.</summary>
		internal static SearchWalkState Begin(in SearchWalkState state) => new SearchWalkState
		{
			Steps = 0,
			Pixels = 0f,
			Restarts = state.SnapBackDue ? state.Restarts + 1 : state.Restarts,
			SnapBackDue = false,
		};

		/// <summary>
		/// Judges the step just run. A displacement that could not be measured (not a number) leaves only
		/// the step limit in force. A displacement past the pixel limit cannot count toward a start, and the
		/// step that reaches the step limit still can.
		/// </summary>
		internal static SearchWalkVerdict Judge(int stepsRun, float pixels, int maxSteps, float maxPixels)
		{
			if (!float.IsNaN(pixels) && pixels > maxPixels)
				return SearchWalkVerdict.OutOfRange;
			return stepsRun >= maxSteps ? SearchWalkVerdict.Spent : SearchWalkVerdict.Walking;
		}

		/// <summary>
		/// The median over the points of how far each lands, on screen, between two poses of the body.
		/// </summary>
		/// <param name="points">Points of the model in the body's own frame.</param>
		/// <param name="count">How many of <paramref name="points"/> to use.</param>
		/// <param name="fromPosition">Position of the body in the camera frame at the first pose.</param>
		/// <param name="fromRotation">Rotation of the body in the camera frame at the first pose.</param>
		/// <param name="toPosition">Position of the body in the camera frame at the second pose.</param>
		/// <param name="toRotation">Rotation of the body in the camera frame at the second pose.</param>
		/// <param name="focalX">Horizontal focal length in pixels.</param>
		/// <param name="focalY">Vertical focal length in pixels.</param>
		/// <param name="scratch">Working space of at least <paramref name="count"/> values.</param>
		/// <returns>
		/// The median displacement in pixels. Not a number when it cannot be measured (no points, no focal
		/// length, or the first pose puts every point behind the camera), and infinity when the second pose
		/// puts every point behind the camera.
		/// </returns>
		/// <remarks>
		/// The camera looks along positive z in both poses' frame. Only the size of a displacement is used, so
		/// the direction of the vertical axis does not matter as long as both poses share it.
		/// </remarks>
		internal static float MedianDisplacementPx(Vector3[] points, int count, Vector3 fromPosition,
			Quaternion fromRotation, Vector3 toPosition, Quaternion toRotation, float focalX, float focalY,
			float[] scratch)
		{
			if (points == null || scratch == null || count <= 0 || count > points.Length ||
				scratch.Length < count || !(focalX > 0f) || !(focalY > 0f))
				return float.NaN;

			int seen = 0;
			int valid = 0;
			for (int i = 0; i < count; ++i)
			{
				Vector3 from = fromPosition + fromRotation * points[i];
				if (!(from.z > MIN_DEPTH))
					continue;
				++seen;
				Vector3 to = toPosition + toRotation * points[i];
				if (!(to.z > MIN_DEPTH))
					continue;
				float du = focalX * (to.x / to.z - from.x / from.z);
				float dv = focalY * (to.y / to.z - from.y / from.z);
				scratch[valid++] = Mathf.Sqrt(du * du + dv * dv);
			}

			if (seen == 0)
				return float.NaN;
			if (valid == 0)
				return float.PositiveInfinity;

			Array.Sort(scratch, 0, valid);
			int middle = valid / 2;
			return (valid & 1) == 1 ? scratch[middle] : 0.5f * (scratch[middle - 1] + scratch[middle]);
		}

		/// <summary>
		/// The points that the motion of the picture of a body is read at: the centre of its bounds and the eight corners of the bounds, with each half extent
		/// limited to <paramref name="maxHalfExtent"/>, in the frame of the body.
		/// </summary>
		internal static Vector3[] BoundsMotionPoints(Vector3 low, Vector3 high, float maxHalfExtent)
		{
			Vector3 centre = 0.5f * (low + high);
			Vector3 half = 0.5f * (high - low);
			half = new Vector3(Mathf.Min(half.x, maxHalfExtent), Mathf.Min(half.y, maxHalfExtent), Mathf.Min(half.z, maxHalfExtent));
			var points = new Vector3[9];
			points[0] = centre;
			for (int corner = 0; corner < 8; ++corner)
			{
				points[corner + 1] = centre + new Vector3(
					(corner & 1) == 0 ? -half.x : half.x,
					(corner & 2) == 0 ? -half.y : half.y,
					(corner & 4) == 0 ? -half.z : half.z);
			}
			return points;
		}

		/// <summary>The motion in pixels of a camera with the given focal length, scaled to the pixels of a camera with the reference focal length. Not a number without a focal length.</summary>
		internal static float ScaleToReferenceFocal(float pixels, float focalPixels, float referenceFocalPixels) =>
			focalPixels > 0f ? pixels * referenceFocalPixels / focalPixels : float.NaN;

		/// <summary>
		/// The largest distance, over the points, that a point of a body fixed in the world lands on screen between two poses of the camera.
		/// </summary>
		/// <param name="points">Points of the body in its own frame, such as the corners of its bounds.</param>
		/// <param name="bodyPosition">World position of the body.</param>
		/// <param name="bodyRotation">World rotation of the body.</param>
		/// <param name="previousCameraPosition">World position of the camera at the first pose.</param>
		/// <param name="previousCameraRotation">World rotation of the camera at the first pose.</param>
		/// <param name="cameraPosition">World position of the camera at the second pose.</param>
		/// <param name="cameraRotation">World rotation of the camera at the second pose.</param>
		/// <param name="focalX">Horizontal focal length in pixels.</param>
		/// <param name="focalY">Vertical focal length in pixels.</param>
		/// <returns>
		/// The largest displacement in pixels. Not a number when it cannot be measured, which is when there are no points, no focal length, or no
		/// point stands in front of the camera at both poses.
		/// </returns>
		internal static float MaxPointMotionPx(Vector3[] points, Vector3 bodyPosition, Quaternion bodyRotation,
			Vector3 previousCameraPosition, Quaternion previousCameraRotation, Vector3 cameraPosition, Quaternion cameraRotation,
			float focalX, float focalY)
		{
			if (points == null || points.Length == 0 || !(focalX > 0f) || !(focalY > 0f))
				return float.NaN;

			Quaternion previousWorldToCamera = Quaternion.Inverse(previousCameraRotation);
			Quaternion worldToCamera = Quaternion.Inverse(cameraRotation);
			float largest = 0f;
			bool measured = false;
			for (int i = 0; i < points.Length; ++i)
			{
				Vector3 world = bodyPosition + bodyRotation * points[i];
				Vector3 from = previousWorldToCamera * (world - previousCameraPosition);
				Vector3 to = worldToCamera * (world - cameraPosition);
				if (!(from.z > MIN_DEPTH) || !(to.z > MIN_DEPTH))
					continue;
				float du = focalX * (to.x / to.z - from.x / from.z);
				float dv = focalY * (to.y / to.z - from.y / from.z);
				largest = Mathf.Max(largest, Mathf.Sqrt(du * du + dv * dv));
				measured = true;
			}
			return measured ? largest : float.NaN;
		}
	}
}
