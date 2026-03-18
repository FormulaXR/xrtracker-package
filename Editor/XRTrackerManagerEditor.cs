using UnityEditor;
using UnityEngine;

namespace IV.FormulaTracker.Editor
{
	/// <summary>
	/// Clean custom editor for FormulaTrackingManager.
	/// </summary>
	[CustomEditor(typeof(XRTrackerManager))]
	[CanEditMultipleObjects]
	public class XRTrackerManagerEditor : UnityEditor.Editor
	{
		// License
		private SerializedProperty _embeddedLicense;

		// Image Source
		private SerializedProperty _imageSource;
		private SerializedProperty _sequenceDirectory;
		private SerializedProperty _realSenseColorResolution;
		private SerializedProperty _realSenseDepthResolution;
		private SerializedProperty _calibrationsFile;
		private SerializedProperty _autoSelectCameraName;
		private SerializedProperty _autoSelectFallbackToFirst;

		// Tracker Settings
		private SerializedProperty _targetFps;
		private SerializedProperty _correspondenceIterations;
		private SerializedProperty _updateIterations;

		// References
		private SerializedProperty _mainCamera;

		// AR Foundation (conditional)
		private SerializedProperty _useARPoseFusion;

		private void OnEnable()
		{
			_embeddedLicense = serializedObject.FindProperty("_embeddedLicense");


			_imageSource = serializedObject.FindProperty("_imageSource");
			_sequenceDirectory = serializedObject.FindProperty("_sequenceDirectory");
			_realSenseColorResolution = serializedObject.FindProperty("_realSenseColorResolution");
			_realSenseDepthResolution = serializedObject.FindProperty("_realSenseDepthResolution");
			_calibrationsFile = serializedObject.FindProperty("_calibrationsFile");
			_autoSelectCameraName = serializedObject.FindProperty("_autoSelectCameraName");
			_autoSelectFallbackToFirst = serializedObject.FindProperty("_autoSelectFallbackToFirst");

			_targetFps = serializedObject.FindProperty("_targetFps");
			_correspondenceIterations = serializedObject.FindProperty("_correspondenceIterations");
			_updateIterations = serializedObject.FindProperty("_updateIterations");

			_mainCamera = serializedObject.FindProperty("_mainCamera");

			// AR Foundation field (may not exist if HAS_AR_FOUNDATION not defined)
			_useARPoseFusion = serializedObject.FindProperty("_useARPoseFusion");
		}

		public override void OnInspectorGUI()
		{
			serializedObject.Update();

			var manager = (XRTrackerManager)target;
			bool isNativeMode = _imageSource.enumValueIndex == (int)ImageSource.Native;
			bool isRealSenseMode = _imageSource.enumValueIndex == (int)ImageSource.RealSense;
			bool isSequenceMode = _imageSource.enumValueIndex == (int)ImageSource.Sequence;

			// Script field (read-only)
			using (new EditorGUI.DisabledScope(true))
			{
				EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));
			}

			DrawLicenseSection(manager);
			DrawImageSourceSection(isNativeMode, isRealSenseMode, isSequenceMode);
			DrawTrackerSettingsSection();
			DrawReferencesSection(isNativeMode);
			DrawRuntimeStatusSection(manager);

			serializedObject.ApplyModifiedProperties();
		}

		private void DrawLicenseSection(XRTrackerManager manager)
		{
			EditorGUILayout.Space(4);
			EditorGUILayout.LabelField("License", EditorStyles.boldLabel);

			EditorGUILayout.PropertyField(_embeddedLicense, new GUIContent("License File",
				"Drag a .lic file here. If empty, a Free license (60s tracking) is used automatically."));

			// Runtime license info
			if (Application.isPlaying)
			{
				EditorGUILayout.Space(2);
				using (new EditorGUI.DisabledScope(true))
				{
					EditorGUILayout.EnumPopup("Tier", manager.LicenseTier);
					EditorGUILayout.EnumPopup("Status", manager.LicenseStatus);

					if (manager.LicenseTier == LicenseTier.Free)
					{
						float secondsLeft = manager.FreeSecondsRemaining;
						EditorGUILayout.TextField("Time Remaining",
							manager.IsLicenseFrozen ? "Frozen" : $"{secondsLeft:F1}s");
					}
				}

				// Machine ID (copyable)
				EditorGUILayout.BeginHorizontal();
				EditorGUILayout.TextField("Machine ID", manager.MachineId);
				if (GUILayout.Button("Copy", GUILayout.Width(50)))
				{
					GUIUtility.systemCopyBuffer = manager.MachineId;
				}
				EditorGUILayout.EndHorizontal();
			}
		}

		private void DrawImageSourceSection(bool isNativeMode, bool isRealSenseMode, bool isSequenceMode)
		{
			EditorGUILayout.Space(8);
			EditorGUILayout.PropertyField(_imageSource, new GUIContent("Image Source"));

			if (isRealSenseMode)
			{
				EditorGUILayout.Space(4);
				EditorGUILayout.LabelField("RealSense Resolution", EditorStyles.miniBoldLabel);

				EditorGUILayout.PropertyField(_realSenseColorResolution,
					new GUIContent("Color Resolution", "Color camera resolution. Higher = more detail for validation, lower FPS."));
				EditorGUILayout.PropertyField(_realSenseDepthResolution,
					new GUIContent("Depth Resolution", "Depth camera resolution. Higher = better depth tracking, lower FPS."));

				EditorGUILayout.HelpBox(
					"Note: 1080p color and 720p depth are limited to 30fps. " +
					"Resolution must be set before initialization.",
					MessageType.Info);
			}
			else if (isSequenceMode)
			{
				EditorGUILayout.Space(4);

				EditorGUILayout.BeginHorizontal();
				EditorGUILayout.PropertyField(_sequenceDirectory, new GUIContent("Sequence Directory"));
				if (GUILayout.Button("...", GUILayout.Width(30)))
				{
					string startDir = _sequenceDirectory.stringValue;
					if (string.IsNullOrEmpty(startDir) || !System.IO.Directory.Exists(startDir))
						startDir = Application.streamingAssetsPath;

					string dir = EditorUtility.OpenFolderPanel("Select Sequence Directory", startDir, "");
					if (!string.IsNullOrEmpty(dir))
						_sequenceDirectory.stringValue = dir;
				}

				EditorGUILayout.EndHorizontal();

				EditorGUILayout.HelpBox(
					"Point to a folder containing sequence.json and recorded frames. " +
					"If no sequence.json is found, the latest subfolder is used automatically.",
					MessageType.Info);
			}
			else if (isNativeMode)
			{
				EditorGUILayout.PropertyField(_calibrationsFile, new GUIContent("Calibrations File",
						"Camera calibration data (JSON TextAsset). If not assigned, a default is loaded from Resources."));

				if (_calibrationsFile.objectReferenceValue == null)
				{
					var fallback = Resources.Load<TextAsset>("camera-calibrations");
					if (fallback != null)
					{
						EditorGUILayout.HelpBox(
							"No calibration file assigned. Using default from package Resources.",
							MessageType.Info);
					}
					else
					{
						EditorGUILayout.HelpBox(
							"No calibration file assigned and no default found. " +
							"Tracking will use default intrinsics, which may reduce accuracy. " +
							"Use XRTracker > Camera Calibration to calibrate your camera.",
							MessageType.Warning);
					}
				}

				EditorGUILayout.Space(4);
				EditorGUILayout.LabelField("Auto-Select Camera", EditorStyles.miniBoldLabel);

				EditorGUILayout.PropertyField(_autoSelectCameraName,
					new GUIContent("Camera Name", "Camera name to auto-select on start (case-insensitive exact match). Leave empty to disable auto-select."));

				// Only show fallback option if camera name is specified
				if (!string.IsNullOrEmpty(_autoSelectCameraName.stringValue))
				{
					EditorGUI.indentLevel++;
					EditorGUILayout.PropertyField(_autoSelectFallbackToFirst,
						new GUIContent("Fallback to First", "If specified camera not found, use first available camera"));
					EditorGUI.indentLevel--;
				}

				// Show available cameras in play mode
				if (Application.isPlaying)
				{
					var manager = (XRTrackerManager)target;
					if (manager.AvailableCameras != null && manager.AvailableCameras.Length > 0)
					{
						EditorGUILayout.Space(4);
						EditorGUILayout.LabelField("Available Cameras:", EditorStyles.miniLabel);
						EditorGUI.indentLevel++;
						foreach (var cam in manager.AvailableCameras)
						{
							EditorGUILayout.LabelField(cam.name, EditorStyles.miniLabel);
						}

						EditorGUI.indentLevel--;
					}
				}
			}
		}

		private void DrawTrackerSettingsSection()
		{
			EditorGUILayout.Space(8);
			EditorGUILayout.LabelField("Tracker Settings", EditorStyles.boldLabel);

			EditorGUILayout.PropertyField(_targetFps, new GUIContent("Target FPS"));
			EditorGUILayout.PropertyField(_correspondenceIterations, new GUIContent("Correspondence Iterations"));
			EditorGUILayout.PropertyField(_updateIterations, new GUIContent("Update Iterations"));

			// Edge render resolution kept at 512 default — not exposed in inspector
			// to avoid confusion. Can still be set via script: _edgeRenderResolution.
		}

		private void DrawReferencesSection(bool isNativeMode)
		{
			EditorGUILayout.Space(8);
			EditorGUILayout.LabelField("References", EditorStyles.boldLabel);

			EditorGUILayout.PropertyField(_mainCamera, new GUIContent("Main Camera"));

			// AR Foundation settings (only available in Injected mode with AR Foundation)
			if (_useARPoseFusion != null && !isNativeMode)
			{
				EditorGUILayout.PropertyField(_useARPoseFusion,
					new GUIContent("AR Foundation Fusion", "Use AR Foundation's world tracking to stabilize pose when tracking quality drops"));
			}
		}

		private void DrawRuntimeStatusSection(XRTrackerManager manager)
		{
			EditorGUILayout.Space(8);
			EditorGUILayout.LabelField("Runtime Status", EditorStyles.boldLabel);

			using (new EditorGUI.DisabledScope(true))
			{
				EditorGUILayout.Toggle("Is Initialized", manager.IsInitialized);
				EditorGUILayout.Toggle("Is Tracking Ready", manager.IsTrackingReady);

				if (manager.SelectedCamera.HasValue)
				{
					EditorGUILayout.TextField("Selected Camera", manager.SelectedCamera.Value.name);
				}
				else
				{
					EditorGUILayout.TextField("Selected Camera", "None");
				}

				// Show camera count
				int cameraCount = manager.AvailableCameras?.Length ?? 0;
				EditorGUILayout.IntField("Available Cameras", cameraCount);

				// Global histogram peak (adaptive quality reference)
				float globalPeak = TrackedBodyManager.Instance?.GlobalHistogramPeak ?? TrackerDefaults.HISTOGRAM_GOOD;
				EditorGUILayout.Slider("Global Histogram Peak", globalPeak, 0f, 1f);
			}

			// Enumerate cameras button (play mode only)
			if (Application.isPlaying)
			{
				EditorGUILayout.Space(4);
				if (GUILayout.Button("Re-enumerate Cameras"))
				{
					manager.EnumerateCameras();
				}
			}

			// Auto-repaint during play mode
			if (Application.isPlaying)
			{
				Repaint();
			}
		}
	}
}