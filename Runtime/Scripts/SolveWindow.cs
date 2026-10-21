using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// The newest solves of a stationary body in the world frame, one per tracking step, for telling whether the solve stands still.
	/// </summary>
	/// <remarks>
	/// The object is fixed in the world, so when the fit is steady its solve stands still in the world frame whatever the camera does. The
	/// window holds consecutive steps only. A step that cannot be counted clears it, so a run of rows in the window is a run of rows in a row.
	/// </remarks>
	internal sealed class SolveWindow
	{
		/// <summary>The most solves the window keeps.</summary>
		internal const int CAPACITY = 8;

		private readonly Vector3[] _positions = new Vector3[CAPACITY];
		private readonly Quaternion[] _rotations = new Quaternion[CAPACITY];
		private int _count;

		/// <summary>How many solves the window holds now.</summary>
		internal int Count => _count;

		/// <summary>Forgets every solve.</summary>
		internal void Clear() => _count = 0;

		/// <summary>Carries every solve in the window to the new world frame after the AR session moved its world, so the window keeps the run it holds.</summary>
		internal void Carry(Pose carry)
		{
			for (int index = 0; index < _count; index++)
			{
				_positions[index] = ArWorldJump.Carry(carry, _positions[index]);
				_rotations[index] = ArWorldJump.Carry(carry, _rotations[index]);
			}
		}

		/// <summary>Adds the solve of the newest step, and drops the oldest one when the window is full.</summary>
		internal void Add(Vector3 position, Quaternion rotation)
		{
			if (_count == CAPACITY)
			{
				for (int index = 1; index < CAPACITY; index++)
				{
					_positions[index - 1] = _positions[index];
					_rotations[index - 1] = _rotations[index];
				}
				_count = CAPACITY - 1;
			}
			_positions[_count] = position;
			_rotations[_count] = rotation;
			_count++;
		}

		/// <summary>
		/// The largest distance and the largest angle between any two of the newest <paramref name="rows"/> solves. False while the window holds
		/// fewer solves than that.
		/// </summary>
		internal bool TryGetSpread(int rows, out float spreadMetres, out float spreadDegrees)
		{
			spreadMetres = 0f;
			spreadDegrees = 0f;
			if (rows < 2 || rows > CAPACITY || _count < rows)
				return false;
			for (int first = _count - rows; first < _count; first++)
			{
				for (int second = first + 1; second < _count; second++)
				{
					spreadMetres = Mathf.Max(spreadMetres, Vector3.Distance(_positions[first], _positions[second]));
					spreadDegrees = Mathf.Max(spreadDegrees, Quaternion.Angle(_rotations[first], _rotations[second]));
				}
			}
			return true;
		}

		/// <summary>
		/// Whether the newest <paramref name="rows"/> solves all stand within <paramref name="spreadMetres"/> and <paramref name="spreadDegrees"/>
		/// of each other. False while the window holds fewer solves than that.
		/// </summary>
		internal bool Within(int rows, float spreadMetres, float spreadDegrees) =>
			TryGetSpread(rows, out float metres, out float degrees) && metres <= spreadMetres && degrees <= spreadDegrees;
	}
}
