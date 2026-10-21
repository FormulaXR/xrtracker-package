using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace IV.FormulaTracker.Editor
{
	[CustomEditor(typeof(TrackedBody))]
	[CanEditMultipleObjects]
	public class TrackedBodyEditor : UnityEditor.Editor
	{
		// Hierarchy
		private SerializedProperty _trackedMotion;

		// Model
		private SerializedProperty _bodyId;
		private SerializedProperty _meshFilters;

		// Tracking Model
		private SerializedProperty _trackingModelAsset;
		private SerializedProperty _modelSettings;
		private SerializedProperty _trackingMethod;
		private SerializedProperty _enableDepthTracking;
		private SerializedProperty _edgeModality;

		// Initial Pose
		private SerializedProperty _initialPoseSource;
		private SerializedProperty _initialViewpoint;

		// Tracking
		private SerializedProperty _isStationary;
		private SerializedProperty _acceptSteadyFits;
		private SerializedProperty _holdDuringViewDip;
		private SerializedProperty _holdDuringFastMotion;
		private SerializedProperty _firstAcceptGuard;
		private SerializedProperty _smoothTime;
		private SerializedProperty _rotationStability;
		private SerializedProperty _positionStability;
		private SerializedProperty _silhouetteTracking;
		private SerializedProperty _multiScale;
		private SerializedProperty _depthTracking;
		private SerializedProperty _useCustomStartThreshold;
		private SerializedProperty _customQualityToStart;
		private SerializedProperty _useCustomConfirmThreshold;
		private SerializedProperty _customQualityToConfirmStart;
		private SerializedProperty _searchBeforeStart;
		private SerializedProperty _startSearchPeriodSteps;
		private SerializedProperty _startSearchMarginBar;
		private SerializedProperty _startSearchScoreFloor;
		private SerializedProperty _startSearchReachPixels;
		private SerializedProperty _startSearchConeHalfAngle;
		private SerializedProperty _startSearchConeSpacing;
		private SerializedProperty _startSearchRollRange;
		private SerializedProperty _startSearchFineHalfAngle;
		private SerializedProperty _startSearchWindowMargin;
		private SerializedProperty _startSearchDistanceCount;
		private SerializedProperty _startSearchScaleDistances;
		private SerializedProperty _useCustomStopThreshold;
		private SerializedProperty _customQualityToStop;
		private SerializedProperty _useCustomSilhouetteStartThreshold;
		private SerializedProperty _customSilhouetteQualityToStart;
		private SerializedProperty _useCustomSilhouetteStopThreshold;
		private SerializedProperty _customSilhouetteQualityToStop;

		// GPU Features
		private SerializedProperty _enableSilhouetteValidation;
		private SerializedProperty _enableTextureTracking;
		private SerializedProperty _enableOcclusion;
		private SerializedProperty _occlusionSettings;

		// Hierarchy (child-specific)
		private SerializedProperty _occludeParent;
		private SerializedProperty _assemblyMode;
		private SerializedProperty _assemblyQualityToConfirm;
		private SerializedProperty _attachManually;
		private SerializedProperty _freeCheck;
		private SerializedProperty _freeCheckDistanceTolerance;
		private SerializedProperty _freeCheckAngleTolerance;
		private SerializedProperty _checkView;
		private SerializedProperty _checkViewDistance;
		private SerializedProperty _checkViewAngle;
		private SerializedProperty _stillnessDistance;
		private SerializedProperty _stillnessAngle;
		private SerializedProperty _stillnessWindow;
		private SerializedProperty _presenceHold;

		// Events
		private SerializedProperty _onStartTracking;
		private SerializedProperty _onStopTracking;

		// Foldout states (persisted via EditorPrefs)
		private static bool _foldoutHierarchy = true;
		private static bool _foldoutTrackingModel = true;
		private static bool _foldoutLifecycle = false;
		private static bool _foldoutTracking = false;
		private static bool _foldoutSilhouette = false;
		private static bool _foldoutGPUFeatures = false;
		private static bool _foldoutEvents = false;

		// Icons
		private static GUIContent _addChildMeshesIcon;

		// Cached gizmo data (computed once on selection, not every repaint)
		private Bounds _cachedBounds;
		private float _cachedSphereRadius;
		private bool _gizmoCacheValid;

		private void OnEnable()
		{
			_gizmoCacheValid = false;
			_bodyId = serializedObject.FindProperty("_bodyId");

			// Hierarchy
			_trackedMotion = serializedObject.FindProperty("_trackedMotion");

			// Tracking Model
			_meshFilters = serializedObject.FindProperty("_meshFilters");
			_trackingModelAsset = serializedObject.FindProperty("_trackingModelAsset");
			_modelSettings = serializedObject.FindProperty("_modelSettings");
			_trackingMethod = serializedObject.FindProperty("_trackingMethod");
			_enableDepthTracking = serializedObject.FindProperty("_enableDepthTracking");
			_edgeModality = serializedObject.FindProperty("_edgeTracking");

			// Initial Pose
			_initialPoseSource = serializedObject.FindProperty("_initialPoseSource");
			_initialViewpoint = serializedObject.FindProperty("_initialViewpoint");

			// Tracking
			_isStationary = serializedObject.FindProperty("_isStationary");
			_acceptSteadyFits = serializedObject.FindProperty("_acceptSteadyFits");
			_holdDuringViewDip = serializedObject.FindProperty("_holdDuringViewDip");
			_holdDuringFastMotion = serializedObject.FindProperty("_holdDuringFastMotion");
			_firstAcceptGuard = serializedObject.FindProperty("_firstAcceptGuard");
			_smoothTime = serializedObject.FindProperty("_smoothTime");
			_rotationStability = serializedObject.FindProperty("_rotationStability");
			_positionStability = serializedObject.FindProperty("_positionStability");
			_silhouetteTracking = serializedObject.FindProperty("_silhouetteTracking");
			_multiScale = serializedObject.FindProperty("_multiScale");
			_depthTracking = serializedObject.FindProperty("_depthTracking");
			_useCustomStartThreshold = serializedObject.FindProperty("_useCustomStartThreshold");
			_customQualityToStart = serializedObject.FindProperty("_customQualityToStart");
			_useCustomConfirmThreshold = serializedObject.FindProperty("_useCustomConfirmThreshold");
			_customQualityToConfirmStart = serializedObject.FindProperty("_customQualityToConfirmStart");
			_searchBeforeStart = serializedObject.FindProperty("_searchBeforeStart");
			_startSearchPeriodSteps = serializedObject.FindProperty("_startSearchPeriodSteps");
			_startSearchMarginBar = serializedObject.FindProperty("_startSearchMarginBar");
			_startSearchScoreFloor = serializedObject.FindProperty("_startSearchScoreFloor");
			_startSearchReachPixels = serializedObject.FindProperty("_startSearchReachPixels");
			_startSearchConeHalfAngle = serializedObject.FindProperty("_startSearchConeHalfAngle");
			_startSearchConeSpacing = serializedObject.FindProperty("_startSearchConeSpacing");
			_startSearchRollRange = serializedObject.FindProperty("_startSearchRollRange");
			_startSearchFineHalfAngle = serializedObject.FindProperty("_startSearchFineHalfAngle");
			_startSearchWindowMargin = serializedObject.FindProperty("_startSearchWindowMargin");
			_startSearchDistanceCount = serializedObject.FindProperty("_startSearchDistanceCount");
			_startSearchScaleDistances = serializedObject.FindProperty("_startSearchScaleDistances");
			_useCustomStopThreshold = serializedObject.FindProperty("_useCustomStopThreshold");
			_customQualityToStop = serializedObject.FindProperty("_customQualityToStop");
			_useCustomSilhouetteStartThreshold = serializedObject.FindProperty("_useCustomSilhouetteStartThreshold");
			_customSilhouetteQualityToStart = serializedObject.FindProperty("_customSilhouetteQualityToStart");
			_useCustomSilhouetteStopThreshold = serializedObject.FindProperty("_useCustomSilhouetteStopThreshold");
			_customSilhouetteQualityToStop = serializedObject.FindProperty("_customSilhouetteQualityToStop");

			// GPU Features
			_enableSilhouetteValidation = serializedObject.FindProperty("_enableSilhouetteValidation");
			_enableTextureTracking = serializedObject.FindProperty("_enableTextureTracking");
			_enableOcclusion = serializedObject.FindProperty("_enableOcclusion");
			_occlusionSettings = serializedObject.FindProperty("_occlusionSettings");

			// Hierarchy (child-specific)
			_occludeParent = serializedObject.FindProperty("_occludeParent");
			_assemblyMode = serializedObject.FindProperty("_assemblyMode");
			_assemblyQualityToConfirm = serializedObject.FindProperty("_assemblyQualityToConfirm");
			_attachManually = serializedObject.FindProperty("_attachManually");
			_freeCheck = serializedObject.FindProperty("_freeCheck");
			_freeCheckDistanceTolerance = serializedObject.FindProperty("_freeCheckDistanceTolerance");
			_freeCheckAngleTolerance = serializedObject.FindProperty("_freeCheckAngleTolerance");
			_checkView = serializedObject.FindProperty("_checkView");
			_checkViewDistance = serializedObject.FindProperty("_checkViewDistance");
			_checkViewAngle = serializedObject.FindProperty("_checkViewAngle");
			_stillnessDistance = serializedObject.FindProperty("_stillnessDistance");
			_stillnessAngle = serializedObject.FindProperty("_stillnessAngle");
			_stillnessWindow = serializedObject.FindProperty("_stillnessWindow");
			_presenceHold = serializedObject.FindProperty("_presenceHold");

			// Events
			_onStartTracking = serializedObject.FindProperty("_onStartTracking");
			_onStopTracking = serializedObject.FindProperty("_onStopTracking");

			// Initialize icon
			_addChildMeshesIcon ??= new GUIContent(
				EditorGUIUtility.IconContent("d_Toolbar Plus").image,
				"Add all child MeshFilters to the list");
		}

		public override void OnInspectorGUI()
		{
			serializedObject.Update();

			var trackedBody = (TrackedBody)target;

			// Detect parent from hierarchy (not serialized anymore)
			var detectedParent = trackedBody.FindParentInHierarchy();
			bool hasParent = detectedParent != null;

			// IsActiveMode: no parent OR any TrackedMotion flags set
			bool isActiveMode = !hasParent || _trackedMotion.intValue != 0;

			// Script field (read-only)
			using (new EditorGUI.DisabledScope(true))
			{
				EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));
			}

			// Body ID - always visible at top
			EditorGUILayout.PropertyField(_bodyId, new GUIContent("Body ID"));

			EditorGUILayout.Space(4);

			// Collapsible sections
			DrawHierarchySection(trackedBody, detectedParent, hasParent);
			DrawTrackingModelSection(trackedBody);
			DrawLifecycleSection(trackedBody, hasParent, isActiveMode);
			DrawTrackingSection(isActiveMode, hasParent);
			DrawGPUFeaturesSection();
			DrawEventsSection(hasParent);
			DrawRuntimeStatusSection(trackedBody);

			if (serializedObject.ApplyModifiedProperties())
				_gizmoCacheValid = false;
		}

		private static bool _foldoutChildren = false;

		private static List<TrackedBody> FindChildrenInHierarchy(TrackedBody body)
		{
			var children = new List<TrackedBody>();
			FindChildrenRecursive(body.transform, children);
			return children;
		}

		private static void FindChildrenRecursive(Transform parent, List<TrackedBody> results)
		{
			foreach (Transform child in parent)
			{
				var body = child.GetComponent<TrackedBody>();
				if (body != null)
					results.Add(body); // Don't recurse - children belong to this body
				else
					FindChildrenRecursive(child, results);
			}
		}

		private void DrawHierarchySection(TrackedBody trackedBody, TrackedBody detectedParent, bool hasParent)
		{
			var children = hasParent ? null : FindChildrenInHierarchy(trackedBody);
			bool hasChildren = children != null && children.Count > 0;

			// Skip section entirely if no hierarchy relationships
			if (!hasParent && !hasChildren)
				return;

			_foldoutHierarchy = EditorGUILayout.Foldout(_foldoutHierarchy, "Hierarchy", true, EditorStyles.foldoutHeader);
			if (_foldoutHierarchy)
			{
				BeginSectionContent();

				if (hasParent)
				{
					// Show parent as read-only
					EditorGUILayout.BeginHorizontal();
					EditorGUILayout.PrefixLabel("Parent");
					using (new EditorGUI.DisabledScope(true))
					{
						EditorGUILayout.ObjectField(detectedParent, typeof(TrackedBody), true);
					}

					EditorGUILayout.EndHorizontal();

					EditorGUILayout.PropertyField(_trackedMotion, new GUIContent("Tracked Motion",
						"Which degrees of freedom the tracker may optimize relative to the parent.\n\n" +
						"None means a rigid attachment, and the pose follows the parent.\n" +
						"Any other value means active tracking with the chosen degrees of freedom."));

					// Only show Occlude Parent when parent uses silhouette (edge handles occlusion automatically)
					if (detectedParent != null && detectedParent.EnableOcclusion
						&& detectedParent.TrackingMethod != TrackingMethod.Edge)
					{
						EditorGUILayout.PropertyField(_occludeParent, new GUIContent("Occlude Parent",
							"When enabled, this child body occludes its parent during tracking. " +
							"Use it when assembling parts that cover portions of the parent."));
					}

					EditorGUILayout.Space(4);
					EditorGUILayout.PropertyField(_assemblyMode, new GUIContent("Assembly Mode",
						"Enable it for parts that are physically installed during operation. " +
						"The child does not affect the parent pose until the quality confirms that the part is present."));

					if (_assemblyMode.boolValue)
					{
						EditorGUI.indentLevel++;
						EditorGUILayout.PropertyField(_attachManually, new GUIContent("Attach Manually",
							"The part is scored but never joins the parent's solve until the application calls Attach. " +
							"With this on, Quality to Confirm is not used."));
						using (new EditorGUI.DisabledScope(_attachManually.boolValue))
						{
							EditorGUILayout.PropertyField(_assemblyQualityToConfirm, new GUIContent("Quality to Confirm",
								"The quality threshold (0-1) that confirms the part is installed."));
						}
						if (_attachManually.boolValue)
						{
							EditorGUILayout.PropertyField(_freeCheck, new GUIContent("Free Check",
								"While the part is not attached, it settles its own pose a few millimetres and degrees around its seat, " +
								"without ever moving the parent, and it is scored where it settled. Read only by a part with no tracked motion."));
							if (_freeCheck.boolValue)
							{
								EditorGUI.indentLevel++;
								EditorGUILayout.PropertyField(_freeCheckDistanceTolerance, new GUIContent("Distance Tolerance (m)",
									"How far the centre of the part may settle from its seat and still be in its seat."));
								EditorGUILayout.PropertyField(_freeCheckAngleTolerance, new GUIContent("Angle Tolerance (degrees)",
									"How far the part may turn from its seat and still be in its seat."));
								EditorGUI.indentLevel--;
							}
						}
						EditorGUILayout.PropertyField(_checkView, new GUIContent("Check View",
							"Optional camera pose that the part is checked from. With it, the presence reading judges the part only " +
							"while the camera is near this view and still, from the evidence of a whole hold."));
						if (_checkView.objectReferenceValue != null)
						{
							EditorGUI.indentLevel++;
							EditorGUILayout.PropertyField(_checkViewDistance, new GUIContent("View Distance (m)",
								"How far the camera may be from the check view and still be near it."));
							EditorGUILayout.PropertyField(_checkViewAngle, new GUIContent("View Angle (degrees)",
								"How far the camera's viewing direction may turn from the check view's and still be near it."));
							EditorGUILayout.PropertyField(_stillnessDistance, new GUIContent("Stillness Distance (m)",
								"How far the camera may move over the stillness window and still count as still."));
							EditorGUILayout.PropertyField(_stillnessAngle, new GUIContent("Stillness Angle (degrees)",
								"How far the camera may turn over the stillness window and still count as still."));
							EditorGUILayout.PropertyField(_stillnessWindow, new GUIContent("Stillness Window (s)",
								"How long the camera must stay within the stillness bounds to count as still."));
							EditorGUILayout.PropertyField(_presenceHold, new GUIContent("Presence Hold (s)",
								"How long the evidence of one judgement is gathered near the check view with the camera still."));
							EditorGUI.indentLevel--;
						}
						EditorGUI.indentLevel--;
					}
				}
				else if (hasChildren)
				{
					// Show children for root bodies (collapsible)
					_foldoutChildren = EditorGUILayout.Foldout(_foldoutChildren, $"Children ({children.Count})", true);
					if (_foldoutChildren)
					{
						EditorGUI.indentLevel++;
						using (new EditorGUI.DisabledScope(true))
						{
							foreach (var child in children)
							{
								EditorGUILayout.ObjectField(child, typeof(TrackedBody), true);
							}
						}

						EditorGUI.indentLevel--;
					}
				}

				EndSectionContent();
			}
		}

		private static void BeginSectionContent()
		{
			EditorGUI.indentLevel++;
		}

		private static void EndSectionContent()
		{
			EditorGUI.indentLevel--;
			EditorGUILayout.Space(2);
		}

		private void DrawTrackingModelSection(TrackedBody trackedBody)
		{
			_foldoutTrackingModel = EditorGUILayout.Foldout(_foldoutTrackingModel, "Tracking Model", true, EditorStyles.foldoutHeader);
			if (_foldoutTrackingModel)
			{
				BeginSectionContent();

				// Mesh Filters first
				DrawMeshFiltersField();

				EditorGUILayout.Space(4);

				// Mode and depth
				EditorGUILayout.PropertyField(_trackingMethod, new GUIContent("Mode",
					"Silhouette works best when a good part of the outline is visible with good contrast, and it handles faster motion. It needs a tracking model.\n" +
					"Edge works in low-contrast scenes and when mostly internal edges are visible, such as close to large machinery. It also works with external edges, but it is less resilient to fast motion. It needs no tracking model."));

				EditorGUILayout.PropertyField(_enableDepthTracking, new GUIContent("Enable Depth",
					"Uses depth measurements to help the fit. The depth comes from LiDAR on iOS devices that have it, " +
					"from the depth image of Android devices that provide one (through an AROcclusionManager on the AR camera), " +
					"or from a RealSense camera on Windows (with the image source set to RealSense). " +
					"It needs a depth model in the Tracking Model Asset."));

				bool silhouetteEnabled = _trackingMethod.enumValueIndex == (int)TrackingMethod.Silhouette;
				bool needsModel = silhouetteEnabled || _enableDepthTracking.boolValue;

				// Model settings, asset, and generate button only when silhouette or depth is enabled
				if (needsModel)
				{
					EditorGUILayout.Space(4);

					if (silhouetteEnabled)
					{
						EditorGUILayout.HelpBox(
							"Silhouette data encodes the visual profile of the object from multiple viewpoints. " +
							"Generating it in advance gives a faster startup, and leaving the asset empty generates it at runtime.",
							MessageType.None);
					}

					DrawModelSettings();
					EditorGUILayout.PropertyField(_trackingModelAsset, new GUIContent("Asset",
						"The tracking model asset that holds the generated silhouette model and depth model."));

					var currentAsset = _trackingModelAsset.objectReferenceValue as TrackingModelAsset;

					// Show warning if asset exists but scale doesn't match
					if (currentAsset != null && currentAsset.HasValidData)
					{
						float currentScale = trackedBody.transform.lossyScale.x;
						if (!Mathf.Approximately(currentAsset.GeneratedAtScale, currentScale))
						{
							EditorGUILayout.HelpBox(
								$"Tracking model was generated at scale {currentAsset.GeneratedAtScale:F3}, " +
								$"but current scale is {currentScale:F3}. Regenerate for accurate tracking.",
								MessageType.Warning);
						}
					}

					EditorGUILayout.Space(4);
					string buttonLabel = currentAsset != null ? "Update Tracking Model" : "Generate Tracking Model";

					if (GUILayout.Button(buttonLabel))
					{
						SilhouetteModelGenerator.GenerateForTrackedBody(trackedBody);
					}
				}

				EndSectionContent();
			}
		}

		private void DrawModelSettings()
		{
			_modelSettings.isExpanded = EditorGUILayout.Foldout(_modelSettings.isExpanded,
				_modelSettings.displayName, true);

			if (!_modelSettings.isExpanded) return;

			EditorGUI.indentLevel++;

			// Standard fields
			EditorGUILayout.PropertyField(_modelSettings.FindPropertyRelative("sphereRadius"));
			EditorGUILayout.PropertyField(_modelSettings.FindPropertyRelative("nDivides"));
			EditorGUILayout.PropertyField(_modelSettings.FindPropertyRelative("nPoints"));
			EditorGUILayout.PropertyField(_modelSettings.FindPropertyRelative("maxRadiusDepthOffset"));
			EditorGUILayout.PropertyField(_modelSettings.FindPropertyRelative("strideDepthOffset"));
			EditorGUILayout.PropertyField(_modelSettings.FindPropertyRelative("imageSize"));

			// Viewpoint preset
			EditorGUILayout.Space(2);
			var presetProp = _modelSettings.FindPropertyRelative("viewpointPreset");
			EditorGUILayout.PropertyField(presetProp, new GUIContent("Viewpoint Coverage",
				"Which viewing angles the model includes. A smaller range reduces the model size for objects that are only seen from certain directions."));

			bool hasElevationFilter = presetProp.enumValueIndex != (int)ViewpointPreset.FullSphere;

			if (presetProp.enumValueIndex == (int)ViewpointPreset.Custom)
			{
				EditorGUI.indentLevel++;
				EditorGUILayout.PropertyField(_modelSettings.FindPropertyRelative("minElevation"),
					new GUIContent("Min Elevation", "The lowest elevation angle in degrees. A value of -90 looks from below, 0 from the side and 90 from above."));
				EditorGUILayout.PropertyField(_modelSettings.FindPropertyRelative("maxElevation"),
					new GUIContent("Max Elevation", "The highest elevation angle in degrees. A value of -90 looks from below, 0 from the side and 90 from above."));
				EditorGUI.indentLevel--;
			}
			else if (presetProp.enumValueIndex == (int)ViewpointPreset.UpperHemisphere)
			{
				EditorGUILayout.HelpBox("Top and side views (-10° to 90°). The views from below are cut.", MessageType.None);
			}
			else if (presetProp.enumValueIndex == (int)ViewpointPreset.SideRing)
			{
				EditorGUILayout.HelpBox("Horizontal views (-20° to 30°), for objects at camera height.", MessageType.None);
			}

			// Horizontal filter
			EditorGUILayout.Space(2);
			var horizEnabledProp = _modelSettings.FindPropertyRelative("enableHorizontalFilter");
			EditorGUILayout.PropertyField(horizEnabledProp, new GUIContent("Horizontal Filter",
				"Limits the viewpoints to a horizontal (azimuth) range around a forward direction. It is useful for wide or flat objects."));

			if (horizEnabledProp.boolValue)
			{
				EditorGUI.indentLevel++;
				EditorGUILayout.PropertyField(_modelSettings.FindPropertyRelative("minHorizontal"),
					new GUIContent("Min Angle", "The smallest azimuth in degrees. A value of -180 is behind and 0 is forward."));
				EditorGUILayout.PropertyField(_modelSettings.FindPropertyRelative("maxHorizontal"),
					new GUIContent("Max Angle", "The largest azimuth in degrees. A value of 0 is forward and 180 is behind."));
				EditorGUILayout.PropertyField(_modelSettings.FindPropertyRelative("forwardAxis"),
					new GUIContent("Forward Axis", "The reference direction that the azimuth is measured from."));

				// Check if forward is parallel to up
				var fwdAxisEnum = (ForwardAxis)_modelSettings.FindPropertyRelative("forwardAxis").enumValueIndex;
				var upAxisEnum = (ForwardAxis)_modelSettings.FindPropertyRelative("upAxis").enumValueIndex;
				Vector3 fwdVec = ModelSettings.ForwardAxisToVector(fwdAxisEnum);
				Vector3 upVec = ModelSettings.ForwardAxisToVector(upAxisEnum);
				if (Mathf.Abs(Vector3.Dot(fwdVec, upVec)) > 0.99f)
				{
					EditorGUILayout.HelpBox("The forward axis is parallel to the up axis, so the horizontal filter is ignored.", MessageType.Warning);
				}
				EditorGUI.indentLevel--;
			}

			// Up Axis — only relevant when a viewpoint filter is active
			if (hasElevationFilter || horizEnabledProp.boolValue)
			{
				EditorGUILayout.Space(2);
				EditorGUILayout.PropertyField(_modelSettings.FindPropertyRelative("upAxis"),
					new GUIContent("Up Axis", "Which axis of the object points up. The elevation angles are measured from this direction. Change it for models that were authored with Z pointing up."));
			}

			EditorGUI.indentLevel--;
		}

		private void DrawMeshFiltersField()
		{
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.PropertyField(_meshFilters, true);

			// Inline icon button
			if (GUILayout.Button(_addChildMeshesIcon, GUILayout.Width(24), GUILayout.Height(EditorGUIUtility.singleLineHeight)))
			{
				foreach (var t in targets)
				{
					var body = (TrackedBody)t;
					var method = body.GetType().GetMethod("AddChildMeshes", BindingFlags.Instance | BindingFlags.NonPublic);
					method?.Invoke(body, null);
					EditorUtility.SetDirty(body);
				}
			}

			EditorGUILayout.EndHorizontal();
		}

		private void OnSceneGUI()
		{
			var trackedBody = (TrackedBody)target;

			if (!_gizmoCacheValid)
			{
				_cachedBounds = trackedBody.ComputeLocalBounds();
				_cachedSphereRadius = trackedBody.GetEffectiveSphereRadius();
				_gizmoCacheValid = true;
			}

			if (_cachedBounds.size == Vector3.zero)
				return;

			var t = trackedBody.transform;
			Vector3 center = t.TransformPoint(_cachedBounds.center);
			float sphereRadius = _cachedSphereRadius * t.lossyScale.x;

			// Draw bounds box
			Handles.color = new Color(0f, 1f, 1f, 0.7f);
			Matrix4x4 matrix = Matrix4x4.TRS(center, t.rotation, Vector3.one);
			Vector3 scaledSize = Vector3.Scale(_cachedBounds.size, t.lossyScale);

			using (new Handles.DrawingScope(matrix))
			{
				DrawWireCube(Vector3.zero, scaledSize);
				DrawDimensionLabels(scaledSize);
			}

			// The viewpoints belong to the silhouette model and the depth model, which are generated from them.
			// A body that tracks by the edge method without depth uses neither, so it shows only the bounds.
			if (!trackedBody.EnableSilhouetteTracking && !trackedBody.EnableDepthTracking)
				return;

			// Read viewpoint filter settings
			GetViewpointFilterSettings(out float minElev, out float maxElev,
				out bool horizEnabled, out float minHoriz, out float maxHoriz,
				out Vector3 forwardDir, out Vector3 upDir);
			bool isFiltered = minElev > -89.9f || maxElev < 89.9f || horizEnabled;

			// Compute orthogonal reference axes from up direction
			Vector3 refRight = Mathf.Abs(Vector3.Dot(upDir, Vector3.right)) < 0.99f
				? Vector3.Cross(upDir, Vector3.right).normalized
				: Vector3.Cross(upDir, Vector3.forward).normalized;
			Vector3 refFwd = Vector3.Cross(refRight, upDir).normalized;

			// Draw sphere/viewpoint coverage in object-local space
			using (new Handles.DrawingScope(matrix))
			{
				if (isFiltered)
				{
					// Faint reference sphere — 3 discs aligned to up axis
					Handles.color = new Color(1f, 1f, 0f, 0.08f);
					Handles.DrawWireDisc(Vector3.zero, upDir, sphereRadius);
					Handles.DrawWireDisc(Vector3.zero, refRight, sphereRadius);
					Handles.DrawWireDisc(Vector3.zero, refFwd, sphereRadius);

					// Bright coverage boundary
					DrawViewpointCoverage(sphereRadius, minElev, maxElev,
						horizEnabled, minHoriz, maxHoriz, forwardDir, upDir);
				}
				else
				{
					// Full sphere — 3 reference discs
					Handles.color = new Color(1f, 1f, 0f, 0.3f);
					Handles.DrawWireDisc(Vector3.zero, Vector3.up, sphereRadius);
					Handles.DrawWireDisc(Vector3.zero, Vector3.right, sphereRadius);
					Handles.DrawWireDisc(Vector3.zero, Vector3.forward, sphereRadius);
				}
			}

			// Draw info label
			Handles.color = Color.white;
			string info = $"radius {sphereRadius:F3} m  {trackedBody.GetViewpointCount()} views  {trackedBody.GetPointsPerView()} points";
			Handles.Label(center + t.up * (scaledSize.y * 0.5f + 0.05f), info, EditorStyles.whiteBoldLabel);
		}

		private void GetViewpointFilterSettings(out float minElev, out float maxElev,
			out bool horizEnabled, out float minHoriz, out float maxHoriz,
			out Vector3 forwardDir, out Vector3 upDir)
		{
			// Read directly from target — serializedObject cannot be used in OnSceneGUI
			var settings = ((TrackedBody)target).ModelSettings;

			settings.GetEffectiveElevation(out minElev, out maxElev);
			upDir = ModelSettings.ForwardAxisToVector(settings.upAxis);
			forwardDir = ModelSettings.ForwardAxisToVector(settings.forwardAxis);

			settings.GetEffectiveHorizontal(out horizEnabled, out minHoriz, out maxHoriz,
				out Vector3 _);
		}

		private static Vector3 SphericalToCartesian(float radius, float elevDeg, float azDeg,
			Vector3 fwd, Vector3 right, Vector3 up)
		{
			float elevRad = elevDeg * Mathf.Deg2Rad;
			float azRad = azDeg * Mathf.Deg2Rad;
			float upComponent = Mathf.Sin(elevRad) * radius;
			float latR = Mathf.Cos(elevRad) * radius;
			Vector3 planeDir = fwd * Mathf.Cos(azRad) - right * Mathf.Sin(azRad);
			return up * upComponent + planeDir * latR;
		}

		private static void DrawLatitudeArc(float radius, float elevDeg,
			float azMin, float azMax, Vector3 fwd, Vector3 right, Vector3 up, int segments)
		{
			float step = (azMax - azMin) / segments;
			Vector3 prev = SphericalToCartesian(radius, elevDeg, azMin, fwd, right, up);
			for (int i = 1; i <= segments; i++)
			{
				Vector3 curr = SphericalToCartesian(radius, elevDeg, azMin + step * i, fwd, right, up);
				Handles.DrawLine(prev, curr);
				prev = curr;
			}
		}

		private static void DrawMeridianArc(float radius, float azDeg,
			float elevMin, float elevMax, Vector3 fwd, Vector3 right, Vector3 up, int segments)
		{
			float step = (elevMax - elevMin) / segments;
			Vector3 prev = SphericalToCartesian(radius, elevMin, azDeg, fwd, right, up);
			for (int i = 1; i <= segments; i++)
			{
				Vector3 curr = SphericalToCartesian(radius, elevMin + step * i, azDeg, fwd, right, up);
				Handles.DrawLine(prev, curr);
				prev = curr;
			}
		}

		private static void DrawViewpointCoverage(float radius, float minElev, float maxElev,
			bool horizEnabled, float minHoriz, float maxHoriz, Vector3 forwardDir, Vector3 upDir)
		{
			const int segments = 64;
			Vector3 right = Vector3.Cross(upDir, forwardDir).normalized;

			bool elevFiltered = minElev > -89.9f || maxElev < 89.9f;
			float azMin = horizEnabled ? minHoriz : -180f;
			float azMax = horizEnabled ? maxHoriz : 180f;

			// Latitude arcs at elevation boundaries
			Handles.color = new Color(1f, 1f, 0f, 0.7f);
			if (elevFiltered)
			{
				DrawLatitudeArc(radius, minElev, azMin, azMax, forwardDir, right, upDir, segments);
				DrawLatitudeArc(radius, maxElev, azMin, azMax, forwardDir, right, upDir, segments);
			}

			// Meridian arcs at azimuth boundaries
			if (horizEnabled)
			{
				float eLow = elevFiltered ? minElev : -90f;
				float eHigh = elevFiltered ? maxElev : 90f;
				DrawMeridianArc(radius, minHoriz, eLow, eHigh, forwardDir, right, upDir, segments);
				DrawMeridianArc(radius, maxHoriz, eLow, eHigh, forwardDir, right, upDir, segments);

				// Equator ring to show the horizontal plane when no elevation filter
				if (!elevFiltered)
				{
					Handles.color = new Color(1f, 1f, 0f, 0.4f);
					DrawLatitudeArc(radius, 0f, minHoriz, maxHoriz, forwardDir, right, upDir, segments);
				}

				// Forward direction indicator (green) — always at equator (0° elevation)
				Handles.color = new Color(0f, 1f, 0f, 0.6f);
				Vector3 fwdPoint = SphericalToCartesian(radius * 1.15f, 0f, 0f, forwardDir, right, upDir);
				Handles.DrawLine(Vector3.zero, fwdPoint);
			}
			else if (elevFiltered)
			{
				// Elevation only: draw 4 reference meridians
				Handles.color = new Color(1f, 1f, 0f, 0.4f);
				DrawMeridianArc(radius, 0f, minElev, maxElev, forwardDir, right, upDir, segments / 4);
				DrawMeridianArc(radius, 90f, minElev, maxElev, forwardDir, right, upDir, segments / 4);
				DrawMeridianArc(radius, 180f, minElev, maxElev, forwardDir, right, upDir, segments / 4);
				DrawMeridianArc(radius, -90f, minElev, maxElev, forwardDir, right, upDir, segments / 4);
			}
		}

		private static void DrawWireCube(Vector3 center, Vector3 size)
		{
			Vector3 half = size * 0.5f;
			Vector3[] corners = new Vector3[8]
			{
				center + new Vector3(-half.x, -half.y, -half.z),
				center + new Vector3(half.x, -half.y, -half.z),
				center + new Vector3(half.x, -half.y, half.z),
				center + new Vector3(-half.x, -half.y, half.z),
				center + new Vector3(-half.x, half.y, -half.z),
				center + new Vector3(half.x, half.y, -half.z),
				center + new Vector3(half.x, half.y, half.z),
				center + new Vector3(-half.x, half.y, half.z),
			};

			// Bottom
			Handles.DrawLine(corners[0], corners[1]);
			Handles.DrawLine(corners[1], corners[2]);
			Handles.DrawLine(corners[2], corners[3]);
			Handles.DrawLine(corners[3], corners[0]);
			// Top
			Handles.DrawLine(corners[4], corners[5]);
			Handles.DrawLine(corners[5], corners[6]);
			Handles.DrawLine(corners[6], corners[7]);
			Handles.DrawLine(corners[7], corners[4]);
			// Vertical
			Handles.DrawLine(corners[0], corners[4]);
			Handles.DrawLine(corners[1], corners[5]);
			Handles.DrawLine(corners[2], corners[6]);
			Handles.DrawLine(corners[3], corners[7]);
		}

		private static void DrawDimensionLabels(Vector3 size)
		{
			var style = new GUIStyle(EditorStyles.whiteBoldLabel) { alignment = TextAnchor.MiddleCenter };
			Vector3 half = size * 0.5f;

			// X dimension (red)
			Handles.color = new Color(1f, 0.4f, 0.4f, 1f);
			Vector3 xPos = new Vector3(0, -half.y - 0.02f, half.z);
			Handles.Label(xPos, $"X: {size.x:F3}m", style);

			// Y dimension (green)
			Handles.color = new Color(0.4f, 1f, 0.4f, 1f);
			Vector3 yPos = new Vector3(half.x + 0.02f, 0, half.z);
			Handles.Label(yPos, $"Y: {size.y:F3}m", style);

			// Z dimension (blue)
			Handles.color = new Color(0.4f, 0.4f, 1f, 1f);
			Vector3 zPos = new Vector3(half.x, -half.y - 0.02f, 0);
			Handles.Label(zPos, $"Z: {size.z:F3}m", style);
		}

		private void DrawLifecycleSection(TrackedBody trackedBody, bool hasParent, bool isActiveMode)
		{
			if (hasParent || !isActiveMode)
				return;

			_foldoutLifecycle = EditorGUILayout.Foldout(_foldoutLifecycle, "Lifecycle", true, EditorStyles.foldoutHeader);
			if (_foldoutLifecycle)
			{
				BeginSectionContent();

				// Initial pose
				EditorGUILayout.PropertyField(_initialPoseSource, new GUIContent("Initialization", "How the tracker determines the initial pose of the body."));

				if (_initialPoseSource.enumValueIndex == (int)InitialPoseSource.Viewpoint)
				{
					EditorGUILayout.BeginHorizontal();
					EditorGUILayout.PropertyField(_initialViewpoint, new GUIContent("Viewpoint",
						"The transform that represents the viewing position. When it is empty, the tracker uses its camera."));

					if (_initialViewpoint.objectReferenceValue == null)
					{
						if (GUILayout.Button("Create", GUILayout.Width(55)))
						{
							var body = (TrackedBody)target;
							var viewpointGO = new GameObject("Viewpoint");
							Undo.RegisterCreatedObjectUndo(viewpointGO, "Create Viewpoint");
							viewpointGO.transform.SetParent(body.transform, false);

							var cam = Camera.main;
							if (cam != null)
							{
								viewpointGO.transform.position = cam.transform.position;
								viewpointGO.transform.rotation = cam.transform.rotation;
							}

							_initialViewpoint.objectReferenceValue = viewpointGO.transform;
							serializedObject.ApplyModifiedProperties();
						}
					}
					else
					{
						var sceneView = SceneView.lastActiveSceneView;
						using (new EditorGUI.DisabledScope(sceneView == null))
						{
							if (GUILayout.Button("Align", GUILayout.Width(55)))
							{
								var viewpoint = (Transform)_initialViewpoint.objectReferenceValue;
								Undo.RecordObject(viewpoint, "Align Viewpoint to Scene View");
								viewpoint.position = sceneView.camera.transform.position;
								viewpoint.rotation = sceneView.camera.transform.rotation;
								EditorUtility.SetDirty(viewpoint);
							}
						}
					}
					EditorGUILayout.EndHorizontal();

					DrawUseCurrentPoseAsViewpoint((TrackedBody)target);
				}

				EditorGUILayout.Space(4);

				// The edge method and the silhouette method each have their own thresholds, because their scores are
				// on different scales. The inspector shows the pair of the method the body tracks by.
				bool edgeMethod = _trackingMethod.enumValueIndex == (int)TrackingMethod.Edge;
				SerializedProperty useStart = edgeMethod ? _useCustomStartThreshold : _useCustomSilhouetteStartThreshold;
				SerializedProperty startValue = edgeMethod ? _customQualityToStart : _customSilhouetteQualityToStart;
				SerializedProperty useStop = edgeMethod ? _useCustomStopThreshold : _useCustomSilhouetteStopThreshold;
				SerializedProperty stopValue = edgeMethod ? _customQualityToStop : _customSilhouetteQualityToStop;

				// Start threshold
				EditorGUILayout.PropertyField(useStart, new GUIContent("Custom Start Threshold", "Uses a custom quality threshold to start tracking instead of the default. The edge method and the silhouette method keep separate thresholds, and the inspector shows the one of the method this body tracks by."));

				if (useStart.boolValue)
				{
					EditorGUI.indentLevel++;
					EditorGUILayout.PropertyField(startValue, new GUIContent("Quality to Start", "The quality (0-1) that a start needs on consecutive frames. The default is " + FormatBar(edgeMethod ? TrackerDefaults.EDGE_QUALITY_TO_START : TrackerDefaults.QUALITY_TO_START) + "."));
					EditorGUI.indentLevel--;
				}

				// Confirm score, which only a body that tracks by the edge method reads
				if (edgeMethod)
				{
					EditorGUILayout.PropertyField(_useCustomConfirmThreshold, new GUIContent("Custom Confirm Score",
						"The score the last frame of a start must reach. When this is off, the last frame needs only the start score."));

					if (_useCustomConfirmThreshold.boolValue)
					{
						EditorGUI.indentLevel++;
						EditorGUILayout.PropertyField(_customQualityToConfirmStart, new GUIContent("Quality to Confirm",
							"The score that the last frame of a start must reach. A start needs the start score on 5 frames in a row and this score on the last one. " +
							"A value above the start score makes a start surer."));
						EditorGUI.indentLevel--;
					}

					// The search around the guide, which only a body that tracks by the edge method reads
					EditorGUILayout.PropertyField(_searchBeforeStart, new GUIContent("Search Before Start",
						"Before the first start, the tracker searches a window around the guide for the shape of the body, within a cone around the guide's view (5 degrees by default, a setting below), " +
						"and refines from the best hypothesis and not from the guide. " +
						"The search only takes over when the guide stands out of reach of the winner, and a guide in reach starts as it does without the search. " +
						"The start waits until the same winner was found on three searches in a row, and the start bars still verify the pose. " +
						"Under pose fusion the guide follows the camera until the start."));

					if (_searchBeforeStart.boolValue)
					{
						EditorGUI.indentLevel++;
						EditorGUILayout.PropertyField(_startSearchPeriodSteps, new GUIContent("Search Period (steps)",
							"How many tracking steps pass between two searches of the whole image. A value of 1 searches on every step."));
						EditorGUILayout.PropertyField(_startSearchMarginBar, new GUIContent("Margin Bar",
							"The margin the winner of a search must reach before a start is let through, as the score of the winner divided by the score of the best hypothesis that stands far from it. A value of 0 sets no margin bar."));
						EditorGUILayout.PropertyField(_startSearchScoreFloor, new GUIContent("Score Floor",
							"The score the winner of a search must reach before a start is let through, as the share of the outline points that find an edge of the same direction. A value of 0 sets no score floor."));
						EditorGUILayout.PropertyField(_startSearchReachPixels, new GUIContent("Reach (pixels)",
							"How far the guide may stand from the winner of a search, as the median on-screen displacement of the model's points in pixels, and still count as in reach. A guide in reach is left alone and the body starts as it does without the search. A value of 0 treats every guide as out of reach."));
						EditorGUILayout.PropertyField(_startSearchConeHalfAngle, new GUIContent("Cone Half Angle (degrees)",
							"The half angle of the cone of views the plugin draws around the guide's view, so 5 degrees at a spacing of 5 degrees is 7 views. The cost of the bank and of every search grows with the number of views."));
						EditorGUILayout.PropertyField(_startSearchConeSpacing, new GUIContent("Cone Spacing (degrees)",
							"The spacing between neighbouring views of the cone. The turns in the image plane are made at this step too."));
						EditorGUILayout.PropertyField(_startSearchRollRange, new GUIContent("Turn Range (degrees)",
							"The range of the turns in the image plane that the bank holds. The turns are zero and plus and minus each step of the spacing out to this value, so the zero turn is always held."));
						EditorGUILayout.PropertyField(_startSearchFineHalfAngle, new GUIContent("Fine Half Angle (degrees)",
							"The half angle of the fine stage, which scores the views within this angle of the winner at a step of 2.5 degrees. A value of 0 switches the fine stage off."));
						EditorGUILayout.PropertyField(_startSearchWindowMargin, new GUIContent("Window Margin (pixels)",
							"How far beyond the box of the guide's outline the search looks, in pixels of the search image (its longer side is at most 640 pixels). A template is placed only where its own outline box lies inside this window. A value of 0 searches the whole image."));
						EditorGUILayout.PropertyField(_startSearchDistanceCount, new GUIContent("Distances",
							"How many distances the bank holds around the guide's own. One is the guide's own distance, three are 0.92, 1.0 and 1.09 times it, and five add 0.85 and 1.2."));
						EditorGUILayout.PropertyField(_startSearchScaleDistances, new GUIContent("Scale Distances",
							"Off, the plugin draws every distance of every view. On, it draws each view once at the guide's own distance and makes the other distances by scaling that drawing, which is a third of the drawings at three distances."));
						EditorGUI.indentLevel--;
					}
				}

				// Stop threshold
				EditorGUILayout.PropertyField(useStop, new GUIContent("Custom Stop Threshold", "Uses a custom quality threshold to stop tracking instead of the default. The edge method and the silhouette method keep separate thresholds, and the inspector shows the one of the method this body tracks by."));

				if (useStop.boolValue)
				{
					EditorGUI.indentLevel++;
					EditorGUILayout.PropertyField(stopValue, new GUIContent("Quality to Stop", "The averaged quality (0-1) below which tracking is lost. The default is " + FormatBar(edgeMethod ? TrackerDefaults.EDGE_LOSE_TRACKING_THRESHOLD : TrackerDefaults.LOSE_TRACKING_THRESHOLD) + "."));
					EditorGUI.indentLevel--;
				}

				// Runtime buttons
				if (Application.isPlaying && trackedBody.IsRegistered)
				{
					EditorGUILayout.Space(4);
					EditorGUILayout.BeginHorizontal();

					using (new EditorGUI.DisabledScope(trackedBody.IsTracking))
					{
						if (GUILayout.Button(new GUIContent("Force Start", "Starts tracking at once, without waiting for the quality bars.")))
							trackedBody.ForceStartTracking();
					}

					if (GUILayout.Button(new GUIContent("Reset",
						"Returns the body to the pose it was placed at, stops tracking and keeps what the body learned.")))
						trackedBody.ResetTracking();

					if (GUILayout.Button(new GUIContent("Reset and forget",
						"Returns the body to the pose it was placed at, stops tracking and forgets what the body learned.")))
						trackedBody.ResetTracking(true);

					EditorGUILayout.EndHorizontal();
				}

				EndSectionContent();
			}
		}

		private void DrawTrackingSection(bool isActiveMode, bool hasParent)
		{
			// Only show tracking section for active mode bodies
			if (!isActiveMode)
				return;

			bool silhouetteEnabled = _trackingMethod.enumValueIndex == (int)TrackingMethod.Silhouette;
			bool edgeEnabled = _trackingMethod.enumValueIndex == (int)TrackingMethod.Edge;

			_foldoutTracking = EditorGUILayout.Foldout(_foldoutTracking, "Tracking", true, EditorStyles.foldoutHeader);
			if (_foldoutTracking)
			{
				BeginSectionContent();

				if (!hasParent)
				{
					EditorGUILayout.PropertyField(_isStationary,
						new GUIContent("Stationary",
							"The object is fixed in the world. This enables AR pose fusion. After stabilization the pose is held in the world frame of the AR session, " +
							"and the tracker provides corrections when the quality is high."));

					if (_isStationary.boolValue)
					{
						EditorGUI.indentLevel++;
						EditorGUILayout.PropertyField(_smoothTime,
							new GUIContent("Smooth Time",
								"The time in seconds over which pose corrections are smoothed. A value of 0 applies them instantly."));
						EditorGUILayout.HelpBox(
							"This needs AR Foundation for SLAM. Without it, tracking falls back to the standard method.",
							MessageType.Info);
						EditorGUI.indentLevel--;
					}
				}

				EditorGUILayout.PropertyField(_rotationStability);
				EditorGUILayout.PropertyField(_positionStability);

				// Silhouette settings
				if (silhouetteEnabled)
				{
					EditorGUILayout.Space(4);
					_foldoutSilhouette = EditorGUILayout.Foldout(_foldoutSilhouette, "Silhouette Tracking", true);
					if (_foldoutSilhouette)
					{
						EditorGUILayout.PropertyField(
							_silhouetteTracking.FindPropertyRelative("_edgeTolerance"),
							new GUIContent("Outline Color Separation",
								"How cleanly the tracker expects the colors of the object and the colors of the background to separate across the outline. " +
								"A higher value expects a clean separation. A lower value allows each side of the outline to hold some colors of the other side."));
						EditorGUILayout.PropertyField(
							_silhouetteTracking.FindPropertyRelative("_updateResponsiveness"),
							new GUIContent("Update Responsiveness",
								"How strongly the tracker adjusts the pose on each frame. " +
								"A lower value is more cautious. A higher value converges faster but may overshoot."));
						// Min Continuous Distance, Function Length, Histogram Bins,
						// Pyramid Scales, and Standard Deviations hidden from inspector.
						// Still settable via script (SilhouetteTrackingSettings / MultiScaleSettings).
					}
				}

				// Edge settings
				if (edgeEnabled)
				{
					DrawEdgeModalityContent(hasParent);
				}

				// Show depth tracking settings only when depth is enabled
				if (_enableDepthTracking.boolValue)
				{
					EditorGUILayout.Space(4);
					EditorGUILayout.PropertyField(_depthTracking, new GUIContent("Depth Tracking",
						"Advanced tuning for depth-based tracking. These settings control the correspondence matching and the occlusion handling."), true);
				}

				EndSectionContent();
			}
		}

		private void DrawEdgeModalityContent(bool hasParent)
		{
			EditorGUILayout.Space(4);
			_edgeModality.isExpanded = EditorGUILayout.Foldout(_edgeModality.isExpanded,
				new GUIContent("Edge Tracking",
					"Tuning parameters for edge tracking."), true);

			if (_edgeModality.isExpanded)
			{
				try
				{
					EditorGUILayout.PropertyField(_edgeModality.FindPropertyRelative("_depthEdgeThreshold"));
					EditorGUILayout.PropertyField(_edgeModality.FindPropertyRelative("_searchRadius"));
					EditorGUILayout.PropertyField(_edgeModality.FindPropertyRelative("_minGradient"));
					EditorGUILayout.PropertyField(_edgeModality.FindPropertyRelative("_sampleStep"));
					EditorGUILayout.PropertyField(_edgeModality.FindPropertyRelative("_keyframeRotationDeg"),
						new GUIContent("Rotation Threshold (degrees)",
							"How far the camera may rotate about its own centre, in degrees, before the tracker draws the edges of the model again. From 10 to 20 degrees, and a smaller value that a scene saved is read as 10."));
					EditorGUILayout.PropertyField(_edgeModality.FindPropertyRelative("_keyframeTranslation"),
						new GUIContent("Translation Threshold (m)",
							"How far the camera must move, in meters, from the pose of a stored view before the tracker draws the edges of the model again. The default is 0.03."));
					EditorGUILayout.PropertyField(_edgeModality.FindPropertyRelative("_enableCreaseEdges"));
					if (_edgeModality.FindPropertyRelative("_enableCreaseEdges").boolValue)
					{
						EditorGUI.indentLevel++;
						EditorGUILayout.PropertyField(_edgeModality.FindPropertyRelative("_creaseEdgeAngle"));
						EditorGUI.indentLevel--;
					}
					if (!hasParent)
					{
						// Read only by a stationary body that tracks by the edge method under AR pose fusion, so it lives here
						// and not under Stationary, which every tracking method shows. It is greyed while the body is not stationary.
						using (new EditorGUI.DisabledScope(!_isStationary.boolValue))
						{
							EditorGUILayout.PropertyField(_acceptSteadyFits,
								new GUIContent("Accept Steady Fits",
									"Applies with AR pose fusion to a stationary body that tracks by the edge method. " +
									"The body also accepts a fit that the coverage score passes, on the same start and stop bars as without AR, while the fit stays close to where AR predicts the object. " +
									"This helps soft or low-contrast real images keep the lock, keep correcting and show Tracked. " +
									"With AR, a stationary body also keeps its shown pose still while part of the object is hidden or the view is clearly weaker than usual, and takes new fits again once the view recovers. " +
									"With AR, a stationary body also keeps its shown pose while the object moves fast on screen. " +
									"With AR, a stationary body also takes its first fit only after 3 solves in a row agree within 8 millimeters and 2 degrees. " +
									"The three rows below switch the last three of these off one by one. " +
									"With this off, the body accepts only the fits that the strict confidence check passes."));

							// The three holds are part of what Accept Steady Fits turns on, so they are greyed while it is off.
							using (new EditorGUI.DisabledScope(!_acceptSteadyFits.boolValue))
							{
								EditorGUI.indentLevel++;
								EditorGUILayout.PropertyField(_holdDuringViewDip,
									new GUIContent("Hold During View Dips",
										"With AR, the shown pose stays still while the view health is below 0.8 of the usual level for this view, until the view has been healthy again for 5 steps in a row. " +
										"A dip can only begin after 30 healthy steps in a row. " +
										"With this off, new fits keep moving the pose while part of the object is hidden. " +
										"It needs Accept Steady Fits."));
								EditorGUILayout.PropertyField(_holdDuringFastMotion,
									new GUIContent("Hold During Fast Motion",
										"With AR, no new fit is stored while AR moves the object more than 6 pixels per frame on screen, counted for a camera with a focal length of 450 pixels. " +
										"The shown pose is carried by AR until the camera slows down. " +
										"The first fit and the first 30 steps after the start are never held back. " +
										"With this off, fits taken during fast motion keep moving the pose. " +
										"It needs Accept Steady Fits."));
								EditorGUILayout.PropertyField(_firstAcceptGuard,
									new GUIContent("First Accept Guard",
										"With AR, before the first fit is shown, 3 solves in a row must agree within 8 millimeters and 2 degrees. " +
										"The guard lets go after 30 steps, so a fit that never settles is still taken in the end. " +
										"With this off, the first fit that passes the other checks is taken at once. " +
										"It needs Accept Steady Fits."));
								EditorGUI.indentLevel--;
							}
						}
					}
					if (Application.isPlaying)
						DrawEdgeSettingsHeldNote();
					// Search Radius Scales and Standard Deviations hidden from inspector.
					// Still settable via script (EdgeTrackingSettings).
					EditorGUILayout.Space(2);
					using (new EditorGUI.DisabledScope(Application.isPlaying))
					{
						if (GUILayout.Button(new GUIContent("Reset Edge Settings to Defaults",
							"Puts every edge setting of this body back to the package default. " +
							"The silhouette, score and confirm settings are not touched. " +
							"The change can be undone.")))
						{
							ResetEdgeSettings();
						}
					}
				}
				catch (System.ArgumentException) { }
			}
		}

		/// <summary>
		/// While the scene plays, says which edge settings the tracker holds for the body, because the tracker reads them when the body registers
		/// and an edit made now does not always reach it.
		/// </summary>
		private void DrawEdgeSettingsHeldNote()
		{
			if (!(target is TrackedBody body))
				return;
			string held = body.RegisteredEdgeSettingsSummary;
			if (held.Length == 0)
				return;

			EditorGUILayout.Space(2);
			if (!body.EdgeSettingsDifferFromRegistration)
			{
				EditorGUILayout.HelpBox("The tracker holds " + held + ".", MessageType.Info);
			}
			else if (body.LiveEdgeSettingsUnavailable)
			{
				EditorGUILayout.HelpBox("The tracker still holds " + held + ". This plugin build cannot change edge settings while the body is registered. " +
					"Disable and enable the component, or play the scene again, to apply the new values.", MessageType.Warning);
			}
			else
			{
				EditorGUILayout.HelpBox("The tracker holds " + held + ". A new depth edge threshold, search radius, minimum gradient, sample step or crease edges setting " +
					"reaches it at the next tracking step. Every other edge setting is read when the body registers, so it needs the component to be disabled and enabled again.",
					MessageType.Info);
			}
		}

		private void ResetEdgeSettings()
		{
			serializedObject.ApplyModifiedProperties();
			foreach (Object selected in targets)
			{
				if (selected is TrackedBody body)
					EdgeSettingsReset.Reset(body);
			}
			serializedObject.Update();
		}

		private void DrawGPUFeaturesSection()
		{
			_foldoutGPUFeatures = EditorGUILayout.Foldout(_foldoutGPUFeatures, "GPU Features", true, EditorStyles.foldoutHeader);
			if (_foldoutGPUFeatures)
			{
				BeginSectionContent();

				EditorGUILayout.HelpBox(
					"These features use additional GPU resources. On mobile devices, consider turning them off.",
					MessageType.None);

				bool isSilhouette = _trackingMethod.enumValueIndex == (int)TrackingMethod.Silhouette;
				if (isSilhouette)
				{
					EditorGUILayout.PropertyField(_enableSilhouetteValidation, new GUIContent("Silhouette Validation",
						"Validates tracking points against rendered silhouette boundaries. Improves accuracy by rejecting false correspondences."));
				}

				EditorGUILayout.PropertyField(_enableTextureTracking, new GUIContent("Texture Tracking",
					"Adds tracking by the surface appearance of the object, as a complement to the Silhouette or the Edge method.\n\n" +
					"This does not use the Unity texture or material of the object. Instead, the tracker detects distinctive points " +
					"on the visible surface of the object in the camera image and matches them against the rendered model.\n\n" +
					"It works best for objects with distinctive surface patterns such as text, logos, scratches or textures, and it adds little for uniform or solid-colored objects. " +
					"It works only while the camera is farther from the object than about 1.5 times the radius of the object."));

				// The edge method handles occlusion automatically through the shared normal renderer
				bool isEdge = _trackingMethod.enumValueIndex == (int)TrackingMethod.Edge;
				if (!isEdge)
				{
					EditorGUILayout.PropertyField(_enableOcclusion, new GUIContent("Occlusion",
						"Handles occlusion for this body's tracking, based on depth. " +
						"Silhouette points that are hidden behind other objects (occluders) are ignored.\n\n" +
						"Child bodies with Occlude Parent enabled occlude this body automatically."));

					// Show occlusion settings only when occlusion is enabled
					if (_enableOcclusion.boolValue)
					{
						EditorGUI.indentLevel++;
						EditorGUILayout.PropertyField(_occlusionSettings, new GUIContent("Settings",
							"The occlusion detection parameters. Adjust the threshold for tight assemblies where parts are close together."), true);
						EditorGUI.indentLevel--;
					}
				}

				EndSectionContent();
			}
		}

		private void DrawEventsSection(bool hasParent)
		{
			_foldoutEvents = EditorGUILayout.Foldout(_foldoutEvents, "Events", true, EditorStyles.foldoutHeader);
			if (_foldoutEvents)
			{
				BeginSectionContent();

				EditorGUILayout.PropertyField(_onStartTracking, new GUIContent("On Start Tracking"));
				EditorGUILayout.PropertyField(_onStopTracking, new GUIContent("On Stop Tracking"));

				EndSectionContent();
			}
		}

		/// <summary>
		/// The button that keeps the pose the body is shown at as its initial Viewpoint. It is offered in play mode, while the body
		/// tracks and starts from a Viewpoint. The Viewpoint is set when play mode ends, because a change made in play mode is lost.
		/// </summary>
		private void DrawUseCurrentPoseAsViewpoint(TrackedBody body)
		{
			if (targets.Length != 1 ||
				!InitialViewpointCapture.IsOffered(Application.isPlaying, body.IsTracking, body.InitialPoseSource))
				return;

			using (new EditorGUI.DisabledScope(body.InitialViewpoint == null))
			{
				var content = new GUIContent("Use Current Pose as Initial Viewpoint",
					"Keeps the pose this body is shown at, seen from the camera, and sets the Viewpoint to it when play mode ends. " +
					"A body that starts from the Viewpoint then starts at this pose.");
				if (GUILayout.Button(content))
				{
					if (InitialViewpointCapture.TryCapture(body, out string message))
						Debug.Log("[XRTracker] " + message);
					else
						Debug.LogWarning("[XRTracker] " + message);
				}
			}

			if (InitialViewpointCapture.HasPending(body))
			{
				EditorGUILayout.HelpBox(
					"The Viewpoint is set to the kept pose when play mode ends, and the scene is then marked as changed.",
					MessageType.Info);
			}
		}

		private void DrawRuntimeStatusSection(TrackedBody trackedBody)
		{
			if (!Application.isPlaying)
				return;

			EditorGUILayout.Space(4);
			EditorGUILayout.LabelField("Runtime Status", EditorStyles.boldLabel);

			var status = trackedBody.LastStatus;

			using (new EditorGUI.DisabledScope(true))
			{
				EditorGUILayout.EnumPopup("Confidence", trackedBody.Confidence);
				EditorGUILayout.EnumPopup("Confidence Reason", trackedBody.ConfidenceReason);

				// Primary quality metrics (averaged). Under pose fusion the start reads the coverage score and the hold reads the
				// registration margin, so the two scores are shown apart, each with its bar. Otherwise both are one score.
				if (trackedBody.StartReadsCoverage)
				{
					EditorGUILayout.Slider(new GUIContent("Start Score", "The coverage score the start reads on five steps in a row."),
						trackedBody.StartScore, 0f, 1f);
					EditorGUILayout.LabelField("  Start Bar", trackedBody.QualityToStartTracking.ToString("F2"));
					EditorGUILayout.Slider(new GUIContent("Hold Confidence", "The registration margin that keeps or gives up the lock, averaged over the quality window."),
						trackedBody.TrackingQuality, 0f, 1f);
					EditorGUILayout.LabelField("  Stop Bar", trackedBody.QualityToStopTracking.ToString("F2"));
				}
				else
				{
					EditorGUILayout.Slider("Tracking Quality", trackedBody.TrackingQuality, 0f, 1f);
				}
				if (trackedBody.EnableSilhouetteTracking || status.HasRegionModality)
					EditorGUILayout.Slider("Shape Quality", trackedBody.ShapeQuality, 0f, 1f);

				// Silhouette tracking (only show if silhouette tracking is enabled)
				if (trackedBody.EnableSilhouetteTracking || status.HasRegionModality)
				{
					EditorGUILayout.Space(2);
					EditorGUILayout.LabelField("Silhouette Tracking", EditorStyles.miniLabel);
					EditorGUILayout.Slider("  Silhouette Visibility", trackedBody.SilhouetteVisibility, 0f, 1f);
					EditorGUILayout.LabelField("  Valid / Maximum Lines", $"{status.n_valid_lines} / {status.n_max_lines}");
				}

				// Depth tracking metrics (only show if depth tracking is enabled)
				if (trackedBody.EnableDepthTracking || status.HasDepthModality)
				{
					EditorGUILayout.Space(2);
					EditorGUILayout.LabelField("Depth Tracking", EditorStyles.miniLabel);
					EditorGUILayout.Slider("  Visibility", trackedBody.DepthVisibility, 0f, 1f);
					EditorGUILayout.LabelField("  Valid / Maximum Points", $"{status.n_valid_points} / {status.n_max_points}");
					EditorGUILayout.FloatField("  Mean Correspondence Distance", status.mean_correspondence_distance);
					EditorGUILayout.FloatField("  Distance Standard Deviation", status.correspondence_distance_stddev);
				}

				// Edge tracking metrics (only show if edge tracking is enabled)
				if (trackedBody.EnableEdgeTracking || status.HasEdgeModality)
				{
					EditorGUILayout.Space(2);
					EditorGUILayout.LabelField("Edge Tracking", EditorStyles.miniLabel);
					EditorGUILayout.Slider(trackedBody.StartReadsCoverage ? "  Instant Hold Confidence" : "  Instant Quality", trackedBody.InstantQuality, 0f, 1f);
					EditorGUILayout.Slider("  Sites That Agree (30 frames)", trackedBody.EdgeCoverageAverage, 0f, 1f);
					float trackingPct = status.n_total_edge_sites > 0 ? 100f * status.n_tracking_edge_sites / status.n_total_edge_sites : 0f;
					EditorGUILayout.LabelField("  Valid / Total Sites", $"{status.n_valid_edge_points} / {status.n_total_edge_sites}");
					EditorGUILayout.LabelField("  Tracking Coverage", $"{status.n_tracking_edge_sites} / {status.n_total_edge_sites} ({trackingPct:F0}%)");
					EditorGUILayout.Slider("  Projection Error (°)", trackedBody.ProjectionErrorAverage, 0f, 90f);
					EditorGUILayout.FloatField("  Median Residual (pixels)", status.mean_edge_residual);
				}
			}

			// Auto-repaint during play mode
			if (Application.isPlaying)
			{
				Repaint();
			}
		}

		private static string FormatBar(float bar) => bar.ToString("0.##", CultureInfo.InvariantCulture);

		private static string FormatBytes(long bytes)
		{
			if (bytes < 1024) return $"{bytes} B";
			if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1} KB";
			return $"{bytes / (1024f * 1024f):F2} MB";
		}
	}
}
