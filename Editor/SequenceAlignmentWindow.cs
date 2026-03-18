using System;
using System.IO;
using IV.FormulaTracker.Recording;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace IV.FormulaTracker.Editor
{
	/// <summary>
	/// Editor window for finding good initial alignment poses from recorded sequences.
	/// Scrub to a frame, overlay it on the Scene View, adjust the camera until the
	/// 3D model aligns with the image, then use "Align with View" to set the initial pose.
	/// </summary>
	public class SequenceAlignmentWindow : EditorWindow
	{
		[MenuItem("XRTracker/Sequence Alignment")]
		private static void ShowWindow()
		{
			var window = GetWindow<SequenceAlignmentWindow>("Sequence Alignment");
			window.minSize = new Vector2(320, 300);
		}

		// Sequence
		private string _sequenceDirectory = "";
		private SequenceReader _reader;
		private Texture2D _displayTexture;

		// Frame state
		private int _currentFrame;
		private int _imageWidth;
		private int _imageHeight;
		private float _fu, _fv, _ppu, _ppv; // normalized intrinsics for current frame

		// Tracked Body
		private TrackedBody _trackedBody;

		// Overlay
		private float _overlayOpacity = 0.5f;
		private bool _lockProjection = true;
		private bool _projectionOverridden;

		private void OnEnable()
		{
			SceneView.duringSceneGui += OnSceneGUI;
			RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;

			if (string.IsNullOrEmpty(_sequenceDirectory))
				_sequenceDirectory = FindManagerSequenceDirectory();

			if (_trackedBody == null)
				_trackedBody = FindDefaultTrackedBody();
		}

		private void OnDisable()
		{
			SceneView.duringSceneGui -= OnSceneGUI;
			RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
			RestoreProjection();
			CleanupTexture();
		}

		private void OnGUI()
		{
			DrawSequenceSection();

			if (_reader != null && _reader.IsValid)
			{
				DrawInfoSection();
				DrawFrameSection();
				DrawOverlaySection();
				DrawViewpointSection();
			}
		}

		// ─────────────────────────────────────────────
		// Sequence Section
		// ─────────────────────────────────────────────

		private void DrawSequenceSection()
		{
			EditorGUILayout.LabelField("Sequence", EditorStyles.boldLabel);

			EditorGUILayout.BeginHorizontal();
			_sequenceDirectory = EditorGUILayout.TextField(_sequenceDirectory);
			if (GUILayout.Button("Browse", GUILayout.Width(60)))
			{
				string defaultPath = Path.Combine(Application.persistentDataPath, "Recordings");
				if (!Directory.Exists(defaultPath))
					defaultPath = Application.persistentDataPath;

				string selected = EditorUtility.OpenFolderPanel("Select Sequence Directory", defaultPath, "");
				if (!string.IsNullOrEmpty(selected))
					_sequenceDirectory = selected;
			}
			string managerDir = FindManagerSequenceDirectory();
			EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(managerDir));
			if (GUILayout.Button("From Player", GUILayout.Width(85)))
				_sequenceDirectory = managerDir;
			EditorGUI.EndDisabledGroup();
			EditorGUILayout.EndHorizontal();

			if (GUILayout.Button("Load"))
				LoadSequence();

			EditorGUILayout.Space();
		}

		// ─────────────────────────────────────────────
		// Info Section
		// ─────────────────────────────────────────────

		private void DrawInfoSection()
		{
			EditorGUILayout.LabelField("Info", EditorStyles.boldLabel);

			EditorGUI.BeginDisabledGroup(true);
			EditorGUILayout.IntField("Frame Count", _reader.FrameCount);
			EditorGUILayout.TextField("Resolution", $"{_reader.ImageWidth} x {_reader.ImageHeight}");

			// Compute HFOV from normalized fu: HFOV = 2 * atan(0.5 / fu)
			if (_fu > 0)
			{
				float hfovDeg = 2f * Mathf.Atan(0.5f / _fu) * Mathf.Rad2Deg;
				EditorGUILayout.FloatField("HFOV (deg)", Mathf.Round(hfovDeg * 10f) / 10f);
			}
			EditorGUI.EndDisabledGroup();

			EditorGUILayout.Space();
		}

		// ─────────────────────────────────────────────
		// Frame Section
		// ─────────────────────────────────────────────

		private void DrawFrameSection()
		{
			EditorGUILayout.LabelField("Frame", EditorStyles.boldLabel);

			int start = _reader.StartIndex;
			int end = start + _reader.FrameCount - 1;

			EditorGUI.BeginChangeCheck();
			_currentFrame = EditorGUILayout.IntSlider(_currentFrame, start, end);
			if (EditorGUI.EndChangeCheck())
				LoadFrame(_currentFrame);

			EditorGUILayout.BeginHorizontal();
			if (GUILayout.Button("|<")) { _currentFrame = start; LoadFrame(_currentFrame); }
			if (GUILayout.Button("<"))  { _currentFrame = Mathf.Max(start, _currentFrame - 1); LoadFrame(_currentFrame); }
			if (GUILayout.Button(">"))  { _currentFrame = Mathf.Min(end, _currentFrame + 1); LoadFrame(_currentFrame); }
			if (GUILayout.Button(">|")) { _currentFrame = end; LoadFrame(_currentFrame); }
			EditorGUILayout.EndHorizontal();

			EditorGUILayout.Space();
		}

		// ─────────────────────────────────────────────
		// Overlay Section
		// ─────────────────────────────────────────────

		private void DrawOverlaySection()
		{
			EditorGUILayout.LabelField("Overlay", EditorStyles.boldLabel);

			_overlayOpacity = EditorGUILayout.Slider("Opacity", _overlayOpacity, 0f, 1f);

			EditorGUI.BeginChangeCheck();
			_lockProjection = EditorGUILayout.Toggle("Lock Projection", _lockProjection);
			if (EditorGUI.EndChangeCheck() && !_lockProjection)
				RestoreProjection();

			SceneView.RepaintAll();
		}

		// ─────────────────────────────────────────────
		// Viewpoint Section
		// ─────────────────────────────────────────────

		private void DrawViewpointSection()
		{
			EditorGUILayout.Space();
			EditorGUILayout.LabelField("Viewpoint", EditorStyles.boldLabel);

			_trackedBody = (TrackedBody)EditorGUILayout.ObjectField("Tracked Body", _trackedBody, typeof(TrackedBody), true);

			if (_trackedBody == null) return;

			Transform viewpoint = _trackedBody.InitialViewpoint;
			var sceneView = SceneView.lastActiveSceneView;
			bool hasSceneView = sceneView != null;
			bool hasViewpoint = viewpoint != null;

			EditorGUILayout.BeginHorizontal();

			using (new EditorGUI.DisabledScope(!hasSceneView || !hasViewpoint))
			{
				if (GUILayout.Button("Scene View → Viewpoint"))
				{
					Undo.RecordObject(viewpoint, "Align Viewpoint to Scene View");
					viewpoint.position = sceneView.camera.transform.position;
					viewpoint.rotation = sceneView.camera.transform.rotation;
					EditorUtility.SetDirty(viewpoint);
				}
			}

			using (new EditorGUI.DisabledScope(!hasSceneView || !hasViewpoint))
			{
				if (GUILayout.Button("Viewpoint → Scene View"))
				{
					sceneView.pivot = viewpoint.position + viewpoint.forward * sceneView.cameraDistance;
					sceneView.rotation = viewpoint.rotation;
					sceneView.Repaint();
				}
			}

			EditorGUILayout.EndHorizontal();

			if (!hasViewpoint)
			{
				EditorGUILayout.HelpBox(
					"No viewpoint assigned on this TrackedBody. Set Initialization to 'Viewpoint' and assign or create one.",
					MessageType.Info);
			}
		}

		// ─────────────────────────────────────────────
		// Loading
		// ─────────────────────────────────────────────

		private void LoadSequence()
		{
			string resolved = ResolveSequenceDirectory(_sequenceDirectory);
			_reader = new SequenceReader(resolved);

			if (!_reader.IsValid)
			{
				Debug.LogError($"[SequenceAlignment] Invalid sequence at: {resolved}");
				_reader = null;
				return;
			}

			_sequenceDirectory = resolved;
			_currentFrame = _reader.StartIndex;
			LoadFrame(_currentFrame);

			Debug.Log($"[SequenceAlignment] Loaded {_reader.FrameCount} frames " +
			          $"({_reader.ImageWidth}x{_reader.ImageHeight})");
		}

		private void LoadFrame(int index)
		{
			if (_reader == null || !_reader.IsValid) return;

			if (_displayTexture == null)
				_displayTexture = new Texture2D(2, 2, TextureFormat.RGB24, false);

			_reader.LoadColorFrame(index, out _imageWidth, out _imageHeight, _displayTexture);
			_reader.GetFrameIntrinsics(index, out _fu, out _fv, out _ppu, out _ppv);

			SceneView.RepaintAll();
			Repaint();
		}

		// ─────────────────────────────────────────────
		// Scene View overlay
		// ─────────────────────────────────────────────

		private void OnSceneGUI(SceneView sceneView)
		{
			if (_displayTexture == null || _overlayOpacity <= 0f) return;

			Handles.BeginGUI();

			var pixelRect = sceneView.camera.pixelRect;
			Rect viewRect = new Rect(0, 0, pixelRect.width, pixelRect.height);
			Rect imageRect = FitRect(viewRect, (float)_imageWidth / _imageHeight);

			Color prev = GUI.color;
			GUI.color = new Color(1f, 1f, 1f, _overlayOpacity);
			GUI.DrawTexture(imageRect, _displayTexture, ScaleMode.StretchToFill);
			GUI.color = prev;

			Handles.EndGUI();
		}

		/// <summary>
		/// Compute the largest rect with the given aspect ratio that fits inside container,
		/// centered (letterboxed/pillarboxed).
		/// </summary>
		private static Rect FitRect(Rect container, float imageAspect)
		{
			float containerAspect = container.width / container.height;
			float w, h;

			if (containerAspect > imageAspect)
			{
				// Container is wider — fit to height, pillarbox
				h = container.height;
				w = h * imageAspect;
			}
			else
			{
				// Container is taller — fit to width, letterbox
				w = container.width;
				h = w / imageAspect;
			}

			float x = container.x + (container.width - w) * 0.5f;
			float y = container.y + (container.height - h) * 0.5f;
			return new Rect(x, y, w, h);
		}

		// ─────────────────────────────────────────────
		// Projection override
		// ─────────────────────────────────────────────

		private void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
		{
			if (!_lockProjection || _fu <= 0) return;

			var sv = SceneView.lastActiveSceneView;
			if (sv == null || cam != sv.camera) return;

			ApplyProjection(cam);
		}

		private void ApplyProjection(Camera cam)
		{
			float near = cam.nearClipPlane;
			float far = cam.farClipPlane;

			// Frustum from normalized intrinsics
			float l = -_ppu / _fu * near;
			float r = (1f - _ppu) / _fu * near;
			float b = -(1f - _ppv) / _fv * near;
			float t = _ppv / _fv * near;

			// Pad to match viewport aspect so the image maps correctly
			// while extra viewport area shows extended 3D content
			float imageAspect = (r - l) / (t - b);
			float viewAspect = cam.aspect;

			if (viewAspect > imageAspect)
			{
				// Viewport is wider — extend l/r symmetrically
				float scale = viewAspect / imageAspect;
				float cx = (l + r) * 0.5f;
				float hw = (r - l) * 0.5f * scale;
				l = cx - hw;
				r = cx + hw;
			}
			else if (viewAspect < imageAspect)
			{
				// Viewport is taller — extend b/t symmetrically
				float scale = imageAspect / viewAspect;
				float cy = (b + t) * 0.5f;
				float hh = (t - b) * 0.5f * scale;
				b = cy - hh;
				t = cy + hh;
			}

			// OpenGL off-center projection matrix
			Matrix4x4 proj = Matrix4x4.identity;
			proj.m00 = 2f * near / (r - l);
			proj.m02 = (r + l) / (r - l);
			proj.m11 = 2f * near / (t - b);
			proj.m12 = (t + b) / (t - b);
			proj.m22 = -(far + near) / (far - near);
			proj.m23 = -2f * far * near / (far - near);
			proj.m32 = -1f;
			proj.m33 = 0f;

			cam.projectionMatrix = proj;
			_projectionOverridden = true;
		}

		private void RestoreProjection()
		{
			if (!_projectionOverridden) return;

			var sv = SceneView.lastActiveSceneView;
			if (sv != null)
				sv.camera.ResetProjectionMatrix();

			_projectionOverridden = false;
		}

		// ─────────────────────────────────────────────
		// Utilities
		// ─────────────────────────────────────────────

		private void CleanupTexture()
		{
			if (_displayTexture != null)
			{
				DestroyImmediate(_displayTexture);
				_displayTexture = null;
			}
		}

		private static TrackedBody FindDefaultTrackedBody()
		{
			foreach (var body in Object.FindObjectsByType<TrackedBody>(FindObjectsSortMode.None))
			{
				if (body.isActiveAndEnabled && !body.HasParent)
					return body;
			}
			return null;
		}

		private static string FindManagerSequenceDirectory()
		{
			var manager = Object.FindAnyObjectByType<XRTrackerManager>();
			return manager != null ? manager.SequenceDirectory : null;
		}

		/// <summary>
		/// If the directory has no sequence.json, find the latest timestamped child folder.
		/// </summary>
		private static string ResolveSequenceDirectory(string dir)
		{
			if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
				return dir;

			if (File.Exists(Path.Combine(dir, "sequence.json")))
				return dir;

			var subDirs = Directory.GetDirectories(dir);
			if (subDirs.Length == 0)
				return dir;

			Array.Sort(subDirs, StringComparer.Ordinal);
			string latest = subDirs[subDirs.Length - 1];

			Debug.Log($"[SequenceAlignment] No sequence.json in '{Path.GetFileName(dir)}', " +
			          $"using latest recording: {Path.GetFileName(latest)}");
			return latest;
		}
	}
}
