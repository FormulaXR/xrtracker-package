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

		// Blittable edge data for NativeArray + Burst (silhouette-only; creases baked into _creaseMesh)
		struct NativeEdgeData
		{
			public int v0Idx, v1Idx;
			public Vector3 n0, n1;
		}

		List<EdgeData> _edges;
		List<Vector3> _weldedVerts;
		Mesh _edgeMesh;         // Dynamic silhouette mesh, rebuilt each frame
		Mesh _creaseMesh;       // Static crease mesh, built once in BuildEdges
		Mesh _occlusionMesh;    // Combined occlusion mesh, built once in BuildEdges
		IList<MeshFilter> _meshFilters;
		IList<Renderer> _renderers;
		float _builtAngle;
		bool _dirty = true;

		// Per-edge scratch data produced by phase 1 (parallel compute) and consumed
		// by phase 2 (serial pack). Stored per edge index, not per slot.
		struct EdgeResult
		{
			public Vector3 p0;
			public Vector3 p1;
			public float sideA;
			public float sideB;
			public int visible;
		}

		// Persistent NativeArrays: silhouette edge data (rebuilt only when dirty)
		NativeArray<NativeEdgeData> _nativeSilhouetteEdges;
		NativeArray<Vector3> _nativeVerts;

		// Persistent buffers reused every frame — sized to the worst case at build
		// time so the per-frame path makes zero allocations.
		NativeArray<EdgeResult> _edgeResults;        // phase 1 output / phase 2 input
		NativeArray<Vector3> _nativePositions;       // phase 2 output, uploaded
		NativeArray<Vector3> _nativeNormalsBuf;      // phase 2 output, uploaded
		NativeArray<Vector2> _nativeUvsBuf;          // phase 2 output, uploaded
		NativeArray<int> _outLineCount;              // phase 2 writes visible count
		int _maxVerts;
		int _maxIndices;

		// Pending job chain kicked at beginCameraRendering, completed inside the
		// render pass — overlaps the Burst work with URP's culling/shadow setup.
		JobHandle _pendingHandle;
		Camera _pendingForCam;

		// Cached AABB of the welded vertex set, in outline local space — reused as the
		// dynamic silhouette mesh's bounds so we skip per-frame RecalculateBounds.
		Bounds _contentBounds;

		public Mesh EdgeMesh => _edgeMesh;
		public Mesh CreaseMesh => _creaseMesh;
		public Mesh OcclusionMesh => _occlusionMesh;

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
			RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
		}

		protected virtual void OnDisable()
		{
			RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
			s_instances.Remove(this);
			if (_hideSourceMesh)
				SetForceRenderingOff(false);
			// Flush any in-flight job before disposing its input/output buffers.
			_pendingHandle.Complete();
			_pendingForCam = null;
			DisposeNativeArrays();
			if (_edgeMesh != null)
				DestroyImmediate(_edgeMesh);
			_edgeMesh = null;
			if (_creaseMesh != null)
				DestroyImmediate(_creaseMesh);
			_creaseMesh = null;
			if (_occlusionMesh != null)
				DestroyImmediate(_occlusionMesh);
			_occlusionMesh = null;
		}

		void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
		{
			// Only pre-schedule for cameras the feature will actually render for
			// (mirrors EdgeOutlineFeature.AddRenderPasses filter).
			if (cam.cameraType != CameraType.SceneView && !cam.CompareTag("MainCamera"))
				return;
			ScheduleBuildForCamera(cam);
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
			if (_nativeSilhouetteEdges.IsCreated) _nativeSilhouetteEdges.Dispose();
			if (_nativeVerts.IsCreated) _nativeVerts.Dispose();
			if (_edgeResults.IsCreated) _edgeResults.Dispose();
			if (_nativePositions.IsCreated) _nativePositions.Dispose();
			if (_nativeNormalsBuf.IsCreated) _nativeNormalsBuf.Dispose();
			if (_nativeUvsBuf.IsCreated) _nativeUvsBuf.Dispose();
			if (_outLineCount.IsCreated) _outLineCount.Dispose();
			_maxVerts = 0;
			_maxIndices = 0;
		}

		/// <summary>
		/// Called by the render feature each frame from inside the render pass.
		/// If a job was pre-scheduled at beginCameraRendering for this camera,
		/// waits for it and uploads the result; otherwise falls back to an inline
		/// schedule+complete (e.g. built-in pipeline, first frame, editor reload).
		/// </summary>
		public void UpdateForCamera(Camera cam)
		{
			if (_pendingForCam != cam)
				ScheduleBuildForCamera(cam);

			if (!_nativeSilhouetteEdges.IsCreated || _nativeSilhouetteEdges.Length == 0)
				return;

			CompleteAndUploadLineMesh();
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

			// Destroy any previously-built static meshes before rebuilding.
			if (_creaseMesh != null)
				DestroyImmediate(_creaseMesh);
			_creaseMesh = null;
			if (_occlusionMesh != null)
				DestroyImmediate(_occlusionMesh);
			_occlusionMesh = null;

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

			int edgeCount = _edges.Count;
			if (edgeCount == 0)
			{
				BuildStaticCreaseMesh(0);
				_edges = null;
				_weldedVerts = null;
				return;
			}

			// Cache the local-space AABB of the welded verts — used as bounds for the
			// dynamic silhouette mesh so we skip RecalculateBounds every frame.
			ComputeContentBounds();

			// Count creases and silhouettes separately
			int creaseCount = 0;
			int silhouetteCount = 0;
			for (int i = 0; i < edgeCount; i++)
			{
				if (_edges[i].isCrease) creaseCount++;
				else silhouetteCount++;
			}

			// Build the static crease mesh once — view-independent, baked into a permanent Mesh
			BuildStaticCreaseMesh(creaseCount);

			// Build the combined occlusion mesh once — collapses Pass 1's per-submesh
			// cmd.DrawMesh loop to a single draw call per outline.
			BuildCombinedOcclusionMesh(meshFilters);

			// Pack silhouette edges into a native array for the per-frame job
			if (silhouetteCount > 0)
			{
				_nativeVerts = new NativeArray<Vector3>(_weldedVerts.Count, Allocator.Persistent);
				for (int i = 0; i < _weldedVerts.Count; i++)
					_nativeVerts[i] = _weldedVerts[i];

				_nativeSilhouetteEdges = new NativeArray<NativeEdgeData>(silhouetteCount, Allocator.Persistent);
				int si = 0;
				for (int i = 0; i < edgeCount; i++)
				{
					var e = _edges[i];
					if (e.isCrease) continue;
					_nativeSilhouetteEdges[si++] = new NativeEdgeData
					{
						v0Idx = e.v0Idx, v1Idx = e.v1Idx,
						n0 = e.n0, n1 = e.n1
					};
				}

				_outLineCount = new NativeArray<int>(1, Allocator.Persistent);

				// Persistent scratch / vertex buffers — sized to the worst case so the
				// per-frame pipeline is allocation-free.
				_maxVerts = silhouetteCount * 4;
				_maxIndices = silhouetteCount * 6;
				_edgeResults = new NativeArray<EdgeResult>(silhouetteCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
				_nativePositions = new NativeArray<Vector3>(_maxVerts, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
				_nativeNormalsBuf = new NativeArray<Vector3>(_maxVerts, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
				_nativeUvsBuf = new NativeArray<Vector2>(_maxVerts, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

				// One-time mesh layout configuration. Per-frame path only uploads data.
				if (_edgeMesh == null)
				{
					_edgeMesh = new Mesh { name = "EdgeOutlineMesh" };
					_edgeMesh.indexFormat = IndexFormat.UInt32;
					_edgeMesh.hideFlags = HideFlags.HideAndDontSave;
					_edgeMesh.MarkDynamic();
				}

				var attrs = new NativeArray<VertexAttributeDescriptor>(3, Allocator.Temp);
				attrs[0] = new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3, 0);
				attrs[1] = new VertexAttributeDescriptor(VertexAttribute.Normal,    VertexAttributeFormat.Float32, 3, 1);
				attrs[2] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 2);
				_edgeMesh.SetVertexBufferParams(_maxVerts, attrs);
				attrs.Dispose();
				_edgeMesh.SetIndexBufferParams(_maxIndices, IndexFormat.UInt32);

				// Static index buffer: each quad K occupies vertex slots K*4..K*4+3
				// regardless of which edge populates it. The pack job emits visible
				// edges contiguously from slot 0, so the first count*6 indices are
				// always the correct ones. Upload once, never touch again.
				const MeshUpdateFlags kInitFlags =
					MeshUpdateFlags.DontRecalculateBounds |
					MeshUpdateFlags.DontValidateIndices |
					MeshUpdateFlags.DontResetBoneBounds |
					MeshUpdateFlags.DontNotifyMeshUsers;

				var staticIndices = new NativeArray<int>(_maxIndices, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
				for (int i = 0; i < silhouetteCount; i++)
				{
					int vi = i * 4;
					int ii = i * 6;
					staticIndices[ii]     = vi;
					staticIndices[ii + 1] = vi + 2;
					staticIndices[ii + 2] = vi + 1;
					staticIndices[ii + 3] = vi + 1;
					staticIndices[ii + 4] = vi + 2;
					staticIndices[ii + 5] = vi + 3;
				}
				_edgeMesh.SetIndexBufferData(staticIndices, 0, 0, _maxIndices, kInitFlags);
				staticIndices.Dispose();

				_edgeMesh.subMeshCount = 1;

				// Bounds are content-static, set once.
				_edgeMesh.bounds = _contentBounds;
			}

			// Release managed lists — data lives in NativeArrays / baked mesh now
			_edges = null;
			_weldedVerts = null;
		}

		void ComputeContentBounds()
		{
			if (_weldedVerts == null || _weldedVerts.Count == 0)
			{
				_contentBounds = new Bounds(Vector3.zero, Vector3.zero);
				return;
			}

			Vector3 min = _weldedVerts[0];
			Vector3 max = min;
			for (int i = 1; i < _weldedVerts.Count; i++)
			{
				var v = _weldedVerts[i];
				if (v.x < min.x) min.x = v.x; else if (v.x > max.x) max.x = v.x;
				if (v.y < min.y) min.y = v.y; else if (v.y > max.y) max.y = v.y;
				if (v.z < min.z) min.z = v.z; else if (v.z > max.z) max.z = v.z;
			}
			_contentBounds = new Bounds((min + max) * 0.5f, max - min);
		}

		/// <summary>
		/// Bake crease edges into a permanent Mesh. Crease geometry is view-independent,
		/// so it only needs to be emitted once (on build). Quad expansion mirrors the
		/// crease branch (sideA = -1, sideB = 1 — view-independent).
		/// </summary>
		void BuildStaticCreaseMesh(int creaseCount)
		{
			// Previous mesh is already destroyed in BuildEdges() before this is called.
			if (creaseCount == 0 || _edges == null || _weldedVerts == null) return;

			var verts = new Vector3[creaseCount * 4];
			var normals = new Vector3[creaseCount * 4];
			var uvs = new Vector2[creaseCount * 4];
			var indices = new int[creaseCount * 6];

			int vi = 0;
			int ii = 0;
			int edgeCount = _edges.Count;
			for (int i = 0; i < edgeCount; i++)
			{
				var e = _edges[i];
				if (!e.isCrease) continue;

				Vector3 p0 = _weldedVerts[e.v0Idx];
				Vector3 p1 = _weldedVerts[e.v1Idx];

				// View-independent crease sides: sideA = -1, sideB = 1
				verts[vi]     = p0; normals[vi]     = p1; uvs[vi]     = new Vector2(-1f, 0f);
				verts[vi + 1] = p0; normals[vi + 1] = p1; uvs[vi + 1] = new Vector2( 1f, 0f);
				verts[vi + 2] = p1; normals[vi + 2] = p0; uvs[vi + 2] = new Vector2( 1f, 0f);
				verts[vi + 3] = p1; normals[vi + 3] = p0; uvs[vi + 3] = new Vector2(-1f, 0f);

				indices[ii]     = vi;
				indices[ii + 1] = vi + 2;
				indices[ii + 2] = vi + 1;
				indices[ii + 3] = vi + 1;
				indices[ii + 4] = vi + 2;
				indices[ii + 5] = vi + 3;

				vi += 4;
				ii += 6;
			}

			_creaseMesh = new Mesh { name = "CreaseOutlineMesh" };
			_creaseMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
			_creaseMesh.hideFlags = HideFlags.HideAndDontSave;
			_creaseMesh.vertices = verts;
			_creaseMesh.normals = normals;
			_creaseMesh.uv = uvs;
			_creaseMesh.SetIndices(indices, MeshTopology.Triangles, 0);
			_creaseMesh.RecalculateBounds();
		}

		/// <summary>
		/// Collapse all source meshes (across MeshFilters and submeshes) into a single
		/// position-only mesh in outline-local space. This lets the occlusion pass issue
		/// one cmd.DrawMesh per outline instead of meshFilters × subMeshes draw calls.
		/// </summary>
		void BuildCombinedOcclusionMesh(IList<MeshFilter> meshFilters)
		{
			if (meshFilters == null || meshFilters.Count == 0) return;

			// Count valid submeshes so we can size CombineInstance[] exactly
			int instanceCount = 0;
			foreach (var mf in meshFilters)
			{
				if (mf == null || mf.sharedMesh == null) continue;
				instanceCount += mf.sharedMesh.subMeshCount;
			}
			if (instanceCount == 0) return;

			var instances = new CombineInstance[instanceCount];
			int idx = 0;
			Matrix4x4 worldToOutline = transform.worldToLocalMatrix;
			foreach (var mf in meshFilters)
			{
				if (mf == null || mf.sharedMesh == null) continue;
				Mesh srcMesh = mf.sharedMesh;
				// Transform source mesh into outline-local space so the draw call can
				// use outline.transform.localToWorldMatrix without any per-mesh rebase.
				Matrix4x4 meshToOutline = worldToOutline * mf.transform.localToWorldMatrix;
				for (int sub = 0; sub < srcMesh.subMeshCount; sub++)
				{
					instances[idx].mesh = srcMesh;
					instances[idx].subMeshIndex = sub;
					instances[idx].transform = meshToOutline;
					idx++;
				}
			}

			_occlusionMesh = new Mesh { name = "EdgeOcclusionMesh" };
			_occlusionMesh.hideFlags = HideFlags.HideAndDontSave;
			// Use 32-bit indices up front — CombineMeshes auto-promotes if needed, but
			// setting it explicitly avoids a silent fallback for heavy meshes.
			_occlusionMesh.indexFormat = IndexFormat.UInt32;
			_occlusionMesh.CombineMeshes(instances, mergeSubMeshes: true, useMatrices: true);

			// The occlusion pass only reads position. Strip every other attribute to
			// shrink the vertex buffer and skip any wasted vertex-stream work on GPU.
			var positions = _occlusionMesh.vertices;
			var triangles = _occlusionMesh.triangles;
			var bounds = _occlusionMesh.bounds;
			_occlusionMesh.Clear();
			_occlusionMesh.indexFormat = positions.Length > 65535
				? IndexFormat.UInt32
				: IndexFormat.UInt16;
			_occlusionMesh.SetVertices(positions);
			_occlusionMesh.subMeshCount = 1;
			_occlusionMesh.SetTriangles(triangles, 0, calculateBounds: false);
			_occlusionMesh.bounds = bounds;
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

		// Phase 1: parallel visibility test + side computation. One iteration per
		// edge, writing into the per-edge scratch buffer. No cross-iteration state.
#if HAS_BURST
		[BurstCompile]
#endif
		struct ComputeEdgeJob : IJobParallelFor
		{
			[ReadOnly] public NativeArray<NativeEdgeData> edges;
			[ReadOnly] public NativeArray<Vector3> verts;

			public Vector3 camLocal;
			public Vector3 camFwdLocal;
			public int isOrtho; // 1 = orthographic, 0 = perspective

			[WriteOnly] public NativeArray<EdgeResult> results;

			public void Execute(int i)
			{
				var e = edges[i];
				Vector3 p0 = verts[e.v0Idx];
				Vector3 p1 = verts[e.v1Idx];

				// Silhouette visibility test: only draw edges where one face is front
				// and the other is back relative to the camera.
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
				float dn0 = (e.n0.x * viewDir.x + e.n0.y * viewDir.y + e.n0.z * viewDir.z) * invLen;
				float dn1 = (e.n1.x * viewDir.x + e.n1.y * viewDir.y + e.n1.z * viewDir.z) * invLen;

				if (dn0 * dn1 > 0.005f)
				{
					results[i] = default; // visible = 0
					return;
				}

				// Compute side expansion (silhouette: outward half only)
				float sideA, sideB;
				{
					Vector3 outward = dn0 < 0 ? e.n0 : e.n1;

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

				results[i] = new EdgeResult
				{
					p0 = p0,
					p1 = p1,
					sideA = sideA,
					sideB = sideB,
					visible = 1
				};
			}
		}

		// Phase 2: serial pack. Scans per-edge results, writes visible quads
		// contiguously from slot 0, records the visible count. Tiny work — one
		// pass through edges, four vertex writes per visible edge.
#if HAS_BURST
		[BurstCompile]
#endif
		struct PackEdgeJob : IJob
		{
			[ReadOnly] public NativeArray<EdgeResult> results;

			[WriteOnly] public NativeArray<Vector3> outVertices;
			[WriteOnly] public NativeArray<Vector3> outNormals;
			[WriteOnly] public NativeArray<Vector2> outUvs;
			[WriteOnly] public NativeArray<int> outLineCount;

			public void Execute()
			{
				int vi = 0;
				int count = results.Length;
				for (int i = 0; i < count; i++)
				{
					var r = results[i];
					if (r.visible == 0) continue;

					outVertices[vi]     = r.p0;
					outNormals[vi]      = r.p1;
					outUvs[vi]          = new Vector2(r.sideA, 0f);

					outVertices[vi + 1] = r.p0;
					outNormals[vi + 1]  = r.p1;
					outUvs[vi + 1]      = new Vector2(r.sideB, 0f);

					outVertices[vi + 2] = r.p1;
					outNormals[vi + 2]  = r.p0;
					outUvs[vi + 2]      = new Vector2(-r.sideA, 0f);

					outVertices[vi + 3] = r.p1;
					outNormals[vi + 3]  = r.p0;
					outUvs[vi + 3]      = new Vector2(-r.sideB, 0f);

					vi += 4;
				}
				outLineCount[0] = vi / 4;
			}
		}

		void ScheduleBuildForCamera(Camera cam)
		{
			// Serialize with any still-running chain — the shared scratch/output
			// buffers can only hold one camera's result at a time.
			_pendingHandle.Complete();
			_pendingForCam = null;

			if (_dirty) BuildEdges();
			if (!_nativeSilhouetteEdges.IsCreated || _nativeSilhouetteEdges.Length == 0)
				return;

			Vector3 camLocal = transform.InverseTransformPoint(cam.transform.position);
			Vector3 camFwdLocal = transform.InverseTransformDirection(cam.transform.forward);

			var compute = new ComputeEdgeJob
			{
				edges = _nativeSilhouetteEdges,
				verts = _nativeVerts,
				camLocal = camLocal,
				camFwdLocal = camFwdLocal,
				isOrtho = cam.orthographic ? 1 : 0,
				results = _edgeResults,
			};

			var pack = new PackEdgeJob
			{
				results = _edgeResults,
				outVertices = _nativePositions,
				outNormals = _nativeNormalsBuf,
				outUvs = _nativeUvsBuf,
				outLineCount = _outLineCount,
			};

			// Parallel compute (64 edges per batch), then a tiny serial pack that
			// depends on it. Both run on worker threads.
			JobHandle computeHandle = compute.Schedule(_nativeSilhouetteEdges.Length, 64);
			_pendingHandle = pack.Schedule(computeHandle);
			_pendingForCam = cam;

			// Kick workers now so they can run concurrently with URP culling/shadow
			// setup instead of waiting for the next main-thread safepoint.
			JobHandle.ScheduleBatchedJobs();
		}

		void CompleteAndUploadLineMesh()
		{
			_pendingHandle.Complete();
			_pendingForCam = null;

			int lineCount = _outLineCount[0];
			int vertCount = lineCount * 4;
			int idxCount = lineCount * 6;

			const MeshUpdateFlags kFlags =
				MeshUpdateFlags.DontRecalculateBounds |
				MeshUpdateFlags.DontValidateIndices |
				MeshUpdateFlags.DontResetBoneBounds |
				MeshUpdateFlags.DontNotifyMeshUsers;

			if (vertCount > 0)
			{
				_edgeMesh.SetVertexBufferData(_nativePositions,  0, 0, vertCount, 0, kFlags);
				_edgeMesh.SetVertexBufferData(_nativeNormalsBuf, 0, 0, vertCount, 1, kFlags);
				_edgeMesh.SetVertexBufferData(_nativeUvsBuf,     0, 0, vertCount, 2, kFlags);
			}

			// Static index buffer was uploaded once in BuildEdges; just update the
			// submesh range so the GPU draws only the visible prefix.
			_edgeMesh.SetSubMesh(
				0,
				new SubMeshDescriptor(0, idxCount, MeshTopology.Triangles),
				kFlags);
		}

		#endregion
	}
}
