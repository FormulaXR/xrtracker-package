using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// The pose a body is initialized at and starts tracking from, held in the world frame
	/// rather than recomputed from the camera on every frame.
	///
	/// The space is identified by a key supplied by the caller, which is always the world key.
	/// </summary>
	internal sealed class RetainedInitializationPose
	{
		/// <summary>Key of the world-origin tracking space, used when no other space is active.</summary>
		public const int WORLD_SPACE_KEY = 0;

		private Matrix4x4 _spaceFromBody = Matrix4x4.identity;

		/// <summary>Whether an initialization pose has been latched.</summary>
		public bool HasPose { get; private set; }

		/// <summary>Key of the tracking space the retained pose is expressed in.</summary>
		public int SpaceKey { get; private set; } = WORLD_SPACE_KEY;

		/// <summary>The retained pose, expressed in the tracking space named by <see cref="SpaceKey"/>.</summary>
		public Matrix4x4 SpaceFromBody => _spaceFromBody;

		/// <summary>
		/// Replaces the retained pose with a world-space pose, stored in the given tracking space.
		/// </summary>
		public void Latch(Matrix4x4 worldFromBody, int spaceKey, Matrix4x4 worldFromSpace)
		{
			_spaceFromBody = worldFromSpace.inverse * worldFromBody;
			SpaceKey = spaceKey;
			HasPose = true;
		}

		/// <summary>Discards the retained pose.</summary>
		public void Clear()
		{
			_spaceFromBody = Matrix4x4.identity;
			SpaceKey = WORLD_SPACE_KEY;
			HasPose = false;
		}

		/// <summary>Whether the retained pose is expressed in the given tracking space.</summary>
		public bool IsInSpace(int spaceKey) => SpaceKey == spaceKey;

		/// <summary>Resolves the retained pose into world space.</summary>
		public Matrix4x4 WorldFromBody(Matrix4x4 worldFromSpace) => worldFromSpace * _spaceFromBody;
	}
}
