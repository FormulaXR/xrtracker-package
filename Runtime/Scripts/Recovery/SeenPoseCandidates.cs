using System;
using System.Collections.Generic;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>One camera-relative pose a body was accepted at, kept as a place a recovery can start from.</summary>
	internal readonly struct SeenPose
	{
		public SeenPose(int entryId, string label, FTTrackingPose trackerPose, float[] cameraFromBody)
		{
			EntryId = entryId;
			Label = label;
			TrackerPose = trackerPose;
			CameraFromBody = cameraFromBody;
		}

		/// <summary>The id the pose is proposed under, at or above the kept-pose base.</summary>
		public int EntryId { get; }

		/// <summary>Where the pose came from, for the record: start, last, or ring:serial.</summary>
		public string Label { get; }

		/// <summary>The pose in the tracker's own space, as the tracker reports its solve.</summary>
		public FTTrackingPose TrackerPose { get; }

		/// <summary>The same pose as the row-major camera-from-body matrix a pose evaluation takes.</summary>
		public float[] CameraFromBody { get; }
	}

	/// <summary>
	/// Scores a kept pose against the current frame beside the pose the body holds.
	/// </summary>
	internal interface ISeenPoseScorer
	{
		/// <summary>
		/// Fills <paramref name="rows"/>[0] with the held pose and <paramref name="rows"/>[1]
		/// with <paramref name="candidate"/>, both evaluated at the current frame.
		/// </summary>
		/// <returns>False, with the reason, when either could not be read or evaluated.</returns>
		bool TryScore(string bodyId, in SeenPose candidate, FTVerifiedCandidate[] rows, out string refusal);
	}

	/// <summary>The scorer the shipped driver runs through: the plugin's own pose evaluation.</summary>
	internal sealed class SeenPoseBridgeScorer : ISeenPoseScorer, ICandidateTimingSource, ICandidateStartTimingSource
	{
		private FTPoseEvidenceTiming _heldTiming;
		private FTPoseEvidenceTiming _candidateTiming;
		private bool _timingsRead;
		private FTPoseEvidenceStartTiming _heldStartTiming;
		private FTPoseEvidenceStartTiming _candidateStartTiming;
		private bool _startTimingsRead;

		/// <summary>The split of the start stage of the last score, held pose first, when the plugin gave it.</summary>
		public bool TryReadStartTimings(string bodyId, int count, FTPoseEvidenceStartTiming[] timings)
		{
			if (!_startTimingsRead || count != 2 || timings == null || timings.Length < 2)
				return false;
			timings[0] = _heldStartTiming;
			timings[1] = _candidateStartTiming;
			return true;
		}

		/// <summary>The stage times of the last score, held pose first, when the plugin gave them.</summary>
		public bool TryReadTimings(string bodyId, int count, FTPoseEvidenceTiming[] timings)
		{
			if (!_timingsRead || count != 2 || timings == null || timings.Length < 2)
				return false;
			timings[0] = _heldTiming;
			timings[1] = _candidateTiming;
			return true;
		}

		public bool TryScore(string bodyId, in SeenPose candidate, FTVerifiedCandidate[] rows,
			out string refusal)
		{
			_timingsRead = false;
			_startTimingsRead = false;
			FTTrackingPose heldPose = default;
			if (!PoseVerification.TryGetCameraFromBodyPose(bodyId, out float[] held) ||
				!FTBridge.FT_GetBodyPose(bodyId, out heldPose))
			{
				refusal = "the held pose could not be read";
				return false;
			}
			if (!PoseVerification.TryEvaluate(bodyId, held, out FTPoseEvidence heldEvidence, out _))
			{
				refusal = "the held pose could not be evaluated";
				return false;
			}
			bool heldTimed = FTBridge.TryGetPoseEvidenceTiming(bodyId, out _heldTiming);
			bool heldStartTimed = FTBridge.TryGetPoseEvidenceStartTiming(bodyId, out _heldStartTiming);
			if (!PoseVerification.TryEvaluate(bodyId, candidate.CameraFromBody,
				out FTPoseEvidence candidateEvidence, out _))
			{
				refusal = "the kept pose could not be evaluated";
				return false;
			}
			_timingsRead = heldTimed && FTBridge.TryGetPoseEvidenceTiming(bodyId, out _candidateTiming);
			_startTimingsRead = heldStartTimed &&
				FTBridge.TryGetPoseEvidenceStartTiming(bodyId, out _candidateStartTiming);
			rows[0].is_held_pose = 1;
			rows[0].camera_from_body = held;
			rows[0].tracker_pose = heldPose;
			rows[0].evidence = heldEvidence;
			rows[1].entry_id = candidate.EntryId;
			rows[1].camera_from_body = (float[])candidate.CameraFromBody.Clone();
			rows[1].tracker_pose = candidate.TrackerPose;
			rows[1].evidence = candidateEvidence;
			refusal = string.Empty;
			return true;
		}
	}

	/// <summary>
	/// The camera-relative poses one body was accepted at, and the order a recovery tries them in.
	/// </summary>
	/// <remarks>
	/// <para>It holds the start pose, the last accepted pose, and a ring of accepted poses
	/// spaced by <see cref="TrackerDefaults.SEEN_POSE_ADMISSION_TRANSLATION_M"/> or
	/// <see cref="TrackerDefaults.SEEN_POSE_ADMISSION_ROTATION_DEG"/>, the oldest evicted
	/// at <see cref="TrackerDefaults.SEEN_POSE_RING_CAPACITY"/>. It lives beside the body's
	/// per-frame state rather than in it, because a loss drops that state and a loss is
	/// the moment these poses exist for.</para>
	/// <para>A sweep tries the last accepted pose, then the start pose, then the ring
	/// newest first. A pose whose seat did not start is left out of the next sweep, so
	/// one wrong pose cannot take every window.</para>
	/// </remarks>
	internal sealed class SeenPoseCandidates
	{
		private const int START_ID = TrackerDefaults.SEEN_POSE_ENTRY_ID_BASE + 1;
		private const int LAST_ID = TrackerDefaults.SEEN_POSE_ENTRY_ID_BASE + 2;
		private const int RING_ID_OFFSET = TrackerDefaults.SEEN_POSE_ENTRY_ID_BASE + 16;

		private readonly List<(int serial, SeenPose pose)> _ring = new List<(int, SeenPose)>();
		private readonly List<SeenPose> _sweep = new List<SeenPose>();
		private readonly HashSet<int> _failedThisSweep = new HashSet<int>();
		private readonly HashSet<int> _skippedThisSweep = new HashSet<int>();
		private SeenPose _start;
		private SeenPose _last;
		private int _lastSerial = -1;
		private int _nextSerial;
		private int _cursor = -1;

		/// <summary>Whether the start pose has been taken.</summary>
		public bool HasStart { get; private set; }

		/// <summary>Whether a pose has been accepted since the body was set up.</summary>
		public bool HasLast { get; private set; }

		/// <summary>How many poses the ring holds.</summary>
		public int RingCount => _ring.Count;

		/// <summary>Whether the tracker-pose frame was checked against the evaluation frame.</summary>
		public bool FrameChecked { get; private set; }

		/// <summary>Whether that check found the two frames in agreement.</summary>
		public bool FrameAgrees { get; private set; }

		/// <summary>Takes the start pose once. Later calls are ignored.</summary>
		public void SetStart(FTTrackingPose trackerPose, float[] cameraFromBody)
		{
			if (HasStart || cameraFromBody == null)
				return;
			_start = new SeenPose(START_ID, "start", trackerPose, cameraFromBody);
			HasStart = true;
		}

		/// <summary>Records the outcome of the frame check, once.</summary>
		public void SetFrameCheck(bool agrees)
		{
			if (FrameChecked)
				return;
			FrameChecked = true;
			FrameAgrees = agrees;
		}

		/// <summary>
		/// Records one accepted pose: it becomes the last accepted pose, and joins the
		/// ring when it is far enough from every pose already kept.
		/// </summary>
		public void Record(FTTrackingPose trackerPose, float[] cameraFromBody)
		{
			if (cameraFromBody == null)
				return;
			int serial = _nextSerial++;
			_last = new SeenPose(LAST_ID, "last", trackerPose, cameraFromBody);
			HasLast = true;
			_lastSerial = -1;
			foreach (var kept in _ring)
			{
				if (!IsApart(trackerPose, kept.pose.TrackerPose))
					return;
			}
			_ring.Add((serial, new SeenPose(RING_ID_OFFSET + serial, "ring:" + serial, trackerPose,
				cameraFromBody)));
			_lastSerial = serial;
			if (_ring.Count > TrackerDefaults.SEEN_POSE_RING_CAPACITY)
				_ring.RemoveAt(0);
		}

		/// <summary>
		/// The next pose to try, in sweep order. False when nothing is kept, or when every
		/// kept pose is being left out of this sweep.
		/// </summary>
		public bool TryNext(out SeenPose candidate)
		{
			candidate = default;
			for (int guard = 0; guard < 2; ++guard)
			{
				if (_cursor < 0 || _cursor >= _sweep.Count)
					BeginSweep();
				while (_cursor < _sweep.Count)
				{
					SeenPose next = _sweep[_cursor++];
					if (_skippedThisSweep.Contains(next.EntryId))
						continue;
					candidate = next;
					return true;
				}
			}
			return false;
		}

		/// <summary>Leaves the pose whose seat did not start out of the next sweep.</summary>
		public void MarkWindowFailed(int entryId)
		{
			_failedThisSweep.Add(entryId);
		}

		/// <summary>Starts the next attempt from the top, with nothing left out.</summary>
		public void RestartSweep()
		{
			_cursor = -1;
			_failedThisSweep.Clear();
			_skippedThisSweep.Clear();
		}

		private void BeginSweep()
		{
			_skippedThisSweep.Clear();
			foreach (int id in _failedThisSweep)
				_skippedThisSweep.Add(id);
			_failedThisSweep.Clear();
			_sweep.Clear();
			if (HasLast)
				_sweep.Add(_last);
			// The start pose's matrix is built from its tracker pose, so it is tried only
			// once the frame check has found the built matrix and the tracker's in agreement.
			if (HasStart && FrameAgrees)
				_sweep.Add(_start);
			for (int index = _ring.Count - 1; index >= 0; --index)
			{
				// The newest ring entry is the last accepted pose itself when it was kept.
				if (_ring[index].serial == _lastSerial && HasLast)
					continue;
				_sweep.Add(_ring[index].pose);
			}
			_cursor = 0;
		}

		/// <summary>Whether two tracker poses are far enough apart for both to be kept.</summary>
		internal static bool IsApart(in FTTrackingPose a, in FTTrackingPose b)
		{
			var ta = new Vector3(a.pos_x, a.pos_y, a.pos_z);
			var tb = new Vector3(b.pos_x, b.pos_y, b.pos_z);
			if (Vector3.Distance(ta, tb) > TrackerDefaults.SEEN_POSE_ADMISSION_TRANSLATION_M)
				return true;
			return AngleDegrees(a, b) > TrackerDefaults.SEEN_POSE_ADMISSION_ROTATION_DEG;
		}

		/// <summary>The rotation between two tracker poses, in degrees.</summary>
		internal static float AngleDegrees(in FTTrackingPose a, in FTTrackingPose b)
		{
			double dot = (double)a.rot_x * b.rot_x + (double)a.rot_y * b.rot_y +
				(double)a.rot_z * b.rot_z + (double)a.rot_w * b.rot_w;
			double na = Math.Sqrt((double)a.rot_x * a.rot_x + (double)a.rot_y * a.rot_y +
				(double)a.rot_z * a.rot_z + (double)a.rot_w * a.rot_w);
			double nb = Math.Sqrt((double)b.rot_x * b.rot_x + (double)b.rot_y * b.rot_y +
				(double)b.rot_z * b.rot_z + (double)b.rot_w * b.rot_w);
			if (na <= 0.0 || nb <= 0.0)
				return 180f;
			double cosine = Math.Min(1.0, Math.Abs(dot) / (na * nb));
			return (float)(2.0 * Math.Acos(cosine) * 180.0 / Math.PI);
		}

		/// <summary>
		/// The row-major 4x4 of a tracker pose, in the tracker's own axes, which is the
		/// camera-from-geometry matrix on a path whose tracking space is the camera.
		/// </summary>
		internal static float[] ToMatrix(in FTTrackingPose pose)
		{
			double x = pose.rot_x, y = pose.rot_y, z = pose.rot_z, w = pose.rot_w;
			double norm = Math.Sqrt(x * x + y * y + z * z + w * w);
			if (norm <= 0.0)
				return null;
			x /= norm;
			y /= norm;
			z /= norm;
			w /= norm;
			return new[]
			{
				(float)(1 - 2 * (y * y + z * z)), (float)(2 * (x * y - z * w)), (float)(2 * (x * z + y * w)), pose.pos_x,
				(float)(2 * (x * y + z * w)), (float)(1 - 2 * (x * x + z * z)), (float)(2 * (y * z - x * w)), pose.pos_y,
				(float)(2 * (x * z - y * w)), (float)(2 * (y * z + x * w)), (float)(1 - 2 * (x * x + y * y)), pose.pos_z,
				0f, 0f, 0f, 1f,
			};
		}

		/// <summary>
		/// The largest translation, in metres, and rotation, in degrees, between two
		/// row-major rigid 4x4 matrices.
		/// </summary>
		internal static void Difference(float[] a, float[] b, out float metres, out float degrees)
		{
			metres = float.PositiveInfinity;
			degrees = 180f;
			if (a == null || b == null || a.Length != 16 || b.Length != 16)
				return;
			float dx = a[3] - b[3], dy = a[7] - b[7], dz = a[11] - b[11];
			metres = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
			// trace(Ra^T Rb) = sum over the rotation entries of a times b.
			double trace = 0.0;
			for (int row = 0; row < 3; ++row)
			{
				for (int column = 0; column < 3; ++column)
					trace += (double)a[row * 4 + column] * b[row * 4 + column];
			}
			double cosine = Math.Max(-1.0, Math.Min(1.0, (trace - 1.0) / 2.0));
			degrees = (float)(Math.Acos(cosine) * 180.0 / Math.PI);
		}
	}
}
