using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>What the re-check of the predicted pose does on one held row.</summary>
	internal enum FusionRecheckAction
	{
		/// <summary>Nothing is due.</summary>
		None,

		/// <summary>A cycle begins now, so the predicted pose is written to the tracker and its modality is started clean.</summary>
		Begin,

		/// <summary>The cycle has refined one more row, so the refined pose is judged now. Every refinement row of a cycle is judged.</summary>
		Judge,
	}

	/// <summary>Where a held body's re-check cycle stands.</summary>
	internal struct FusionRecheckState
	{
		/// <summary>Whether a cycle is refining from the predicted pose.</summary>
		public bool Refining;

		/// <summary>Rows the current cycle has refined.</summary>
		public int RowsRefined;

		/// <summary>Held rows in the picture since the last cycle began.</summary>
		public int RowsSinceBegin;

		/// <summary>The consecutive refinement rows of the cycle that cleared the start value and the margin gate, when the re-check is judged on a run.</summary>
		public int RunPasses;
	}

	/// <summary>What the re-check read on one row, for the replay record.</summary>
	internal struct FusionRecheckReading
	{
		/// <summary>Whether a cycle was begun on this row.</summary>
		public bool Began;

		/// <summary>Whether the refined pose was judged on this row.</summary>
		public bool Judged;

		/// <summary>The scalar read on the refined pose.</summary>
		public float Value;

		/// <summary>The start bar it was read against.</summary>
		public float Bar;

		/// <summary>Whether the scalar cleared the start bar.</summary>
		public bool ClearsStart;

		/// <summary>The margin gate's bar, and whether it read, its value and whether it passed.</summary>
		public float GateBar;
		public bool GateRead;
		public float GateValue;
		public bool GatePassed;

		/// <summary>Whether the judgement took the lock back on the refined pose.</summary>
		public bool TookBack;

		/// <summary>When the re-check is judged on a run of rows, the rows in a row that cleared so far and the rows it needs.</summary>
		public int RunPasses;
		public int RunRequired;

		/// <summary>How far the refined pose stood from the predicted pose, in millimetres and degrees.</summary>
		public float Millimetres;
		public float Degrees;
	}

	/// <summary>
	/// The re-check of the predicted pose of a held body under pose fusion. Pure decisions, so each can be tested without a
	/// tracker.
	/// </summary>
	/// <remarks>
	/// <para>The predicted pose of a stationary body under pose fusion is the pose it was placed at, held in the world frame and
	/// composed with the camera pose of each row, which is what the manager already writes to the tracker as the seat. The
	/// re-check does not read the tracker's own wandering pose. Every few rows while the body is held and in the picture it
	/// writes the seat, stops and starts the body's tracking so the next step renders and predicts at the seat and from
	/// nothing older, lets the tracker refine for a few rows, and judges the refined pose of each of those rows on the start value and the margin
	/// gate. A pass takes the lock back at once on the refined pose.</para>
	/// </remarks>
	internal static class FusionRecovery
	{
		/// <summary>Held rows in the picture between the begin of one cycle and the begin of the next.</summary>
		internal const int RECHECK_INTERVAL_ROWS = 5;

		/// <summary>Rows a cycle refines from the predicted pose, each judged.</summary>
		internal const int RECHECK_REFINE_ROWS = 3;

		/// <summary>The bar of the margin gate the refined pose must also read at, on the margin with the sub-pixel median.</summary>
		internal const float RECHECK_GATE_BAR = 0.988f;

		/// <summary>The rows in a row a re-check judged on a run needs, the rows the lifecycle uses for a start.</summary>
		internal const int RECHECK_RUN_ROWS = 5;

		/// <summary>The most rows a re-check judged on a run may ask for.</summary>
		internal const int RECHECK_RUN_MAX = 29;

		/// <summary>Whether the re-check is on. Null is off.</summary>
		internal static bool RecheckOn(bool? requested) => requested == true;

		/// <summary>
		/// Advances a held body's cycle by one row and says what is due. The cycle runs only while the body is in the
		/// picture, and leaving the picture ends it. A cycle begins when <paramref name="intervalRows"/> held rows in the
		/// picture have passed since the last one began (the first row of a hold begins one, through
		/// <paramref name="beginNow"/>), and each of the <paramref name="refineRows"/> rows it refines is judged.
		/// </summary>
		internal static FusionRecheckAction Next(ref FusionRecheckState state, bool inPicture, bool beginNow,
			int intervalRows, int refineRows)
		{
			if (!inPicture)
			{
				// A cycle in progress is dropped, and the first row back in the picture begins the next one.
				state = default;
				state.RowsSinceBegin = intervalRows;
				return FusionRecheckAction.None;
			}
			if (beginNow)
			{
				state.Refining = true;
				state.RowsRefined = 0;
				state.RowsSinceBegin = 0;
				state.RunPasses = 0;
				return FusionRecheckAction.Begin;
			}
			++state.RowsSinceBegin;
			if (state.Refining)
			{
				// Every refinement row is judged, because the first row from a clean modality at the predicted
				// pose is the one that reads the pose itself, and the rows after it walk away from it when the evidence is weak.
				++state.RowsRefined;
				if (state.RowsRefined >= refineRows)
					state.Refining = false;
				return FusionRecheckAction.Judge;
			}
			if (state.RowsSinceBegin >= intervalRows)
			{
				state.Refining = true;
				state.RowsRefined = 0;
				state.RowsSinceBegin = 0;
				state.RunPasses = 0;
				return FusionRecheckAction.Begin;
			}
			return FusionRecheckAction.None;
		}

		/// <summary>The distance and rotation between two poses of one space.</summary>
		internal static void Distance(Vector3 positionA, Quaternion rotationA, Vector3 positionB, Quaternion rotationB,
			out float millimetres, out float degrees)
		{
			millimetres = Vector3.Distance(positionA, positionB) * 1000f;
			degrees = Quaternion.Angle(rotationA, rotationB);
		}

		/// <summary>The rows in a row a re-check needs. Null reads as a single passing row, and a value below 2 reads as one.</summary>
		internal static int RunRowsFor(int? requested) =>
			requested.HasValue && requested.Value >= 2 && requested.Value <= RECHECK_RUN_MAX ? requested.Value : 1;

		/// <summary>
		/// Advances a run of rows by one judged row. A row that clears the start value and the margin gate adds one, and any other row
		/// ends the run. Returns whether the run has reached the rows it needs.
		/// </summary>
		internal static bool RunAdvance(ref int passes, bool rowPasses, int required)
		{
			passes = rowPasses ? passes + 1 : 0;
			return passes >= required;
		}

		/// <summary>Whether the refined pose of a cycle takes the lock back: the start value and the margin gate both clear.</summary>
		internal static bool Passes(bool clearsStart, bool gatePassed) => clearsStart && gatePassed;
	}
}
