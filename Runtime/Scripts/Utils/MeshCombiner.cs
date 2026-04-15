using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Combines multiple meshes into a single vertex/triangle buffer for tracking.
	/// Handles coordinate conversion and transform application.
	/// </summary>
	public struct CombinedMeshData : IDisposable
	{
		public NativeArray<float> Vertices;
		public NativeArray<float> Normals;
		public NativeArray<int> Triangles;
		public int VertexCount;
		public int TriangleCount;
		public bool HasNormals;

		public IntPtr VerticesPtr
		{
			get
			{
				unsafe { return (IntPtr)Vertices.GetUnsafeReadOnlyPtr(); }
			}
		}

		public IntPtr NormalsPtr
		{
			get
			{
				if (!HasNormals || !Normals.IsCreated) return IntPtr.Zero;
				unsafe { return (IntPtr)Normals.GetUnsafeReadOnlyPtr(); }
			}
		}

		public IntPtr TrianglesPtr
		{
			get
			{
				unsafe { return (IntPtr)Triangles.GetUnsafeReadOnlyPtr(); }
			}
		}

		public void Dispose()
		{
			if (Vertices.IsCreated) Vertices.Dispose();
			if (Normals.IsCreated) Normals.Dispose();
			if (Triangles.IsCreated) Triangles.Dispose();
		}
	}

	public static class MeshCombiner
	{
		/// <summary>
		/// Combines multiple MeshFilters into a single mesh data structure.
		/// Vertices are transformed to the root transform's local space.
		/// Coordinate conversion (Y-flip) and winding order flip are applied for native tracker.
		/// </summary>
		/// <param name="meshFilters">List of MeshFilters to combine</param>
		/// <param name="rootTransform">The root transform to use as local space origin</param>
		/// <returns>Combined mesh data ready for native code</returns>
		public static CombinedMeshData Combine(IList<MeshFilter> meshFilters, Transform rootTransform)
		{
			if (meshFilters == null || meshFilters.Count == 0)
				throw new ArgumentException("At least one MeshFilter required", nameof(meshFilters));

			// Build list of valid meshes and their transforms
			var meshList = new List<Mesh>();
			var transforms = new List<Matrix4x4>();
			Matrix4x4 rootWorldToLocal = rootTransform.worldToLocalMatrix;

			foreach (var mf in meshFilters)
			{
				if (mf == null || mf.sharedMesh == null) continue;
				meshList.Add(mf.sharedMesh);
				transforms.Add(rootWorldToLocal * mf.transform.localToWorldMatrix);
			}

			if (meshList.Count == 0)
				throw new ArgumentException("No valid meshes found in MeshFilters", nameof(meshFilters));

			// Acquire all mesh data in single call
			var meshDataArray = Mesh.AcquireReadOnlyMeshData(meshList);

			// Calculate totals
			int totalVertexCount = 0;
			int totalTriangleCount = 0;
			for (int m = 0; m < meshDataArray.Length; m++)
			{
				totalVertexCount += meshDataArray[m].vertexCount;
				totalTriangleCount += meshList[m].triangles.Length;
			}

			// Check if all meshes have normals
			bool allHaveNormals = true;
			for (int m = 0; m < meshDataArray.Length; m++)
			{
				if (!meshDataArray[m].HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal))
				{
					allHaveNormals = false;
					break;
				}
			}

			// Allocate combined arrays
			var result = new CombinedMeshData
			{
				Vertices = new NativeArray<float>(totalVertexCount * 3, Allocator.Persistent),
				Normals = allHaveNormals
					? new NativeArray<float>(totalVertexCount * 3, Allocator.Persistent)
					: default,
				Triangles = new NativeArray<int>(totalTriangleCount, Allocator.Persistent),
				VertexCount = totalVertexCount,
				TriangleCount = totalTriangleCount / 3,
				HasNormals = allHaveNormals
			};

			int vertexOffset = 0;
			int triangleOffset = 0;
			int vertexIndexOffset = 0;

			for (int m = 0; m < meshDataArray.Length; m++)
			{
				var data = meshDataArray[m];
				var meshToRoot = transforms[m];

				var vertices = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp);
				data.GetVertices(vertices);

				// Transform vertices and apply coordinate conversion
				for (int i = 0; i < vertices.Length; i++)
				{
					Vector3 v = meshToRoot.MultiplyPoint3x4(vertices[i]);
					// Native tracker coordinate convention: flip Y
					result.Vertices[(vertexOffset + i) * 3 + 0] = v.x;
					result.Vertices[(vertexOffset + i) * 3 + 1] = -v.y;
					result.Vertices[(vertexOffset + i) * 3 + 2] = v.z;
				}

				vertices.Dispose();

				// Extract and transform normals (direction only, no translation)
				if (allHaveNormals)
				{
					var normals = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp);
					data.GetNormals(normals);

					for (int i = 0; i < normals.Length; i++)
					{
						Vector3 n = meshToRoot.MultiplyVector(normals[i]).normalized;
						// Native tracker coordinate convention: flip Y
						result.Normals[(vertexOffset + i) * 3 + 0] = n.x;
						result.Normals[(vertexOffset + i) * 3 + 1] = -n.y;
						result.Normals[(vertexOffset + i) * 3 + 2] = n.z;
					}

					normals.Dispose();
				}

				// Copy triangles with offset and flip winding order
				var triangles = meshList[m].triangles;
				for (int i = 0; i < triangles.Length; i += 3)
				{
					result.Triangles[triangleOffset + i + 0] = triangles[i + 0] + vertexIndexOffset;
					result.Triangles[triangleOffset + i + 1] = triangles[i + 2] + vertexIndexOffset;
					result.Triangles[triangleOffset + i + 2] = triangles[i + 1] + vertexIndexOffset;
				}

				vertexOffset += data.vertexCount;
				triangleOffset += triangles.Length;
				vertexIndexOffset += data.vertexCount;
			}

			meshDataArray.Dispose();

			return result;
		}

		/// <summary>
		/// Validates that mesh filters contain valid meshes.
		/// </summary>
		public static bool Validate(IList<MeshFilter> meshFilters, out string error)
		{
			error = null;

			if (meshFilters == null || meshFilters.Count == 0)
			{
				error = "No MeshFilters assigned";
				return false;
			}

			int validCount = 0;
			foreach (var mf in meshFilters)
			{
				if (mf != null && mf.sharedMesh != null)
					validCount++;
			}

			if (validCount == 0)
			{
				error = "No valid meshes found in MeshFilters";
				return false;
			}

			return true;
		}

		/// <summary>
		/// Gets mesh statistics for logging.
		/// </summary>
		public static (int meshCount, int vertexCount, int triangleCount) GetStats(IList<MeshFilter> meshFilters)
		{
			int meshCount = 0;
			int vertexCount = 0;
			int triangleCount = 0;

			foreach (var mf in meshFilters)
			{
				if (mf == null || mf.sharedMesh == null) continue;
				meshCount++;
				vertexCount += mf.sharedMesh.vertexCount;
				triangleCount += mf.sharedMesh.triangles.Length / 3;
			}

			return (meshCount, vertexCount, triangleCount);
		}
	}
}
