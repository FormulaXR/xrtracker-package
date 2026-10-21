using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Shared utility methods for mesh processing and tracking calculations.
	/// Used by TrackedBody, TrackedObject, and SilhouetteModelGeneratorCore.
	/// </summary>
	internal static class TrackedBodyUtils
	{
		/// <summary>
		/// Computes the local transform from child to ancestor using only local transforms.
		/// Avoids world space to prevent floating point precision issues.
		/// </summary>
		/// <param name="child">The child transform</param>
		/// <param name="ancestor">The ancestor transform (e.g., TrackedBody/TrackedObject)</param>
		/// <returns>Matrix that transforms from child local space to ancestor local space</returns>
		public static Matrix4x4 GetLocalTransformRelativeTo(Transform child, Transform ancestor)
		{
			if (child == ancestor)
				return Matrix4x4.identity;

			// Build transform chain from child up to ancestor using only local matrices
			var matrices = new List<Matrix4x4>();
			var current = child;
			while (current != null && current != ancestor)
			{
				matrices.Add(Matrix4x4.TRS(current.localPosition, current.localRotation, current.localScale));
				current = current.parent;
			}

			// Multiply in reverse order (from ancestor down to child)
			var result = Matrix4x4.identity;
			for (int i = matrices.Count - 1; i >= 0; i--)
			{
				result *= matrices[i];
			}

			return result;
		}

		/// <summary>
		/// Compute a hash of the mesh data for change detection.
		/// Uses only local transforms to avoid floating point precision issues with world space.
		/// </summary>
		/// <param name="meshFilters">List of mesh filters to hash</param>
		/// <param name="rootTransform">The root transform to compute relative transforms from</param>
		/// <returns>MD5 hash string of mesh data</returns>
		public static string ComputeMeshHash(IList<MeshFilter> meshFilters, Transform rootTransform)
		{
			using var md5 = MD5.Create();
			var sb = new StringBuilder();

			foreach (var mf in meshFilters)
			{
				if (mf == null || mf.sharedMesh == null) continue;

				var mesh = mf.sharedMesh;
				var localMatrix = GetLocalTransformRelativeTo(mf.transform, rootTransform);

				// Hash vertex count and positions
				sb.Append(mesh.vertexCount);
				foreach (var v in mesh.vertices)
				{
					var tv = localMatrix.MultiplyPoint3x4(v);
					sb.AppendFormat("{0:F4}{1:F4}{2:F4}", tv.x, tv.y, tv.z);
				}

				// Hash triangle indices
				foreach (var t in mesh.triangles)
					sb.Append(t);
			}

			byte[] inputBytes = Encoding.UTF8.GetBytes(sb.ToString());
			byte[] hashBytes = md5.ComputeHash(inputBytes);

			return System.BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
		}

		/// <summary>
		/// Check if two ModelSettings match.
		/// </summary>
		public static bool SettingsMatch(ModelSettings a, ModelSettings b)
		{
			if (a == null || b == null) return a == b;
			a.GetEffectiveElevation(out float aMin, out float aMax);
			b.GetEffectiveElevation(out float bMin, out float bMax);
			a.GetEffectiveHorizontal(out bool aHorizEnabled, out float aHorizMin, out float aHorizMax,
				out Vector3 aFwdDir);
			b.GetEffectiveHorizontal(out bool bHorizEnabled, out float bHorizMin, out float bHorizMax,
				out Vector3 bFwdDir);
			if (!(Mathf.Approximately(a.sphereRadius, b.sphereRadius) &&
			      a.nDivides == b.nDivides &&
			      a.nPoints == b.nPoints &&
			      Mathf.Approximately(a.maxRadiusDepthOffset, b.maxRadiusDepthOffset) &&
			      Mathf.Approximately(a.strideDepthOffset, b.strideDepthOffset) &&
			      a.imageSize == b.imageSize &&
			      Mathf.Approximately(aMin, bMin) &&
			      Mathf.Approximately(aMax, bMax)))
				return false;
			if (a.upAxis != b.upAxis) return false;
			if (aHorizEnabled != bHorizEnabled) return false;
			if (aHorizEnabled)
			{
				if (!Mathf.Approximately(aHorizMin, bHorizMin) ||
				    !Mathf.Approximately(aHorizMax, bHorizMax) ||
				    aFwdDir != bFwdDir)
					return false;
			}
			return true;
		}

		/// <summary>
		/// Computes the combined local-space bounds of all mesh filters.
		/// Bounds are relative to the root transform.
		/// Uses only local transforms to avoid floating point precision issues.
		/// </summary>
		/// <param name="meshFilters">List of mesh filters</param>
		/// <param name="rootTransform">The root transform for local space calculation</param>
		/// <returns>Combined bounds in root's local space</returns>
		public static Bounds ComputeLocalBounds(IList<MeshFilter> meshFilters, Transform rootTransform)
		{
			if (meshFilters == null || meshFilters.Count == 0)
				return new Bounds(Vector3.zero, Vector3.zero);

			Bounds combinedBounds = new Bounds();
			bool first = true;

			foreach (var mf in meshFilters)
			{
				if (mf == null || mf.sharedMesh == null)
					continue;

				var mesh = mf.sharedMesh;
				var localMatrix = GetLocalTransformRelativeTo(mf.transform, rootTransform);

				// Transform each vertex to root local space and encapsulate
				foreach (var vertex in mesh.vertices)
				{
					Vector3 localPoint = localMatrix.MultiplyPoint3x4(vertex);
					if (first)
					{
						combinedBounds = new Bounds(localPoint, Vector3.zero);
						first = false;
					}
					else
					{
						combinedBounds.Encapsulate(localPoint);
					}
				}
			}

			return combinedBounds;
		}

		/// <summary>
		/// Remembers the result of <see cref="ComputeLocalBounds"/> for one body, so that a caller that asks
		/// every frame reads the stored bounds instead of copying every vertex of every mesh again.
		/// </summary>
		/// <remarks>
		/// The stored value is reused only while the same mesh filters hold the same meshes, in the same
		/// order, as when it was computed, and until <see cref="Invalidate"/> is called. A change to the
		/// vertices of a mesh that stays assigned, or to the local transform of a mesh filter, is not seen,
		/// so the owner invalidates the cache when it changes its geometry. One cache serves one root transform.
		/// </remarks>
		internal sealed class LocalBoundsCache
		{
			private MeshFilter[] _filters = System.Array.Empty<MeshFilter>();
			private Mesh[] _meshes = System.Array.Empty<Mesh>();
			private Bounds _bounds;
			private bool _hasValue;

			/// <summary>
			/// Drops the stored bounds, so that the next request computes them again.
			/// </summary>
			internal void Invalidate()
			{
				_hasValue = false;
			}

			/// <summary>
			/// Returns the same bounds <see cref="ComputeLocalBounds"/> returns for these arguments, computing
			/// them only when nothing is stored or the stored value was computed for other meshes.
			/// </summary>
			internal Bounds Get(IList<MeshFilter> meshFilters, Transform rootTransform)
			{
				if (_hasValue && Matches(meshFilters))
					return _bounds;

				_bounds = ComputeLocalBounds(meshFilters, rootTransform);
				Remember(meshFilters);
				_hasValue = true;
				return _bounds;
			}

			private bool Matches(IList<MeshFilter> meshFilters)
			{
				int count = meshFilters == null ? 0 : meshFilters.Count;
				if (count != _filters.Length)
					return false;

				for (int i = 0; i < count; ++i)
				{
					MeshFilter filter = meshFilters[i];
					if (!ReferenceEquals(filter, _filters[i]) || !ReferenceEquals(MeshOf(filter), _meshes[i]))
						return false;
				}

				return true;
			}

			private void Remember(IList<MeshFilter> meshFilters)
			{
				int count = meshFilters == null ? 0 : meshFilters.Count;
				if (count != _filters.Length)
				{
					_filters = new MeshFilter[count];
					_meshes = new Mesh[count];
				}

				for (int i = 0; i < count; ++i)
				{
					_filters[i] = meshFilters[i];
					_meshes[i] = MeshOf(_filters[i]);
				}
			}

			// A destroyed filter or mesh reads as null, which is also how ComputeLocalBounds skips it.
			private static Mesh MeshOf(MeshFilter filter)
			{
				if (filter == null)
					return null;

				Mesh mesh = filter.sharedMesh;
				return mesh == null ? null : mesh;
			}
		}

		/// <summary>
		/// Gets the effective sphere radius for silhouette model generation.
		/// Returns the configured value if positive, otherwise computes auto value (0.8 x diameter).
		/// </summary>
		/// <param name="meshFilters">List of mesh filters</param>
		/// <param name="rootTransform">The root transform</param>
		/// <param name="configuredRadius">The configured sphere radius (0 or negative for auto)</param>
		/// <returns>Effective sphere radius in local units</returns>
		public static float GetEffectiveSphereRadius(IList<MeshFilter> meshFilters, Transform rootTransform, float configuredRadius)
		{
			if (configuredRadius > 0)
				return configuredRadius;

			// Auto: 0.8 x object diameter
			Bounds bounds = ComputeLocalBounds(meshFilters, rootTransform);
			float diameter = bounds.size.magnitude;
			return diameter * 0.8f;
		}

		/// <summary>
		/// Collects all MeshFilters under a transform, excluding those under child tracked bodies.
		/// </summary>
		/// <typeparam name="T">The tracked body component type (TrackedBody or TrackedObject)</typeparam>
		/// <param name="root">The root transform to search from</param>
		/// <returns>List of MeshFilters belonging to this tracked body</returns>
		public static List<MeshFilter> CollectMeshFilters<T>(T root) where T : Component
		{
			var result = new List<MeshFilter>();
			var allMeshFilters = root.GetComponentsInChildren<MeshFilter>(true);
			var childTrackedBodies = root.GetComponentsInChildren<T>(true);

			foreach (var meshFilter in allMeshFilters)
			{
				if (meshFilter.sharedMesh == null)
					continue;

				bool isUnderChildTracked = false;
				foreach (var childBody in childTrackedBodies)
				{
					if (!ReferenceEquals(childBody, root) && meshFilter.transform.IsChildOf(childBody.transform))
					{
						isUnderChildTracked = true;
						break;
					}
				}

				if (!isUnderChildTracked)
					result.Add(meshFilter);
			}

			return result;
		}

		/// <summary>
		/// Gets mesh statistics (count, vertices, triangles) from a list of mesh filters.
		/// </summary>
		public static (int meshCount, int totalVertices, int totalTriangles) GetMeshStats(IList<MeshFilter> meshFilters)
		{
			int meshCount = 0;
			int totalVertices = 0;
			int totalTriangles = 0;

			foreach (var mf in meshFilters)
			{
				if (mf == null || mf.sharedMesh == null) continue;
				meshCount++;
				totalVertices += mf.sharedMesh.vertexCount;
				totalTriangles += mf.sharedMesh.triangles.Length / 3;
			}

			return (meshCount, totalVertices, totalTriangles);
		}
	}
}
