using System;
using System.Runtime.InteropServices;
using IV.FormulaTracker;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace IV.FormulaTracker.Editor
{
	/// <summary>
	/// Editor window that captures color and depth from the Scene View camera
	/// for offline testing without a physical camera.
	/// It writes a sequence of PNG images and a JSON metadata file (sequence.json).
	///
	/// Color is rendered with the Scene View camera.
	/// Depth is rendered with a command buffer that draws each renderer with the LinearDepth material,
	/// which computes linear eye-space depth from vertex positions.
	/// This bypasses the shader selection of the active render pipeline, so it works with any render pipeline.
	///
	/// The native plugin records the frames and encodes the PNG files.
	/// </summary>
	public class SceneViewRecorder : EditorWindow
	{
		[MenuItem("Tools/XRTracker/Scene View Recorder")]
		private static void ShowWindow()
		{
			var window = GetWindow<SceneViewRecorder>("Scene View Recorder");
			window.minSize = new Vector2(300, 400);
		}

		// Settings
		private string _outputDirectory = "";
		private int _width = 640;
		private int _height = 480;
		private int _targetFps = 30;
		private float _depthScale = 0.001f;
		private bool _captureDepth;
		private Transform _truthBody;

		// Viewpoint
		private TrackedBody _trackedBody;

		// State
		private bool _isRecording;
		private float _nextCaptureTime;
		private int _frameCount;
		private double _startTime;
		private string _activeOutputDirectory;
		private string _lastError;
		private SceneViewTruthWriter _truthWriter;

		// Resources
		private Material _depthMaterial;
		private RenderTexture _colorRT;
		private RenderTexture _depthRT;
		private Texture2D _colorTex;
		private Texture2D _depthTex;

		// Pinned buffers for native calls
		private byte[] _colorBuffer;
		private ushort[] _depthBuffer;

		private Material EnsureDepthMaterial()
		{
			if (_depthMaterial != null) return _depthMaterial;

			var shader = Shader.Find("Hidden/FormulaTracker/LinearDepth");
			if (shader == null)
			{
				shader = Resources.Load<Shader>("Shaders/LinearDepth");
			}

			if (shader != null)
				_depthMaterial = new Material(shader);

			return _depthMaterial;
		}

		private void OnEnable()
		{
			EnsureDepthMaterial();

			if (_trackedBody == null)
				_trackedBody = FindDefaultTrackedBody();

			if (string.IsNullOrEmpty(_outputDirectory))
				_outputDirectory = System.IO.Path.Combine(Application.persistentDataPath, "Recordings");
		}

		private void OnDisable()
		{
			StopRecording();
			CleanupRenderResources();
			if (_depthMaterial != null) { DestroyImmediate(_depthMaterial); _depthMaterial = null; }
		}

		private void OnGUI()
		{
			EditorGUILayout.Space(8);
			EditorGUILayout.LabelField("Scene View Recorder", EditorStyles.boldLabel);
			EditorGUILayout.Space(4);

			// Settings
			using (new EditorGUI.DisabledScope(_isRecording))
			{
				EditorGUILayout.LabelField("Output", EditorStyles.miniBoldLabel);

				EditorGUILayout.BeginHorizontal();
				_outputDirectory = EditorGUILayout.TextField("Directory", _outputDirectory);
				if (GUILayout.Button("...", GUILayout.Width(30)))
				{
					string dir = EditorUtility.OpenFolderPanel("Select Output Directory",
						string.IsNullOrEmpty(_outputDirectory) ? GetDefaultRecordingsPath() : _outputDirectory, "");
					if (!string.IsNullOrEmpty(dir))
						_outputDirectory = dir;
				}
				EditorGUILayout.EndHorizontal();

				EditorGUILayout.Space(4);
				EditorGUILayout.LabelField("Capture", EditorStyles.miniBoldLabel);

				_width = EditorGUILayout.IntField("Width", _width);
				_height = EditorGUILayout.IntField("Height", _height);
				_targetFps = EditorGUILayout.IntSlider("Target FPS", _targetFps, 1, 60);
				_captureDepth = EditorGUILayout.Toggle("Capture Depth", _captureDepth);
				_truthBody = (Transform)EditorGUILayout.ObjectField(
					new GUIContent("Truth Body (Optional)",
						"When set, the recorder writes the true pose of this object for every frame to a truth.jsonl file next to the recording. " +
						"The file lets you check tracking accuracy later. Leave it empty to record without a truth file."),
					_truthBody, typeof(Transform), true);

				if (_captureDepth)
				{
					EditorGUI.indentLevel++;
					_depthScale = EditorGUILayout.FloatField("Depth Scale (meters per unit)", _depthScale);
					EditorGUI.indentLevel--;
				}
			}

			// Viewpoint
			EditorGUILayout.Space(4);
			EditorGUILayout.LabelField("Viewpoint", EditorStyles.miniBoldLabel);
			_trackedBody = (TrackedBody)EditorGUILayout.ObjectField("Tracked Body", _trackedBody, typeof(TrackedBody), true);

			if (_trackedBody != null)
			{
				Transform viewpoint = _trackedBody.InitialViewpoint;
				var sceneView = SceneView.lastActiveSceneView;

				using (new EditorGUI.DisabledScope(viewpoint == null || sceneView == null))
				{
					if (GUILayout.Button("Align Scene View to Viewpoint"))
					{
						sceneView.pivot = viewpoint.position + viewpoint.forward * sceneView.cameraDistance;
						sceneView.rotation = viewpoint.rotation;
						sceneView.Repaint();
					}
				}

				if (viewpoint == null)
				{
					EditorGUILayout.HelpBox(
						"This TrackedBody has no viewpoint assigned.",
						MessageType.Info);
				}
			}

			EditorGUILayout.Space(8);

			// Buttons
			if (!_isRecording)
			{
				using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_outputDirectory)))
				{
					if (GUILayout.Button("Start Recording", GUILayout.Height(30)))
						StartRecording();
				}
			}
			else
			{
				// Red recording indicator
				var prevColor = GUI.backgroundColor;
				GUI.backgroundColor = Color.red;
				if (GUILayout.Button("Stop Recording", GUILayout.Height(30)))
					StopRecording();
				GUI.backgroundColor = prevColor;
			}

			// Status
			EditorGUILayout.Space(8);
			EditorGUILayout.LabelField("Status", EditorStyles.miniBoldLabel);

			if (_isRecording)
			{
				double elapsed = EditorApplication.timeSinceStartup - _startTime;
				EditorGUILayout.LabelField("State", "RECORDING");
				EditorGUILayout.LabelField("Frames", _frameCount.ToString());
				EditorGUILayout.LabelField("Elapsed", $"{elapsed:F1}s");
				EditorGUILayout.LabelField("Effective FPS",
					elapsed > 0 ? $"{_frameCount / elapsed:F1}" : "0");
				EditorGUILayout.LabelField("Output", _activeOutputDirectory ?? "");
				Repaint();
			}
			else
			{
				EditorGUILayout.LabelField("State", "Idle");
				if (!string.IsNullOrEmpty(_activeOutputDirectory))
					EditorGUILayout.LabelField("Last Output", _activeOutputDirectory);
			}

			if (!string.IsNullOrEmpty(_lastError))
				EditorGUILayout.HelpBox(_lastError, MessageType.Error);

			// Warnings
			if (SceneView.lastActiveSceneView == null)
			{
				EditorGUILayout.Space(4);
				EditorGUILayout.HelpBox("The recorder found no active Scene View. Open a Scene View to record.", MessageType.Warning);
			}

			if (_captureDepth && EnsureDepthMaterial() == null)
			{
				EditorGUILayout.Space(4);
				EditorGUILayout.HelpBox(
					"The LinearDepth shader was not found, so depth capture is disabled.\n" +
					"The 'Hidden/FormulaTracker/LinearDepth' shader must be included in the project.",
					MessageType.Warning);
			}
		}

		private static TrackedBody FindDefaultTrackedBody()
		{
			foreach (var body in FindObjectsByType<TrackedBody>(FindObjectsSortMode.None))
			{
				if (body.isActiveAndEnabled && !body.HasParent)
					return body;
			}
			return null;
		}

		private static string GetDefaultRecordingsPath()
		{
			string recordings = System.IO.Path.Combine(Application.streamingAssetsPath, "Recordings");
			if (System.IO.Directory.Exists(recordings))
				return recordings;
			if (System.IO.Directory.Exists(Application.streamingAssetsPath))
				return Application.streamingAssetsPath;
			return Application.dataPath;
		}

		private void StartRecording()
		{
			if (string.IsNullOrEmpty(_outputDirectory))
			{
				Debug.LogError("[SceneViewRecorder] The output directory is not set.");
				return;
			}

			string timestampedDir = System.IO.Path.Combine(_outputDirectory,
				DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
			_activeOutputDirectory = timestampedDir;
			_lastError = null;

			int result = FTBridge.FT_StartRecording(timestampedDir);
			if (result != FTErrorCode.OK)
			{
				SetError($"The recording could not start (error {result}).");
				return;
			}

			try
			{
				_truthWriter = _truthBody != null
					? new SceneViewTruthWriter(timestampedDir)
					: null;
			}
			catch (Exception exception)
			{
				FTBridge.FT_StopRecording();
				SetError($"Could not create truth.jsonl ({exception.Message}).");
				return;
			}

			EnsureRenderResources();

			_isRecording = true;
			_frameCount = 0;
			_startTime = EditorApplication.timeSinceStartup;
			_nextCaptureTime = 0f;

			EditorApplication.update += OnEditorUpdate;

			Debug.Log($"[SceneViewRecorder] Recording started in {timestampedDir}.");
		}

		private void StopRecording()
		{
			if (!_isRecording) return;

			EditorApplication.update -= OnEditorUpdate;

			_isRecording = false;
			int frameCount = -1;
			Exception stopException = null;
			try
			{
				frameCount = FTBridge.FT_StopRecording();
			}
			catch (Exception exception)
			{
				stopException = exception;
			}

			try
			{
				_truthWriter?.Dispose();
			}
			catch (Exception exception)
			{
				stopException ??= exception;
			}
			finally
			{
				_truthWriter = null;
			}

			if (stopException != null)
			{
				string message = $"Could not stop the recording cleanly ({stopException.Message}).";
				if (string.IsNullOrEmpty(_lastError))
					SetError(message);
				else
					Debug.LogError($"[SceneViewRecorder] {message}");
				return;
			}

			Debug.Log($"[SceneViewRecorder] Recording stopped. {frameCount} frames saved to {_activeOutputDirectory}.");
		}


		private void OnEditorUpdate()
		{
			if (!_isRecording) return;

			float now = (float)(EditorApplication.timeSinceStartup - _startTime);
			if (now < _nextCaptureTime) return;

			_nextCaptureTime = now + (1f / _targetFps);
			CaptureFrame();
		}

		private void CaptureFrame()
		{
			var sceneView = SceneView.lastActiveSceneView;
			if (sceneView == null) return;
			if (FTBridge.FT_IsRecording() == 0) return;

			Camera cam = sceneView.camera;
			bool wantDepth = _captureDepth && EnsureDepthMaterial() != null;

			// Compute normalized intrinsics from Scene View camera
			float vfov = cam.fieldOfView * Mathf.Deg2Rad;
			float fPixel = (_height / 2f) / Mathf.Tan(vfov / 2f);
			float fuNorm = fPixel / _width;
			float fvNorm = fPixel / _height;

			// Snapshot all transforms before rendering. The same immutable context
			// is used for native submission and optional truth output.
			long timestampNs = (long)((EditorApplication.timeSinceStartup - _startTime) * 1_000_000_000.0);
			Matrix4x4 worldFromCameraUnity = cam.transform.localToWorldMatrix;
			Matrix4x4? worldFromBodyUnity = _truthBody != null
				? (Matrix4x4?)_truthBody.localToWorldMatrix
				: null;
			var frameSnapshot = SceneViewFrameSnapshot.Create(
				timestampNs,
				_width,
				_height,
				fuNorm,
				fvNorm,
				0.5f,
				0.5f,
				worldFromCameraUnity,
				worldFromBodyUnity);

			var originalRT = cam.targetTexture;

			// Pass 1: Color (through URP)
			cam.targetTexture = _colorRT;
			cam.Render();

			RenderTexture.active = _colorRT;
			_colorTex.ReadPixels(new Rect(0, 0, _width, _height), 0, 0);
			_colorTex.Apply();
			RenderTexture.active = null;

			cam.targetTexture = originalRT;

			// Pass 2: Depth via CommandBuffer.DrawRenderer (pipeline-independent)
			if (wantDepth)
			{
				CaptureDepthCommandBuffer(cam);
			}

			// Extract raw RGB bytes and send to C++ recorder
			byte[] colorRaw = _colorTex.GetRawTextureData();

			// Texture2D ReadPixels gives bottom-to-top; flip to top-to-bottom
			FlipColorRows(colorRaw, _width, _height);

			int colorSize = _width * _height * 3;
			if (_colorBuffer == null || _colorBuffer.Length != colorSize)
				_colorBuffer = new byte[colorSize];
			Buffer.BlockCopy(colorRaw, 0, _colorBuffer, 0, colorSize);

			// Prepare depth data (if capturing)
			IntPtr depthPtr = IntPtr.Zero;
			GCHandle depthHandle = default;
			int depthW = 0, depthH = 0;
			float depthFu = 0, depthFv = 0, depthPpu = 0, depthPpv = 0;

			if (wantDepth)
			{
				var pixels = _depthTex.GetPixels();
				int pixelCount = _width * _height;
				if (_depthBuffer == null || _depthBuffer.Length != pixelCount)
					_depthBuffer = new ushort[pixelCount];

				// Convert float depth (meters) to uint16, flip bottom-to-top → top-to-bottom
				for (int y = 0; y < _height; y++)
				{
					int srcRow = (_height - 1 - y) * _width;  // flip
					int dstRow = y * _width;
					for (int x = 0; x < _width; x++)
					{
						float d = pixels[srcRow + x].r;
						_depthBuffer[dstRow + x] = d > 0
							? (ushort)Mathf.Clamp(d / _depthScale, 0, 65535)
							: (ushort)0;
					}
				}

			}

			GCHandle colorHandle = default;
			int nativeResult;
			try
			{
				_truthWriter?.BeginFrame(_frameCount, frameSnapshot);

				if (wantDepth)
				{
					depthHandle = GCHandle.Alloc(_depthBuffer, GCHandleType.Pinned);
					depthPtr = depthHandle.AddrOfPinnedObject();
					depthW = _width;
					depthH = _height;
					depthFu = fuNorm;
					depthFv = fvNorm;
					depthPpu = 0.5f;
					depthPpv = 0.5f;
				}

				// Pin color data and atomically submit it with the snapshotted pose.
				colorHandle = GCHandle.Alloc(_colorBuffer, GCHandleType.Pinned);
				var worldFromCameraNative = frameSnapshot.WorldFromCameraNative;
				nativeResult = FTBridge.FT_RecordFrameWithCameraPose(
					colorHandle.AddrOfPinnedObject(), _width, _height,
					fuNorm, fvNorm, 0.5f, 0.5f,
					depthPtr, depthW, depthH,
					depthFu, depthFv, depthPpu, depthPpv,
					_depthScale, frameSnapshot.TimestampNs,
					ref worldFromCameraNative);
			}
			catch (Exception exception)
			{
				string rollbackError = null;
				try
				{
					_truthWriter?.RollbackFrame();
				}
				catch (Exception rollbackException)
				{
					rollbackError = $" The truth rollback also failed ({rollbackException.Message}).";
				}

				SetError($"Could not record frame {_frameCount} ({exception.Message}).{rollbackError}");
				StopRecording();
				return;
			}
			finally
			{
				if (colorHandle.IsAllocated)
					colorHandle.Free();
				if (depthHandle.IsAllocated)
					depthHandle.Free();
			}

			bool committed = SceneViewRecordingUtilities.TryFinalizeSubmittedFrame(
				nativeResult,
				_frameCount,
				_truthWriter,
				out int nextFrameCount,
				out string error);
			_frameCount = nextFrameCount;

			if (!committed)
			{
				SetError(error);
				StopRecording();
			}
		}

		private void SetError(string message)
		{
			_lastError = message;
			Debug.LogError($"[SceneViewRecorder] {message}");
			Repaint();
		}

		/// <summary>
		/// Render depth by manually drawing each scene Renderer with the LinearDepth material.
		/// Uses CommandBuffer with explicit camera matrices — works regardless of render pipeline.
		/// </summary>
		private void CaptureDepthCommandBuffer(Camera cam)
		{
			var cmd = new CommandBuffer { name = "SceneViewRecorder_Depth" };
			cmd.SetRenderTarget(_depthRT);
			cmd.ClearRenderTarget(true, true, Color.black);
			cmd.SetViewProjectionMatrices(cam.worldToCameraMatrix, cam.projectionMatrix);

			var frustumPlanes = GeometryUtility.CalculateFrustumPlanes(cam);

			foreach (Renderer r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
			{
				if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
				if (!GeometryUtility.TestPlanesAABB(frustumPlanes, r.bounds)) continue;

				for (int sub = 0; sub < r.sharedMaterials.Length; sub++)
					cmd.DrawRenderer(r, _depthMaterial, sub, 0);
			}

			Graphics.ExecuteCommandBuffer(cmd);
			cmd.Release();

			RenderTexture.active = _depthRT;
			_depthTex.ReadPixels(new Rect(0, 0, _width, _height), 0, 0);
			_depthTex.Apply();
			RenderTexture.active = null;
		}

		private void EnsureRenderResources()
		{
			CleanupRenderResources();

			_colorRT = new RenderTexture(_width, _height, 24, RenderTextureFormat.ARGB32);
			_colorTex = new Texture2D(_width, _height, TextureFormat.RGB24, false);

			if (_captureDepth && EnsureDepthMaterial() != null)
			{
				_depthRT = new RenderTexture(_width, _height, 24, RenderTextureFormat.RFloat);
				_depthTex = new Texture2D(_width, _height, TextureFormat.RFloat, false);
			}
		}

		private void CleanupRenderResources()
		{
			if (_colorRT != null) { _colorRT.Release(); DestroyImmediate(_colorRT); _colorRT = null; }
			if (_depthRT != null) { _depthRT.Release(); DestroyImmediate(_depthRT); _depthRT = null; }
			if (_colorTex != null) { DestroyImmediate(_colorTex); _colorTex = null; }
			if (_depthTex != null) { DestroyImmediate(_depthTex); _depthTex = null; }
		}

		private static void FlipColorRows(byte[] data, int width, int height)
		{
			int rowBytes = width * 3;
			var tempRow = new byte[rowBytes];
			for (int y = 0; y < height / 2; y++)
			{
				int topOffset = y * rowBytes;
				int bottomOffset = (height - 1 - y) * rowBytes;
				Buffer.BlockCopy(data, topOffset, tempRow, 0, rowBytes);
				Buffer.BlockCopy(data, bottomOffset, data, topOffset, rowBytes);
				Buffer.BlockCopy(tempRow, 0, data, bottomOffset, rowBytes);
			}
		}
	}
}
