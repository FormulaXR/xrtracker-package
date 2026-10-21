using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// What the settle check of a start read on the latest step of a body that has not started.
	/// </summary>
	public readonly struct StartSettleReading
	{
		internal StartSettleReading(bool known, float pixels, float driftPixels, bool driftBoundFired, int driftReturns,
			int settledRows, int requiredRows, bool passes)
		{
			Known = known;
			Pixels = pixels;
			DriftPixels = driftPixels;
			DriftBoundFired = driftBoundFired;
			DriftReturns = driftReturns;
			SettledRows = settledRows;
			RequiredRows = requiredRows;
			Passes = passes;
		}

		/// <summary>Whether a step has been read since the body was last put back to its guide.</summary>
		public bool Known { get; }

		/// <summary>
		/// How far the refined pose moved from the pose of the step before, as the median on-screen displacement of the model's
		/// points in pixels. Not a number on the first step, after a return to the guide and on a step whose pose could not be read.
		/// </summary>
		public float Pixels { get; }

		/// <summary>
		/// How far the refined pose stands from the guide, as the median on-screen displacement of the model's points in
		/// pixels. Not a number when it could not be measured.
		/// </summary>
		public float DriftPixels { get; }

		/// <summary>Whether this step stood further from the guide than the bound allows, so the refinement went back to the guide.</summary>
		public bool DriftBoundFired { get; }

		/// <summary>How many times the refinement has gone back to the guide since the body registered.</summary>
		public int DriftReturns { get; }

		/// <summary>How many steps in a row have moved no further than the limit.</summary>
		public int SettledRows { get; }

		/// <summary>How many steps in a row a start needs.</summary>
		public int RequiredRows { get; }

		/// <summary>Whether the settle check lets a start through.</summary>
		public bool Passes { get; }
	}

	/// <summary>
	/// The rules of the settled start. Before the first start the tracker refines the body from the pose of the step before,
	/// so the refinement can finish its work, and a start is taken only once the refined pose has stopped moving. The pose
	/// has stopped when the model's outline moves by no more than a number of pixels between two steps, which is a measure
	/// that scales with the size of the object and its distance, so one limit suits a small part and a large one. The
	/// refinement may also not wander. When the refined pose stands further from the guide than a bound in the same pixels,
	/// the refinement goes back to the guide and the count of settled steps starts again.
	/// </summary>
	internal static class StartSettle
	{
		/// <summary>The default number of steps in a row that must be settled.</summary>
		internal const int DEFAULT_ROWS = 3;

		/// <summary>The default most the outline may move between two steps, in pixels.</summary>
		internal const float DEFAULT_PIXELS = 1f;

		/// <summary>The default most the refined pose may stand from the guide, in pixels. It is the top of the close band.</summary>
		internal const float DEFAULT_MAX_DRIFT_PIXELS = 16f;

		internal const int MAX_ROWS = 60;
		internal const float MIN_PIXELS = 0.1f;
		internal const float MAX_PIXELS = 10f;
		internal const float MIN_MAX_DRIFT_PIXELS = 1f;
		internal const float MAX_MAX_DRIFT_PIXELS = 200f;

		/// <summary>The setting as the body reads it, either its own or the override of the tooling.</summary>
		internal readonly struct Setting
		{
			internal Setting(bool on, int rows, float pixels, float maxDriftPixels)
			{
				On = on;
				Rows = rows;
				Pixels = pixels;
				MaxDriftPixels = maxDriftPixels;
			}

			internal bool On { get; }
			internal int Rows { get; }
			internal float Pixels { get; }
			internal float MaxDriftPixels { get; }
		}

		/// <summary>The refined pose of the step before, in the tracker's own frame, and how many steps in a row have been settled.</summary>
		internal struct State
		{
			internal bool HasPrevious;
			internal Vector3 Position;
			internal Quaternion Rotation;
			internal int SettledRows;
			internal int DriftReturns;
			internal StartSettleReading Last;
		}

		internal static int ClampRows(int value) => Mathf.Clamp(value, 1, MAX_ROWS);

		internal static float ClampPixels(float value) =>
			float.IsNaN(value) ? DEFAULT_PIXELS : Mathf.Clamp(value, MIN_PIXELS, MAX_PIXELS);

		internal static float ClampMaxDriftPixels(float value) =>
			float.IsNaN(value) ? DEFAULT_MAX_DRIFT_PIXELS : Mathf.Clamp(value, MIN_MAX_DRIFT_PIXELS, MAX_MAX_DRIFT_PIXELS);

		/// <summary>
		/// Whether a refined pose stands too far from the guide. A distance that is not a number could not be measured and
		/// is not too far, and an infinite distance puts the pose behind the camera and is too far.
		/// </summary>
		internal static bool ExceedsDrift(float driftPixels, in Setting setting) =>
			driftPixels > ClampMaxDriftPixels(setting.MaxDriftPixels);

		/// <summary>
		/// Takes up the refined pose of a step, the pixels it moved by since the step before and the pixels it stands from the
		/// guide. A step past the drift bound clears the count and the pose of the step before, counts a return, and says
		/// that the refinement has to go back to the guide. Any other step is judged for its movement. The first step after the
		/// state was last cleared has nothing to compare with and counts no row. A later step counts a row when it moved no
		/// further than the limit, and any other step, a step whose movement could not be measured included, starts the
		/// count again.
		/// </summary>
		/// <param name="pixels">The movement since the step before, or not a number when there is none to measure.</param>
		/// <param name="driftPixels">The distance from the guide, or not a number when it could not be measured.</param>
		/// <returns>True when the drift bound fired on this step.</returns>
		internal static bool Take(ref State state, in Setting setting, float pixels, float driftPixels, Vector3 position, Quaternion rotation)
		{
			int required = ClampRows(setting.Rows);
			if (ExceedsDrift(driftPixels, setting))
			{
				state.HasPrevious = false;
				state.SettledRows = 0;
				++state.DriftReturns;
				state.Last = new StartSettleReading(true, float.NaN, driftPixels, true, state.DriftReturns, 0, required, false);
				return true;
			}

			bool measured = state.HasPrevious && !float.IsNaN(pixels);
			bool settled = measured && pixels <= ClampPixels(setting.Pixels);
			state.SettledRows = settled ? state.SettledRows + 1 : 0;
			state.HasPrevious = true;
			state.Position = position;
			state.Rotation = rotation;
			state.Last = new StartSettleReading(true, measured ? pixels : float.NaN, driftPixels, false, state.DriftReturns,
				state.SettledRows, required, state.SettledRows >= required);
			return false;
		}

		/// <summary>
		/// A step whose pose could not be read counts no row and leaves nothing to compare the next step with.
		/// </summary>
		internal static void Lose(ref State state, in Setting setting)
		{
			state.HasPrevious = false;
			state.SettledRows = 0;
			state.Last = new StartSettleReading(true, float.NaN, float.NaN, false, state.DriftReturns, 0, ClampRows(setting.Rows), false);
		}
	}
}
