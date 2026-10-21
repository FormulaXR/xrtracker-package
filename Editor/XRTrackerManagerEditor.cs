using UnityEditor;
using UnityEngine;

namespace IV.FormulaTracker.Editor
{
	/// <summary>
	/// Custom inspector for XRTrackerManager.
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
		private SerializedProperty _followArWorldJumps;

		private const string FOLLOW_AR_WORLD_JUMPS_TOOLTIP =
			"When the AR session moves its world, the AR camera jumps farther in one frame than a hand can move a phone " +
			"(more than 25 centimetres and faster than 5 metres per second, or more than 45 degrees and faster than 900 degrees per second). " +
			"With this on, every tracked body is carried into the new world, so it keeps its place relative to the camera. " +
			"With this off, a body can lose the object after such a jump.";

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

			// AR Foundation fields (may not exist if HAS_AR_FOUNDATION not defined)
			_useARPoseFusion = serializedObject.FindProperty("_useARPoseFusion");
			_followArWorldJumps = serializedObject.FindProperty("_followArWorldJumps");
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
			DrawReferencesSection();
			DrawRuntimeStatusSection(manager);

			serializedObject.ApplyModifiedProperties();
		}

		private void DrawLicenseSection(XRTrackerManager manager)
		{
			EditorGUILayout.Space(4);
			EditorGUILayout.LabelField("License", EditorStyles.boldLabel);

			EditorGUILayout.PropertyField(_embeddedLicense, new GUIContent("License File",
				"Drag a .lic file here to embed the license in the build. " +
				"When it is empty, XRTracker looks for a .lic file in StreamingAssets and then in the persistent data folder. " +
				"Without a license, tracking stays blocked until you register one under Tools > XRTracker > License Registration."));

			// Runtime license info
			if (Application.isPlaying)
			{
				EditorGUILayout.Space(2);

				if (manager.LicenseTier == LicenseTier.None && manager.LicenseStatus == LicenseStatus.NotSet)
				{
					EditorGUILayout.HelpBox("No license found. Register for a free Developer license.", MessageType.Warning);
					if (GUILayout.Button("Open License Registration"))
					{
						EditorApplication.ExecuteMenuItem("Tools/XRTracker/License Registration");
					}
				}
				else
				{
					using (new EditorGUI.DisabledScope(true))
					{
						EditorGUILayout.EnumPopup("Tier", manager.LicenseTier);
						EditorGUILayout.EnumPopup("Status", manager.LicenseStatus);
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
					new GUIContent("Color Resolution", "Color camera resolution. A higher resolution gives more detail for validation and a lower frame rate."));
				EditorGUILayout.PropertyField(_realSenseDepthResolution,
					new GUIContent("Depth Resolution", "Depth camera resolution. A higher resolution gives better depth tracking and a lower frame rate."));

				EditorGUILayout.HelpBox(
					"Color at 1080p and depth at 720p are limited to 30 frames per second. " +
					"The resolution must be set before initialization.",
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
							"No calibration file is assigned, so XRTracker uses the default from the package Resources.",
							MessageType.Info);
					}
					else
					{
						EditorGUILayout.HelpBox(
							"No calibration file is assigned and no default exists. " +
							"Tracking will use estimated camera intrinsics, which may reduce accuracy. " +
							"Use Tools > XRTracker > Camera Calibration to calibrate your camera.",
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
						new GUIContent("Fallback to First", "When the named camera is not found, XRTracker uses the first available camera."));
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

			EditorGUILayout.PropertyField(_targetFps, new GUIContent("Target FPS",
				"Target frames per second of the tracking loop. " +
				"With the Injected (AR) image source this value matters only when Limit Fps is turned on in the AR Foundation Camera Feeder, " +
				"which then passes frames to the tracker at no more than this rate."));
			EditorGUILayout.PropertyField(_correspondenceIterations, new GUIContent("Correspondence Iterations",
				"Number of correspondence iterations per frame. A higher value is more accurate and slower."));
			EditorGUILayout.PropertyField(_updateIterations, new GUIContent("Update Iterations",
				"Number of pose update iterations per correspondence. A higher value refines the pose more and is slower."));
		}

		private void DrawReferencesSection()
		{
			EditorGUILayout.Space(8);
			EditorGUILayout.LabelField("References", EditorStyles.boldLabel);

			EditorGUILayout.PropertyField(_mainCamera, new GUIContent("Main Camera"));

			// AR Foundation settings. They are greyed out for a camera source that provides no camera pose,
			// because the manager discards them for that source at runtime whatever is serialized here.
			if (_useARPoseFusion != null)
			{
				var source = (ImageSource)_imageSource.enumValueIndex;
				string directory = _sequenceDirectory != null ? _sequenceDirectory.stringValue : null;
				PoseFusionSourcePolicy.Verdict verdict = PoseFusionSourcePolicy.Evaluate(source, directory);
				bool discarded = PoseFusionSourcePolicy.Discards(verdict);

				using (new EditorGUI.DisabledScope(discarded))
				{
					EditorGUILayout.PropertyField(_useARPoseFusion,
						new GUIContent("AR Foundation Fusion", "Use AR Foundation's world tracking to stabilize pose when tracking quality drops"));

				}

				// The row belongs to pose fusion, so it shows only while fusion is on and the camera source can carry it.
				if (_followArWorldJumps != null && _useARPoseFusion.boolValue && !discarded)
				{
					EditorGUI.indentLevel++;
					EditorGUILayout.PropertyField(_followArWorldJumps,
						new GUIContent("Follow AR World Jumps", FOLLOW_AR_WORLD_JUMPS_TOOLTIP));
					EditorGUI.indentLevel--;
				}

				if (discarded)
				{
					EditorGUILayout.HelpBox(
						"Pose fusion needs an AR camera pose. This camera source has none, so it is ignored.",
						MessageType.Info);
				}
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