using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using AOT;
using UnityEditor;
using UnityEngine;

namespace IV.FormulaTracker
{
	public class CameraCalibrationWindow : EditorWindow
	{
		private enum CalibState { Idle, Previewing, Capturing, Done }

		[MenuItem("XRTracker/Camera Calibration")]
		private static void ShowWindow()
		{
			var window = GetWindow<CameraCalibrationWindow>("Camera Calibration");
			window.minSize = new Vector2(420, 600);
			window.Show();
		}

		// Camera settings
		private FTCameraDevice[] _cameras;
		private string[] _cameraNames;
		private int _selectedCamera;
		private int _requestedWidth = 640;
		private int _requestedHeight = 480;
		private const int BOARD_W = 9;
		private const int BOARD_H = 6;

		// Resolution presets
		private static readonly string[] ResolutionLabels = { "1280x720", "960x540", "640x480", "Custom" };
		private static readonly int[][] ResolutionValues = { new[]{1280,720}, new[]{960,540}, new[]{640,480} };
		private int _resolutionIndex = 2; // default 640x480

		// State
		private CalibState _state = CalibState.Idle;
		private Texture2D _previewTexture;
		private byte[] _imageBuffer;
		private int _actualWidth;
		private int _actualHeight;

		// Capture
		private const int CAPTURE_TARGET = 25;
		private int _captureCount;
		private int _stableFrameCount;
		private const int RequiredStableFrames = 10;
		private const float MinDiversityDistance = 30f;
		private List<Vector2> _captureCenters = new List<Vector2>();
		private float[] _cornersBuffer;
		private const int MaxCorners = 200; // 9x6 = 54 corners, plenty of room

		// Results
		private float _rmsError;
		private float _fx, _fy, _cx, _cy;
		private float _fxDist, _fyDist, _cxDist, _cyDist;
		private float _k1, _k2, _k3, _p1, _p2;
		private int _calibWidth, _calibHeight;
		private string _statusMessage = "";

		// Callback prevent-GC
		private static CameraCalibrationWindow _instance;
		private static FTBridge.ImageCallback _imageCallbackDelegate;

		private void OnEnable()
		{
			_instance = this;
			_cornersBuffer = new float[MaxCorners * 2];
			EnumerateCameras();
			EditorApplication.update += OnEditorUpdate;
		}

		private void OnDisable()
		{
			EditorApplication.update -= OnEditorUpdate;
			StopCamera();
			_instance = null;
		}

		private void EnumerateCameras()
		{
			var devices = new FTCameraDevice[16];
			int count = FTBridge.FT_EnumerateCameras(devices, devices.Length);
			_cameras = new FTCameraDevice[count];
			_cameraNames = new string[count];
			for (int i = 0; i < count; i++)
			{
				_cameras[i] = devices[i];
				_cameraNames[i] = string.IsNullOrEmpty(devices[i].name)
					? $"Camera {devices[i].camera_id}"
					: devices[i].name;
			}

			if (_selectedCamera >= count) _selectedCamera = 0;
		}

		private void StartCamera()
		{
			if (_cameras == null || _cameras.Length == 0) return;

			_imageCallbackDelegate = OnCalibImageReceived;
			FTBridge.FT_Calib_SetImageCallback(_imageCallbackDelegate, IntPtr.Zero);

			int result = FTBridge.FT_Calib_Start(
				_cameras[_selectedCamera].camera_id,
				_requestedWidth, _requestedHeight,
				BOARD_W, BOARD_H,
				out _actualWidth, out _actualHeight);

			if (result != FTErrorCode.OK)
			{
				_statusMessage = "Failed to open camera. Is it in use by the tracker?";
				return;
			}

			_state = CalibState.Previewing;
			_statusMessage = "Preview active. Click 'Start Capture' when ready.";
		}

		private void StopCamera()
		{
			if (_state != CalibState.Idle)
			{
				FTBridge.FT_Calib_Stop();
				FTBridge.FT_Calib_SetImageCallback(null, IntPtr.Zero);
				_state = CalibState.Idle;
			}

			if (_previewTexture != null)
			{
				DestroyImmediate(_previewTexture);
				_previewTexture = null;
			}

			_imageCallbackDelegate = null;
		}

		[MonoPInvokeCallback(typeof(FTBridge.ImageCallback))]
		private static void OnCalibImageReceived(IntPtr rgbData, int width, int height, IntPtr userdata)
		{
			var inst = _instance;
			if (inst == null) return;

			if (inst._previewTexture == null || inst._previewTexture.width != width || inst._previewTexture.height != height)
			{
				if (inst._previewTexture != null) DestroyImmediate(inst._previewTexture);
				inst._previewTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
				inst._imageBuffer = new byte[width * height * 3];
			}

			// Flip vertically (OpenCV top-down → Unity bottom-up)
			int rowSize = width * 3;
			for (int y = 0; y < height; y++)
			{
				IntPtr sourceRow = rgbData + ((height - 1 - y) * rowSize);
				Marshal.Copy(sourceRow, inst._imageBuffer, y * rowSize, rowSize);
			}

			inst._previewTexture.LoadRawTextureData(inst._imageBuffer);
			inst._previewTexture.Apply();
		}

		private void OnEditorUpdate()
		{
			if (_state == CalibState.Idle || _state == CalibState.Done) return;

			int cornersFound;
			int framesProcessed = FTBridge.FT_Calib_ProcessFrame(out cornersFound, _cornersBuffer, MaxCorners);

			if (framesProcessed > 0)
			{
				Repaint();

				if (_state == CalibState.Capturing)
					ProcessAutoCapture(cornersFound);
			}
		}

		private void ProcessAutoCapture(int cornersFound)
		{
			if (cornersFound == 0)
			{
				_stableFrameCount = 0;
				_statusMessage = "No board detected";
				return;
			}

			// Compute center of detected corners
			float cx = 0, cy = 0;
			for (int i = 0; i < cornersFound; i++)
			{
				cx += _cornersBuffer[i * 2];
				cy += _cornersBuffer[i * 2 + 1];
			}
			cx /= cornersFound;
			cy /= cornersFound;
			var center = new Vector2(cx, cy);

			// Diversity check
			bool diverse = true;
			foreach (var prev in _captureCenters)
			{
				if (Vector2.Distance(center, prev) < MinDiversityDistance)
				{
					diverse = false;
					break;
				}
			}

			if (!diverse)
			{
				_stableFrameCount = 0;
				_statusMessage = "Move to a new position";
				return;
			}

			_stableFrameCount++;
			if (_stableFrameCount < RequiredStableFrames)
			{
				_statusMessage = $"Hold steady ({_stableFrameCount}/{RequiredStableFrames})";
				return;
			}

			// Capture!
			_captureCount = FTBridge.FT_Calib_Capture();
			_captureCenters.Add(center);
			_stableFrameCount = 0;
			_statusMessage = $"Captured! ({_captureCount}/{CAPTURE_TARGET})";

			if (_captureCount >= CAPTURE_TARGET)
			{
				RunCalibration();
			}
		}

		private void RunCalibration()
		{
			_statusMessage = "Calibrating...";
			_rmsError = FTBridge.FT_Calib_Run(
				out _fx, out _fy, out _cx, out _cy,
				out _fxDist, out _fyDist, out _cxDist, out _cyDist,
				out _k1, out _k2, out _k3, out _p1, out _p2,
				out _calibWidth, out _calibHeight);

			if (_rmsError < 0)
			{
				_statusMessage = "Calibration failed. Try capturing more diverse poses.";
				return;
			}

			_state = CalibState.Done;
			string quality = _rmsError < 0.5f ? "Excellent" : _rmsError < 1.0f ? "Good" : "Acceptable";
			_statusMessage = $"RMS Error: {_rmsError:F2} px ({quality})";
		}

		private void SaveCalibration()
		{
			string deviceName = _cameraNames[_selectedCamera];

			// Find the TextAsset currently assigned on the manager (if any)
			string path = null;
			var manager = FindObjectOfType<XRTrackerManager>();
			if (manager != null)
			{
				var so = new SerializedObject(manager);
				var prop = so.FindProperty("_calibrationsFile");
				var existing = prop.objectReferenceValue as TextAsset;
				if (existing != null)
				{
					string assetPath = AssetDatabase.GetAssetPath(existing);
					// Only write to it if it's in Assets/ (not in Packages/ which is read-only)
					if (assetPath.StartsWith("Assets/"))
						path = assetPath;
				}
			}

			// No writable asset assigned — create a new one in Assets/
			if (path == null)
			{
				string dir = "Assets/CameraCalibrations";
				if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
				path = Path.Combine(dir, "camera-calibrations.json");
			}

			// Load existing or create new
			MultiCameraCalibration calibData;
			string fullPath = Path.GetFullPath(path);
			if (File.Exists(fullPath))
			{
				try
				{
					string json = File.ReadAllText(fullPath);
					calibData = JsonUtility.FromJson<MultiCameraCalibration>(json);
				}
				catch
				{
					calibData = new MultiCameraCalibration();
				}
			}
			else
			{
				calibData = new MultiCameraCalibration();
			}

			if (calibData.cameras == null)
				calibData.cameras = new List<CameraCalibrationEntry>();

			// Find or create entry
			var entry = calibData.cameras.Find(c => c.deviceName == deviceName);
			if (entry == null)
			{
				entry = new CameraCalibrationEntry { deviceName = deviceName };
				calibData.cameras.Add(entry);
			}

			// Post-undistortion intrinsics
			entry.intrinsics = new CalibrationIntrinsics
			{
				fx = _fx, fy = _fy, cx = _cx, cy = _cy,
				width = _calibWidth, height = _calibHeight
			};

			// Pre-undistortion intrinsics with distortion coefficients
			entry.intrinsicsDist = new CalibrationIntrinsics
			{
				fx = _fxDist, fy = _fyDist, cx = _cxDist, cy = _cyDist,
				width = _calibWidth, height = _calibHeight,
				k1 = _k1, k2 = _k2, k3 = _p1, k4 = _p2, k5 = _k3
			};

			string output = JsonUtility.ToJson(calibData, true);
			File.WriteAllText(fullPath, output);
			AssetDatabase.Refresh();

			// Auto-assign to the manager if not already assigned
			if (manager != null)
			{
				var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
				if (asset != null)
				{
					var so = new SerializedObject(manager);
					var prop = so.FindProperty("_calibrationsFile");
					if (prop.objectReferenceValue == null || prop.objectReferenceValue != asset)
					{
						prop.objectReferenceValue = asset;
						so.ApplyModifiedProperties();
						EditorUtility.SetDirty(manager);
					}
				}
			}

			Debug.Log($"[CameraCalibration] Saved calibration for '{deviceName}' to {path}");
			_statusMessage = $"Saved to {path}";
		}

		private void OnGUI()
		{
			// Checkerboard link
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField("Print a 9x6 checkerboard (10x7 squares) on matte paper.", EditorStyles.wordWrappedLabel);
			if (GUILayout.Button("Open Pattern", GUILayout.Width(100)))
			{
				string pkgPath = "Packages/com.formulaxr.tracker/Editor/Resources/checkerboard_9x6.png";
				string absPath = Path.GetFullPath(pkgPath);
				if (File.Exists(absPath))
					Application.OpenURL("file:///" + absPath.Replace("\\", "/"));
				else
					Debug.LogWarning($"[CameraCalibration] Checkerboard not found at {absPath}");
			}
			EditorGUILayout.EndHorizontal();

			EditorGUILayout.Space(8);

			// Camera Settings
			EditorGUILayout.LabelField("Camera Settings", EditorStyles.boldLabel);

			using (new EditorGUI.DisabledScope(_state != CalibState.Idle))
			{
				if (_cameras == null || _cameras.Length == 0)
				{
					EditorGUILayout.HelpBox("No cameras found.", MessageType.Warning);
					if (GUILayout.Button("Refresh"))
						EnumerateCameras();
				}
				else
				{
					_selectedCamera = EditorGUILayout.Popup("Camera", _selectedCamera, _cameraNames);

					int newRes = EditorGUILayout.Popup("Resolution", _resolutionIndex, ResolutionLabels);
					if (newRes != _resolutionIndex)
					{
						_resolutionIndex = newRes;
						if (_resolutionIndex < ResolutionValues.Length)
						{
							_requestedWidth = ResolutionValues[_resolutionIndex][0];
							_requestedHeight = ResolutionValues[_resolutionIndex][1];
						}
					}
					if (_resolutionIndex >= ResolutionValues.Length) // Custom
					{
						EditorGUILayout.BeginHorizontal();
						_requestedWidth = EditorGUILayout.IntField("Width", _requestedWidth);
						_requestedHeight = EditorGUILayout.IntField("Height", _requestedHeight);
						EditorGUILayout.EndHorizontal();
					}

				}
			}

			EditorGUILayout.Space(8);

			// Preview
			if (_previewTexture != null)
			{
				EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
				float aspect = (float)_previewTexture.width / _previewTexture.height;
				float width = EditorGUIUtility.currentViewWidth - 20;
				float height = width / aspect;
				Rect rect = GUILayoutUtility.GetRect(width, height);
				GUI.DrawTexture(rect, _previewTexture, ScaleMode.ScaleToFit);
			}

			EditorGUILayout.Space(4);

			// Controls
			switch (_state)
			{
				case CalibState.Idle:
					if (_cameras != null && _cameras.Length > 0)
					{
						if (GUILayout.Button("Start Preview", GUILayout.Height(30)))
							StartCamera();
					}
					break;

				case CalibState.Previewing:
					EditorGUILayout.BeginHorizontal();
					if (GUILayout.Button("Start Capture", GUILayout.Height(30)))
					{
						FTBridge.FT_Calib_Reset();
						_captureCount = 0;
						_stableFrameCount = 0;
						_captureCenters.Clear();
						_state = CalibState.Capturing;
						_statusMessage = "Move checkerboard to different positions";
					}
					if (GUILayout.Button("Stop", GUILayout.Height(30)))
						StopCamera();
					EditorGUILayout.EndHorizontal();

					break;

				case CalibState.Capturing:
					float progress = (float)_captureCount / CAPTURE_TARGET;
					EditorGUI.ProgressBar(
						GUILayoutUtility.GetRect(18, 24, GUILayout.ExpandWidth(true)),
						progress,
						$"{_captureCount}/{CAPTURE_TARGET}");

					EditorGUILayout.Space(4);

					EditorGUILayout.BeginHorizontal();
					if (GUILayout.Button("Stop & Calibrate", GUILayout.Height(30)))
					{
						if (_captureCount >= 3)
							RunCalibration();
						else
							_statusMessage = "Need at least 3 captures";
					}
					if (GUILayout.Button("Cancel", GUILayout.Height(30)))
					{
						FTBridge.FT_Calib_Reset();
						_state = CalibState.Previewing;
						_captureCount = 0;
						_captureCenters.Clear();
					}
					EditorGUILayout.EndHorizontal();
					break;

				case CalibState.Done:
					EditorGUILayout.LabelField("Results", EditorStyles.boldLabel);

					string quality = _rmsError < 0.5f ? "Excellent" : _rmsError < 1.0f ? "Good" : "Acceptable";
					EditorGUILayout.LabelField($"RMS Error: {_rmsError:F3} px ({quality})");

					EditorGUILayout.Space(4);
					EditorGUILayout.LabelField("Undistorted Intrinsics (normalized):");
					EditorGUI.indentLevel++;
					EditorGUILayout.LabelField($"fx={_fx:F6}  fy={_fy:F6}");
					EditorGUILayout.LabelField($"cx={_cx:F6}  cy={_cy:F6}");
					EditorGUI.indentLevel--;

					EditorGUILayout.LabelField("Distortion Coefficients:");
					EditorGUI.indentLevel++;
					EditorGUILayout.LabelField($"k1={_k1:F6}  k2={_k2:F6}  k3={_k3:F6}");
					EditorGUILayout.LabelField($"p1={_p1:F6}  p2={_p2:F6}");
					EditorGUI.indentLevel--;

					EditorGUILayout.Space(8);

					EditorGUILayout.BeginHorizontal();
					if (GUILayout.Button("Save to Project", GUILayout.Height(30)))
						SaveCalibration();
					if (GUILayout.Button("Recalibrate", GUILayout.Height(30)))
					{
						FTBridge.FT_Calib_Reset();
						_captureCount = 0;
						_captureCenters.Clear();
						_state = CalibState.Previewing;
						_statusMessage = "Ready for new calibration";
					}
					EditorGUILayout.EndHorizontal();

					if (GUILayout.Button("Close Camera"))
						StopCamera();
					break;
			}

			// Status
			if (!string.IsNullOrEmpty(_statusMessage))
			{
				EditorGUILayout.Space(4);
				EditorGUILayout.HelpBox(_statusMessage, MessageType.Info);
			}
		}
	}
}
