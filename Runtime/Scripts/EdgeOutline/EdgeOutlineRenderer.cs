using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;
#if HAS_BURST
using Unity.Burst;
using Unity.Mathematics;
#endif

namespace IV.FormulaTracker
{
	/// <summary>
	/// Abstract base for edge outline rendering. Extracts crease and silhouette edges
	/// from meshes and exposes them as a quad mesh for a render feature.
	/// Subclasses provide the mesh sources via CollectMeshFilters().
	/// Uses Jobs + Burst for zero-GC per-frame mesh rebuilds.
	/// </summary>
	public abstract class EdgeOutlineRenderer : MonoBehaviour
	{
		static readonly HashSet<EdgeOutlineRenderer> s_instances = new();
		public static IReadOnlyCollection<EdgeOutlineRenderer> Instances => s_instances;

		[Header("Edge Detection")]
		[Tooltip("Minimum dihedral angle (degrees) for an edge to be a crease.")]
		[Range(5f, 180f)]
		[SerializeField] float _creaseAngle = 60;

		[Header("Rendering")]
		[Tooltip("Show edges inside the mesh silhouette (internal geometry). When off, only the outer contour is drawn.")]
		[SerializeField] bool _showInternalEdges;

		[Tooltip("Hide the source mesh (edges only). Uses forceRenderingOff on child renderers.")]
		[SerializeField] bool _hideSourceMesh;

		[Header("Width")]
		[Tooltip("Edge outline width in screen-space pixels.")]
		[Range(1f, 10f)]
		[SerializeField] float _edgeWidth = 2f;

		[Header("Color")]
		[SerializeField] Color _edgeColor = new Color(0f, 1f, 1f, 0.9f);

		// Managed edge data used during BuildEdges (dictionary-heavy, not suitable for Jobs)
		struct EdgeData
		{
			public int v0Idx, v1Idx;
			public Vector3 n0, n1;
			public bool isCrease;
		}

		// Blittable edge data for NativeArray + Burst
		struct NativeEdgeData
		{
			public int v0Idx, v1Idx;
			public Vector3 n0, n1;
			public byte isCrease; // 1 = crease (always drawn), 0 = silhouette (view-dependent)
		}

		List<EdgeData> _edges;
		List<Vector3> _weldedVerts;
		Mesh _edgeMesh;
		IList<MeshFilter> _meshFilters;
		IList<Renderer> _renderers;
		float _builtAngle;
		bool _dirty = true;

		// Persistent NativeArrays: edge data (rebuilt only when dirty)
		NativeArray<NativeEdgeData> _nativeEdges;
		NativeArray<Vector3> _nativeVerts;

		// Persistent NativeArrays: output buffers (pre-allocated at max size)
		NativeArray<Vector3> _outVertices;
		NativeArray<Vector3> _outNormals;
		NativeArray<Vector2> _outUvs;
		NativeArray<int> _outIndices;
		NativeArray<int> _outLineCount; // single element: actual visible edge count

		public Mesh EdgeMesh => _edgeMesh;

		public float CreaseAngle
		{
			get => _creaseAngle;
			set { _creaseAngle = value; _dirty = true; }
		}

		public float EdgeWidth
		{
			get => _edgeWidth;
			set => _edgeWidth = value;
		}

		public Color EdgeColor
		{
			get => _edgeColor;
			set => _edgeColor = value;
		}

		public bool ShowInternalEdges
		{
			get => _showInternalEdges;
			set => _showInternalEdges = value;
		}

		public bool HideSourceMesh
		{
			get => _hideSourceMesh;
			set { _hideSourceMesh = value; ApplyHideSourceMesh(); }
		}

		protected abstract IList<MeshFilter> CollectMeshFilters();

		public IList<MeshFilter> GetMeshFilters()
		{
			if (_meshFilters == null || _meshFilters.Count == 0)
				_meshFilters = CollectMeshFilters();
			return _meshFilters;
		}

		IList<Renderer> GetRenderers()
		{
			if (_renderers == null || _renderers.Count == 0)
			{
				var filters = GetMeshFilters();
				var list = new List<Renderer>(filters.Count);
				foreach (var mf in filters)
				{
					var r = mf != null ? mf.GetComponent<Renderer>() : null;
					if (r != null) list.Add(r);
				}
				_renderers = list;
			}
			return _renderers;
		}

		protected virtual void OnEnable()
		{
			s_instances.Add(this);
			_dirty = true;
			if (_hideSourceMesh)
				ApplyHideSourceMesh();
			EnsureBuiltInRendererIfNeeded();
		}

		protected virtual void OnDisable()
		{
			s_instances.Remove(this);
			if (_hideSourceMesh)
				SetForceRenderingOff(false);
			DisposeNativeArrays();
			if (_edgeMesh != null)
				DestroyImmediate(_edgeMesh);
			_edgeMesh = null;
		}

		/// <summary>
		/// If no SRP is active (built-in pipeline), auto-add the CommandBuffer-based
		/// fallback to Camera.main. When URP is active, EdgeOutlineFeature handles rendering.
		/// </summary>
		static void EnsureBuiltInRendererIfNeeded()
		{
			if (GraphicsSettings.currentRenderPipeline != null)
				return;

			var cam = Camera.main;
			if (cam != null && cam.GetComponent<EdgeOutlineBuiltIn>() == null)
				cam.gameObject.AddComponent<EdgeOutlineBuiltIn>();
		}

		protected virtual void OnValidate()
		{
			if (Mathf.Abs(_builtAngle - _creaseAngle) > 0.1f)
				_dirty = true;
		}

		void ApplyHideSourceMesh()
		{
			SetForceRenderingOff(_hideSourceMesh);
		}

		void SetForceRenderingOff(bool off)
		{
			var renderers = GetRenderers();
			foreach (var r in renderers)
			{
				if (r != null)
					r.forceRenderingOff = off;
			}
		}

		void DisposeNativeArrays()
		{
			if (_nativeEdges.IsCreated) _nativeEdges.Dispose();
			if (_nativeVerts.IsCreated) _nativeVerts.Dispose();
			if (_outVertices.IsCreated) _outVertices.Dispose();
			if (_outNormals.IsCreated) _outNormals.Dispose();
			if (_outUvs.IsCreated) _outUvs.Dispose();
			if (_outIndices.IsCreated) _outIndices.Dispose();
			if (_outLineCount.IsCreated) _outLineCount.Dispose();
		}

		/// <summary>
		/// Called by the render feature each frame. Rebuilds edge data if dirty,
		/// then builds the quad mesh with silhouette edges updated for the current camera.
		/// </summary>
		public void UpdateForCamera(Camera cam)
		{
			if (_dirty) BuildEdges();
			if (!_nativeEdges.IsCreated || _nativeEdges.Length == 0) return;

			BuildLineMesh(cam);
		}

		/// <summary>
		/// Force a rebuild of the edge data on the next frame.
		/// Call this when the mesh source changes.
		/// </summary>
		public void SetDirty()
		{
			_dirty = true;
			_meshFilters = null;
			_renderers = null;
		}

		#region Edge Building

		static (int, int, int) Quantize(Vector3 v)
		{
			return (
				Mathf.RoundToInt(v.x * 10000f),
				Mathf.RoundToInt(v.y * 10000f),
				Mathf.RoundToInt(v.z * 10000f)
			);
		}

		int GetWeldedIndex(Dictionary<(int, int, int), int> weldMap, Vector3 pos)
		{
			var key = Quantize(pos);
			if (weldMap.TryGetValue(key, out int idx))
				return idx;
			idx = _weldedVerts.Count;
			_weldedVerts.Add(pos);
			weldMap[key] = idx;
			return idx;
		}

		void BuildEdges()
		{
			// Dispose previous NativeArrays
			DisposeNativeArrays();

			var meshFilters = CollectMeshFilters();

			_edges = new List<EdgeData>();
			_weldedVerts = new List<Vector3>();

			float cosThresh = Mathf.Cos(_creaseAngle * Mathf.Deg2Rad);

			// Shared across all meshes so edges at mesh boundaries accumulate faces
			// from both meshes and are correctly classified as internal, not silhouette.
			var weldMap = new Dictionary<(int, int, int), int>();
			var edgeMap = new Dictionary<(int, int), List<int>>();
			var faceNormals = new List<Vector3>();

			foreach (var mf in meshFilters)
			{
				if (mf == null || mf.sharedMesh == null) continue;
				ProcessMesh(mf, weldMap, edgeMap, faceNormals);
			}

			// Classify edges now that all meshes have contributed faces
			foreach (var kvp in edgeMap)
			{
				var (a, b) = kvp.Key;
				var faces = kvp.Value;

				if (faces.Count == 1)
				{
					var n = faceNormals[faces[0]];
					_edges.Add(new EdgeData
					{
						v0Idx = a, v1Idx = b,
						n0 = n, n1 = n,
						isCrease = true
					});
					continue;
				}

				var n0 = faceNormals[faces[0]];
				var n1 = faceNormals[faces[1]];
				float dot = Vector3.Dot(n0, n1);

				_edges.Add(new EdgeData
				{
					v0Idx = a, v1Idx = b,
					n0 = n0, n1 = n1,
					isCrease = faces.Count > 2 || dot < cosThresh
				});
			}

			_builtAngle = _creaseAngle;
			_dirty = false;
			_meshFilters = null;
			_renderers = null;

			// Copy managed data to persistent NativeArrays, filtering out crease edges if disabled
			int edgeCount = _edges.Count;
			if (edgeCount == 0) return;

			_nativeEdges = new NativeArray<NativeEdgeData>(edgeCount, Allocator.Persistent);
			int nativeCount = 0;
			for (int i = 0; i < edgeCount; i++)
			{
				var e = _edges[i];
				_nativeEdges[i] = new NativeEdgeData
				{
					v0Idx = e.v0Idx, v1Idx = e.v1Idx,
					n0 = e.n0, n1 = e.n1,
					isCrease = e.isCrease ? (byte)1 : (byte)0
				};
			}

			if (edgeCount == 0)
			{
				_nativeEdges.Dispose();
				return;
			}

			_nativeVerts = new NativeArray<Vector3>(_weldedVerts.Count, Allocator.Persistent);
			for (int i = 0; i < _weldedVerts.Count; i++)
				_nativeVerts[i] = _weldedVerts[i];

			// Pre-allocate output buffers at max size (all edges visible)
			_outVertices = new NativeArray<Vector3>(edgeCount * 4, Allocator.Persistent);
			_outNormals = new NativeArray<Vector3>(edgeCount * 4, Allocator.Persistent);
			_outUvs = new NativeArray<Vector2>(edgeCount * 4, Allocator.Persistent);
			_outIndices = new NativeArray<int>(edgeCount * 6, Allocator.Persistent);
			_outLineCount = new NativeArray<int>(1, Allocator.Persistent);

			// Release managed lists — data lives in NativeArrays now
			_edges = null;
			_weldedVerts = null;
		}

		void ProcessMesh(MeshFilter mf,
			Dictionary<(int, int, int), int> weldMap,
			Dictionary<(int, int), List<int>> edgeMap,
			List<Vector3> faceNormals)
		{
			var mesh = mf.sharedMesh;
			var localMat = LocalMatrixTo(mf.transform, transform);
			var normalMat = localMat;

			var meshVerts = mesh.vertices;
			var meshTris = mesh.triangles;
			var meshNormals = mesh.normals;

			var remap = new int[meshVerts.Length];
			for (int v = 0; v < meshVerts.Length; v++)
				remap[v] = GetWeldedIndex(weldMap, localMat.MultiplyPoint3x4(meshVerts[v]));

			int faceCount = meshTris.Length / 3;
			int faceBase = faceNormals.Count;

			for (int f = 0; f < faceCount; f++)
			{
				int ti0 = meshTris[f * 3];
				int ti1 = meshTris[f * 3 + 1];
				int ti2 = meshTris[f * 3 + 2];

				var n = meshNormals[ti0] + meshNormals[ti1] + meshNormals[ti2];
				faceNormals.Add(normalMat.MultiplyVector(n).normalized);
			}

			for (int f = 0; f < faceCount; f++)
			{
				int i0 = remap[meshTris[f * 3]];
				int i1 = remap[meshTris[f * 3 + 1]];
				int i2 = remap[meshTris[f * 3 + 2]];
				if (i0 == i1 || i1 == i2 || i2 == i0) continue;
				AddFace(edgeMap, i0, i1, faceBase + f);
				AddFace(edgeMap, i1, i2, faceBase + f);
				AddFace(edgeMap, i2, i0, faceBase + f);
			}
		}

		static Matrix4x4 LocalMatrixTo(Transform child, Transform ancestor)
		{
			if (child == ancestor) return Matrix4x4.identity;
			var chain = new List<Matrix4x4>();
			var cur = child;
			while (cur != null && cur != ancestor)
			{
				chain.Add(Matrix4x4.TRS(cur.localPosition, cur.localRotation, cur.localScale));
				cur = cur.parent;
			}
			var m = Matrix4x4.identity;
			for (int i = chain.Count - 1; i >= 0; i--)
				m *= chain[i];
			return m;
		}

		static void AddFace(Dictionary<(int, int), List<int>> map, int a, int b, int face)
		{
			var key = a < b ? (a, b) : (b, a);
			if (!map.TryGetValue(key, out var list))
			{
				list = new List<int>(2);
				map[key] = list;
			}
			list.Add(face);
		}

		#endregion

		#region Line Mesh Building (Jobs + Burst)

#if HAS_BURST
		[BurstCompile]
#endif
		struct BuildEdgeMeshJob : IJob
		{
			[ReadOnly] public NativeArray<NativeEdgeData> edges;
			[ReadOnly] public NativeArray<Vector3> verts;

			public Vector3 camLocal;
			public Vector3 camFwdLocal;
			public int isOrtho; // 1 = orthographic, 0 = perspective

			[NativeDisableParallelForRestriction]
			public NativeArray<Vector3> outVertices;
			[NativeDisableParallelForRestriction]
			public NativeArray<Vector3> outNormals;
			[NativeDisableParallelForRestriction]
			public NativeArray<Vector2> outUvs;
			[NativeDisableParallelForRestriction]
			public NativeArray<int> outIndices;

			[WriteOnly] public NativeArray<int> outLineCount;

			public void Execute()
			{
				int vi = 0;
				int ii = 0;

				for (int i = 0; i < edges.Length; i++)
				{
					var e = edges[i];

					Vector3 p0 = verts[e.v0Idx];
					Vector3 p1 = verts[e.v1Idx];

					// ShouldDraw
					if (e.isCrease == 0)
					{
						Vector3 mid;
						mid.x = (p0.x + p1.x) * 0.5f;
						mid.y = (p0.y + p1.y) * 0.5f;
						mid.z = (p0.z + p1.z) * 0.5f;

						Vector3 viewDir;
						if (isOrtho == 1)
						{
							viewDir.x = -camFwdLocal.x;
							viewDir.y = -camFwdLocal.y;
							viewDir.z = -camFwdLocal.z;
						}
						else
						{
							viewDir.x = camLocal.x - mid.x;
							viewDir.y = camLocal.y - mid.y;
							viewDir.z = camLocal.z - mid.z;
						}

						float invLen = 1f / (viewDir.x * viewDir.x + viewDir.y * viewDir.y + viewDir.z * viewDir.z + 1e-12f);
						float d0 = (e.n0.x * viewDir.x + e.n0.y * viewDir.y + e.n0.z * viewDir.z) * invLen;
						float d1 = (e.n1.x * viewDir.x + e.n1.y * viewDir.y + e.n1.z * viewDir.z) * invLen;
						if (d0 * d1 > 0.005f) continue;
					}

					// Compute side expansion
					float sideA, sideB;
					if (e.isCrease == 1)
					{
						sideA = -1f;
						sideB = 1f;
					}
					else
					{
						Vector3 mid;
						mid.x = (p0.x + p1.x) * 0.5f;
						mid.y = (p0.y + p1.y) * 0.5f;
						mid.z = (p0.z + p1.z) * 0.5f;

						Vector3 viewDir;
						if (isOrtho == 1)
						{
							viewDir.x = -camFwdLocal.x;
							viewDir.y = -camFwdLocal.y;
							viewDir.z = -camFwdLocal.z;
						}
						else
						{
							viewDir.x = camLocal.x - mid.x;
							viewDir.y = camLocal.y - mid.y;
							viewDir.z = camLocal.z - mid.z;
						}

						float d0 = e.n0.x * viewDir.x + e.n0.y * viewDir.y + e.n0.z * viewDir.z;
						Vector3 outward = d0 < 0 ? e.n0 : e.n1;

						// Cross(v1 - v0, viewDir)
						Vector3 edge;
						edge.x = p1.x - p0.x;
						edge.y = p1.y - p0.y;
						edge.z = p1.z - p0.z;

						Vector3 cross;
						cross.x = edge.y * viewDir.z - edge.z * viewDir.y;
						cross.y = edge.z * viewDir.x - edge.x * viewDir.z;
						cross.z = edge.x * viewDir.y - edge.y * viewDir.x;

						float sign = cross.x * outward.x + cross.y * outward.y + cross.z * outward.z;
						if (sign >= 0)
						{
							sideA = 0f;
							sideB = 2f;
						}
						else
						{
							sideA = -2f;
							sideB = 0f;
						}
					}

					// Write 4 vertices
					outVertices[vi]     = p0;
					outNormals[vi]      = p1;
					outUvs[vi]          = new Vector2(sideA, 0f);

					outVertices[vi + 1] = p0;
					outNormals[vi + 1]  = p1;
					outUvs[vi + 1]      = new Vector2(sideB, 0f);

					outVertices[vi + 2] = p1;
					outNormals[vi + 2]  = p0;
					outUvs[vi + 2]      = new Vector2(-sideA, 0f);

					outVertices[vi + 3] = p1;
					outNormals[vi + 3]  = p0;
					outUvs[vi + 3]      = new Vector2(-sideB, 0f);

					// Write 6 indices (two triangles)
					outIndices[ii]     = vi;
					outIndices[ii + 1] = vi + 2;
					outIndices[ii + 2] = vi + 1;
					outIndices[ii + 3] = vi + 1;
					outIndices[ii + 4] = vi + 2;
					outIndices[ii + 5] = vi + 3;

					vi += 4;
					ii += 6;
				}

				outLineCount[0] = vi / 4;
			}
		}

		void BuildLineMesh(Camera cam)
		{
			Vector3 camLocal = transform.InverseTransformPoint(cam.transform.position);
			Vector3 camFwdLocal = transform.InverseTransformDirection(cam.transform.forward);

			var job = new BuildEdgeMeshJob
			{
				edges = _nativeEdges,
				verts = _nativeVerts,
				camLocal = camLocal,
				camFwdLocal = camFwdLocal,
				isOrtho = cam.orthographic ? 1 : 0,
				outVertices = _outVertices,
				outNormals = _outNormals,
				outUvs = _outUvs,
				outIndices = _outIndices,
				outLineCount = _outLineCount
			};

			job.Schedule().Complete();

			int lineCount = _outLineCount[0];
			int vertCount = lineCount * 4;
			int idxCount = lineCount * 6;

			if (_edgeMesh == null)
			{
				_edgeMesh = new Mesh { name = "EdgeOutlineMesh" };
				_edgeMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
				_edgeMesh.hideFlags = HideFlags.HideAndDontSave;
			}

			_edgeMesh.Clear();

			if (lineCount == 0) return;

			_edgeMesh.SetVertices(_outVertices, 0, vertCount);
			_edgeMesh.SetNormals(_outNormals, 0, vertCount);
			_edgeMesh.SetUVs(0, _outUvs, 0, vertCount);
			_edgeMesh.SetIndices(_outIndices, 0, idxCount, MeshTopology.Triangles, 0);
		}

		#endregion
	}
}
