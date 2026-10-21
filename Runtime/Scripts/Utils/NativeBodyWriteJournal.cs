using System.Collections.Generic;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// One pose or lifecycle call this package made to the plugin.
	/// </summary>
	internal readonly struct NativeBodyWrite
	{
		/// <summary>Engine axes: the pose as this package holds it.</summary>
		/// <remarks>
		/// Most poses recorded here are the pose as the package held it, before
		/// <c>ConversionUtils.GetConvertedPose</c> turned it into the plugin's
		/// axes. The plugin's own answer — what <c>FT_GetBodyPose</c> returns — is
		/// in the other convention, and the two differ by a negation of the y
		/// translation and of the x and z rotation components. Comparing a record
		/// here against a pose read from the plugin without applying that
		/// conversion reports an exact identity as a total disagreement, which is
		/// why every record names its own convention rather than leaving a reader
		/// to know which one it is.
		/// </remarks>
		public const string ENGINE_AXES = "unity";

		/// <summary>Plugin axes: the pose already converted for the call.</summary>
		/// <remarks>
		/// Not every call site holds an engine-axis pose to record. One publishes a
		/// pose that was converted earlier and never existed in engine axes on that
		/// path, and recording it as though it had would be the exact mistake this
		/// field exists to prevent.
		/// </remarks>
		public const string PLUGIN_AXES = "plugin";

		/// <summary>The convention named on a record that carries no pose.</summary>
		public const string NO_AXES = "none";

		/// <summary>
		/// The <see cref="Call"/> of a record that is not a call to the plugin at all,
		/// but a replacement of the retained initialization pose the next publication
		/// will carry.
		/// </summary>
		/// <remarks>
		/// It sits in the same sequence as the pose calls because its position in that
		/// sequence is what a reader needs from it: a replacement recorded before the
		/// publication that follows it is the pose that publication carried, and one
		/// recorded after it is not. Its <see cref="Space"/> names the cadence the
		/// replacement was made on rather than a tracking space, which is why the
		/// constants below name the cadences.
		/// </remarks>
		public const string RETAINED_POSE_LATCH = "RetainedPoseLatch";

		/// <summary>Replaced while the tracking step was being prepared.</summary>
		public const string CADENCE_BEFORE_STEP = "beforeStep";

		/// <summary>Replaced by a pose written in from outside the package.</summary>
		public const string CADENCE_ADOPTED = "adopted";

		/// <summary>The initialization pose was latched again, returning a body to the guide.</summary>
		public const string CADENCE_GUIDE = "guide";

		/// <summary>Replaced because the body's initial pose was computed afresh.</summary>
		public const string CADENCE_INITIAL_POSE = "initialPose";

		/// <summary>Replaced because the AR session moved its world, so the pose was carried to the new frame.</summary>
		public const string CADENCE_WORLD_CARRY = "worldCarry";

		/// <summary>The result value a call that was never made is recorded with.</summary>
		public const int STATUS_NOT_ATTEMPTED = int.MinValue;

		public NativeBodyWrite(string bodyId, string call, string space,
			Vector3 position, Quaternion rotation, bool hasPose, bool landed)
			: this(bodyId, call, space, position, rotation, hasPose, landed,
				STATUS_NOT_ATTEMPTED, -1, hasPose ? ENGINE_AXES : NO_AXES)
		{
		}

		internal NativeBodyWrite(string bodyId, string call, string space,
			Vector3 position, Quaternion rotation, bool hasPose, bool landed,
			int nativeStatus, int ordinal, string axes)
		{
			BodyId = bodyId;
			Call = call;
			Space = space;
			Position = position;
			Rotation = rotation;
			HasPose = hasPose;
			Landed = landed;
			NativeStatus = nativeStatus;
			Ordinal = ordinal;
			Axes = axes;
		}

		/// <summary>The body the call named.</summary>
		public string BodyId { get; }

		/// <summary>The entry point that was called.</summary>
		public string Call { get; }

		/// <summary>The tracking space the pose is expressed in.</summary>
		public string Space { get; }

		/// <summary>The pose that was written, in the space named beside it.</summary>
		public Vector3 Position { get; }

		/// <summary>The orientation that was written.</summary>
		public Quaternion Rotation { get; }

		/// <summary>Whether the call carried a pose at all.</summary>
		public bool HasPose { get; }

		/// <summary>Whether the plugin accepted the call.</summary>
		public bool Landed { get; }

		/// <summary>
		/// The result the plugin returned, or <see cref="STATUS_NOT_ATTEMPTED"/>
		/// for a call that was withheld or whose result was not captured.
		/// </summary>
		/// <remarks>
		/// <see cref="Landed"/> is the package's own verdict and folds a refused
		/// frame together with a refused call. The raw result tells the two apart,
		/// and a reader chasing a pose that did not take effect needs that
		/// difference.
		/// </remarks>
		public int NativeStatus { get; }

		/// <summary>
		/// Where this call sat in the frame's sequence, counting from zero.
		/// </summary>
		/// <remarks>
		/// The list a reader takes is already in call order, but a serialized copy
		/// of it is not necessarily, and a frame that dropped calls past its
		/// capacity leaves a gap that only an explicit ordinal makes visible.
		/// </remarks>
		public int Ordinal { get; }

		/// <summary>The axis convention <see cref="Position"/> and <see cref="Rotation"/> are in.</summary>
		public string Axes { get; }

		/// <summary>A call the package decided not to make, and the reason it gave.</summary>
		public static NativeBodyWrite Withheld(string bodyId, string call, string reason)
		{
			return new NativeBodyWrite(bodyId, call, reason,
				Vector3.zero, Quaternion.identity, false, false,
				STATUS_NOT_ATTEMPTED, -1, NO_AXES);
		}
	}

	/// <summary>
	/// Keeps the pose and lifecycle calls made to the plugin since a reader last
	/// took them, so a recording can state what reached the tracker rather than
	/// inferring it from what the tracker afterwards held.
	/// </summary>
	/// <remarks>
	/// <para>Off unless a caller arms it, and it allocates nothing while off: a
	/// write on the closed journal is one boolean test. It records rather than
	/// decides, so a run with the journal armed takes the same decisions as one
	/// without it.</para>
	/// <para>A withheld call is recorded too. A guard that declines to write is
	/// indistinguishable at the plugin from a write that was never reached, and
	/// telling those apart is the whole reason this exists.</para>
	/// </remarks>
	internal static class NativeBodyWriteJournal
	{
		/// <summary>How many calls one frame may record before the rest are counted only.</summary>
		private const int FRAME_CAPACITY = 64;

		private static readonly List<NativeBodyWrite> Entries = new List<NativeBodyWrite>(FRAME_CAPACITY);
		private static bool _armed;
		private static int _dropped;
		private static int _next;

		/// <summary>Whether calls are being recorded.</summary>
		public static bool Armed => _armed;

		/// <summary>How many calls were made after the frame's capacity ran out.</summary>
		public static int Dropped => _dropped;

		/// <summary>
		/// How many calls this frame has made so far, dropped ones included.
		/// </summary>
		/// <remarks>
		/// It is the ordinal the next recorded call will carry, so a reader that
		/// samples it at some point in the frame can afterwards say, of any
		/// recorded call, whether that call was made before or after the sample:
		/// a call whose <see cref="NativeBodyWrite.Ordinal"/> is at least the
		/// sampled value was made after it. That is an ordering fact about the
		/// calls themselves rather than an inference from what two of them
		/// contain, which is why it is exposed instead of being reconstructed.
		/// </remarks>
		public static int WritesSoFar => _next;

		/// <summary>Starts or stops recording, and drops anything already held.</summary>
		public static void Arm(bool armed)
		{
			_armed = armed;
			Entries.Clear();
			_dropped = 0;
			_next = 0;
		}

		/// <summary>Copies out the calls recorded so far and clears the journal.</summary>
		public static void Take(List<NativeBodyWrite> into)
		{
			if (into == null)
				return;
			into.Clear();
			into.AddRange(Entries);
			Entries.Clear();
			_dropped = 0;
			_next = 0;
		}

		/// <summary>Records a call that carried a pose.</summary>
		/// <remarks>
		/// The pose is recorded as the package held it, in engine axes. See
		/// <see cref="NativeBodyWrite.ENGINE_AXES"/>.
		/// </remarks>
		internal static void RecordPose(string bodyId, string call, string space,
			Vector3 position, Quaternion rotation, bool landed)
		{
			RecordPose(bodyId, call, space, position, rotation, landed,
				NativeBodyWrite.STATUS_NOT_ATTEMPTED);
		}

		/// <summary>Records a call that carried an engine-axis pose, with the plugin's result.</summary>
		internal static void RecordPose(string bodyId, string call, string space,
			Vector3 position, Quaternion rotation, bool landed, int nativeStatus)
		{
			RecordPoseInAxes(bodyId, call, space, position, rotation, landed,
				nativeStatus, NativeBodyWrite.ENGINE_AXES);
		}

		/// <summary>Records a call that carried a pose already in the plugin's axes.</summary>
		internal static void RecordPluginAxisPose(string bodyId, string call,
			string space, Vector3 position, Quaternion rotation, bool landed,
			int nativeStatus)
		{
			RecordPoseInAxes(bodyId, call, space, position, rotation, landed,
				nativeStatus, NativeBodyWrite.PLUGIN_AXES);
		}

		private static void RecordPoseInAxes(string bodyId, string call,
			string space, Vector3 position, Quaternion rotation, bool landed,
			int nativeStatus, string axes)
		{
			if (!_armed)
				return;
			Add(new NativeBodyWrite(bodyId, call, space, position, rotation, true,
				landed, nativeStatus, _next, axes));
		}

		/// <summary>Records a call that carried no pose, such as a start or a reset.</summary>
		internal static void RecordCall(string bodyId, string call, bool landed)
		{
			if (!_armed)
				return;
			Add(new NativeBodyWrite(bodyId, call, "none",
				Vector3.zero, Quaternion.identity, false, landed,
				NativeBodyWrite.STATUS_NOT_ATTEMPTED, _next,
				NativeBodyWrite.NO_AXES));
		}

		/// <summary>
		/// Records a replacement of a body's retained initialization pose, and the
		/// cadence the replacement was made on.
		/// </summary>
		/// <remarks>
		/// <para>The retained pose is published to the tracker on the step cadence and
		/// nowhere else, so what the tracker is initialized at on any frame is decided
		/// by which replacements fell before that frame's publication. A reader given
		/// only the published pose can see that it changed but not what changed it: two
		/// replacements carrying different poses, one made while the frame was being
		/// rendered and one made while the step was being prepared, are
		/// indistinguishable afterwards from the pose alone.</para>
		/// <para>So the cadence is recorded beside the pose, and the entry takes an
		/// ordinal from the same sequence as the pose calls. That ordinal is the whole
		/// point: it places each replacement before or after the publication that
		/// follows it, which is the ordering fact a reader otherwise has to guess at.</para>
		/// <para>This records and decides nothing. No code reads a replacement entry
		/// back, and a body takes the same poses whether or not the journal is armed.</para>
		/// </remarks>
		internal static void RecordRetainedPoseLatch(string bodyId, string cadence,
			Vector3 position, Quaternion rotation)
		{
			if (!_armed)
				return;
			Add(new NativeBodyWrite(bodyId, NativeBodyWrite.RETAINED_POSE_LATCH, cadence,
				position, rotation, true, true,
				NativeBodyWrite.STATUS_NOT_ATTEMPTED, _next,
				NativeBodyWrite.ENGINE_AXES));
		}

		/// <summary>Records a call the package declined to make.</summary>
		internal static void RecordWithheld(string bodyId, string call, string reason)
		{
			if (!_armed)
				return;
			Add(new NativeBodyWrite(bodyId, call, reason,
				Vector3.zero, Quaternion.identity, false, false,
				NativeBodyWrite.STATUS_NOT_ATTEMPTED, _next,
				NativeBodyWrite.NO_AXES));
		}

		private static void Add(NativeBodyWrite entry)
		{
			// Counted before the capacity test, so a dropped call still advances the
			// sequence and the gap it leaves in the recorded ordinals is visible.
			++_next;
			if (Entries.Count >= FRAME_CAPACITY)
			{
				++_dropped;
				return;
			}
			Entries.Add(entry);
		}
	}
}
