using System.Collections.Generic;
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
		private SerializedProperty _enableValidation;
		private SerializedProperty _validationThreshold;

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
		private SerializedProperty _smoothTime;
		private SerializedProperty _rotationStability;
		private SerializedProperty _positionStability;
		private SerializedProperty _silhouetteTracking;
		private SerializedProperty _multiScale;
		private SerializedProperty _depthTracking;
		private SerializedProperty _useCustomStartThreshold;
		private SerializedProperty _customQualityToStart;
		private SerializedProperty _useCustomStopThreshold;
		private SerializedProperty _customQualityToStop;

		// GPU Features
		private SerializedProperty _enableSilhouetteValidation;
		private SerializedProperty _enableTextureTracking;
		private SerializedProperty _enableOcclusion;
		private SerializedProperty _occlusionSettings;

		// Hierarchy (child-specific)
		private SerializedProperty _occludeParent;
		private SerializedProperty _assemblyMode;
		private SerializedProperty _assemblyQualityToConfirm;

		// Events
		private SerializedProperty _onStartTracking;
		private SerializedProperty _onStopTracking;
		private SerializedProperty _onBecomeValid;
		private SerializedProperty _onBecomeInvalid;

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
			_enableValidation = serializedObject.FindProperty("_enableValidation");
			_validationThreshold = serializedObject.FindProperty("_validationThreshold");

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
			_smoothTime = serializedObject.FindProperty("_smoothTime");
			_rotationStability = serializedObject.FindProperty("_rotationStability");
			_positionStability = serializedObject.FindProperty("_positionStability");
			_silhouetteTracking = serializedObject.FindProperty("_silhouetteTracking");
			_multiScale = serializedObject.FindProperty("_multiScale");
			_depthTracking = serializedObject.FindProperty("_depthTracking");
			_useCustomStartThreshold = serializedObject.FindProperty("_useCustomStartThreshold");
			_customQualityToStart = serializedObject.FindProperty("_customQualityToStart");
			_useCustomStopThreshold = serializedObject.FindProperty("_useCustomStopThreshold");
			_customQualityToStop = serializedObject.FindProperty("_customQualityToStop");

			// GPU Features
			_enableSilhouetteValidation = serializedObject.FindProperty("_enableSilhouetteValidation");
			_enableTextureTracking = serializedObject.FindProperty("_enableTextureTracking");
			_enableOcclusion = serializedObject.FindProperty("_enableOcclusion");
			_occlusionSettings = serializedObject.FindProperty("_occlusionSettings");

			// Hierarchy (child-specific)
			_occludeParent = serializedObject.FindProperty("_occludeParent");
			_assemblyMode = serializedObject.FindProperty("_assemblyMode");
			_assemblyQualityToConfirm = serializedObject.FindProperty("_assemblyQualityToConfirm");

			// Events
			_onStartTracking = serializedObject.FindProperty("_onStartTracking");
			_onStopTracking = serializedObject.FindProperty("_onStopTracking");
			_onBecomeValid = serializedObject.FindProperty("_onBecomeValid");
			_onBecomeInvalid = serializedObject.FindProperty("_onBecomeInvalid");

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
						"Which degrees of freedom can be optimized relative to parent.\n\n" +
						"None: Rigid attachment, pose follows parent.\n" +
						"Any flags: Active tracking with specified DOF."));

					// Only show Occlude Parent when parent uses silhouette (edge handles occlusion automatically)
					if (detectedParent != null && detectedParent.EnableOcclusion
						&& detectedParent.TrackingMethod != TrackingMethod.Edge)
					{
						EditorGUILayout.PropertyField(_occludeParent, new GUIContent("Occlude Parent",
							"When enabled, this child body will occlude its parent during tracking. " +
							"Use this when assembling parts that cover portions of the parent."));
					}

					EditorGUILayout.Space(4);
					EditorGUILayout.PropertyField(_assemblyMode, new GUIContent("Assembly Mode",
						"Enable for parts that will be physically installed during operation. " +
						"The child won't affect parent pose until quality confirms the part is present."));

					if (_assemblyMode.boolValue)
					{
						EditorGUI.indentLevel++;
						EditorGUILayout.PropertyField(_assemblyQualityToConfirm, new GUIContent("Quality to Confirm",
							"Quality threshold (0-1) required to confirm the part is installed."));
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
					"Silhouette: Foreground/background color separation (requires tracking model).\n" +
					"Edge: Render-based edge detection (no model file needed).\n" +
					"Silhouette + Edge: Both combined."));

				EditorGUILayout.PropertyField(_enableDepthTracking, new GUIContent("Enable Depth",
					"Enable depth tracking using RealSense depth camera. " +
					"Requires a depth model in the Tracking Model Asset and RealSense camera mode."));

				bool silhouetteEnabled = _trackingMethod.enumValueIndex == (int)TrackingMethod.Silhouette;
				bool needsModel = silhouetteEnabled || _enableDepthTracking.boolValue;

				// Model settings, asset, and generate button only when silhouette or depth is enabled
				if (needsModel)
				{
					EditorGUILayout.Space(4);

					if (silhouetteEnabled)
					{
						EditorGUILayout.HelpBox(
							"Silhouette data encodes the object's visual profile from multiple viewpoints. " +
							"Pre-generate for faster startup, or leave empty to generate at runtime.",
							MessageType.None);
					}

					DrawModelSettings();
					EditorGUILayout.PropertyField(_trackingModelAsset, new GUIContent("Asset"));

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
				"Which viewing angles to include. Reduces model size for objects only viewed from certain directions."));

			bool hasElevationFilter = presetProp.enumValueIndex != (int)ViewpointPreset.FullSphere;

			if (presetProp.enumValueIndex == (int)ViewpointPreset.Custom)
			{
				EditorGUI.indentLevel++;
				EditorGUILayout.PropertyField(_modelSettings.FindPropertyRelative("minElevation"),
					new GUIContent("Min Elevation", "-90° = below, 0° = side, 90° = above"));
				EditorGUILayout.PropertyField(_modelSettings.FindPropertyRelative("maxElevation"),
					new GUIContent("Max Elevation", "-90° = below, 0° = side, 90° = above"));
				EditorGUI.indentLevel--;
			}
			else if (presetProp.enumValueIndex == (int)ViewpointPreset.UpperHemisphere)
			{
				EditorGUILayout.HelpBox("Top + side views (-10° to 90°). Cuts bottom-up views.", MessageType.None);
			}
			else if (presetProp.enumValueIndex == (int)ViewpointPreset.SideRing)
			{
				EditorGUILayout.HelpBox("Horizontal views (-20° to 30°). For objects at camera height.", MessageType.None);
			}

			// Horizontal filter
			EditorGUILayout.Space(2);
			var horizEnabledProp = _modelSettings.FindPropertyRelative("enableHorizontalFilter");
			EditorGUILayout.PropertyField(horizEnabledProp, new GUIContent("Horizontal Filter",
				"Limit viewpoints to a horizontal (azimuth) range around a forward direction. Useful for wide/flat objects."));

			if (horizEnabledProp.boolValue)
			{
				EditorGUI.indentLevel++;
				EditorGUILayout.PropertyField(_modelSettings.FindPropertyRelative("minHorizontal"),
					new GUIContent("Min Angle", "Minimum azimuth in degrees (-180 = behind, 0 = forward)"));
				EditorGUILayout.PropertyField(_modelSettings.FindPropertyRelative("maxHorizontal"),
					new GUIContent("Max Angle", "Maximum azimuth in degrees (0 = forward, 180 = behind)"));
				EditorGUILayout.PropertyField(_modelSettings.FindPropertyRelative("forwardAxis"),
					new GUIContent("Forward Axis", "Reference direction for azimuth measurement."));

				// Check if forward is parallel to up
				var fwdAxisEnum = (ForwardAxis)_modelSettings.FindPropertyRelative("forwardAxis").enumValueIndex;
				var upAxisEnum = (ForwardAxis)_modelSettings.FindPropertyRelative("upAxis").enumValueIndex;
				Vector3 fwdVec = ModelSettings.ForwardAxisToVector(fwdAxisEnum);
				Vector3 upVec = ModelSettings.ForwardAxisToVector(upAxisEnum);
				if (Mathf.Abs(Vector3.Dot(fwdVec, upVec)) > 0.99f)
				{
					EditorGUILayout.HelpBox("Forward axis is parallel to up axis — horizontal filter will be ignored.", MessageType.Warning);
				}
				EditorGUI.indentLevel--;
			}

			// Up Axis — only relevant when a viewpoint filter is active
			if (hasElevationFilter || horizEnabledProp.boolValue)
			{
				EditorGUILayout.Space(2);
				EditorGUILayout.PropertyField(_modelSettings.FindPropertyRelative("upAxis"),
					new GUIContent("Up Axis", "Which object-local axis points 'up'. Elevation angles are measured relative to this. Change for CAD models with Z-up."));
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
			string info = $"r={sphereRadius:F3}m  {trackedBody.GetViewpointCount()} views  {trackedBody.GetPointsPerView()} pts";
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
				EditorGUILayout.PropertyField(_initialPoseSource, new GUIContent("Initialization"));

				if (_initialPoseSource.enumValueIndex == (int)InitialPoseSource.Viewpoint)
				{
					EditorGUILayout.BeginHorizontal();
					EditorGUILayout.PropertyField(_initialViewpoint, new GUIContent("Viewpoint",
						"Transform representing the viewing position. If null, uses main camera."));

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
				}

				EditorGUILayout.Space(4);

				// Start threshold
				EditorGUILayout.PropertyField(_useCustomStartThreshold, new GUIContent("Custom Start Threshold"));

				if (_useCustomStartThreshold.boolValue)
				{
					EditorGUI.indentLevel++;
					EditorGUILayout.PropertyField(_customQualityToStart, new GUIContent("Quality to Start"));
					EditorGUI.indentLevel--;
				}

				// Stop threshold
				EditorGUILayout.PropertyField(_useCustomStopThreshold, new GUIContent("Custom Stop Threshold"));

				if (_useCustomStopThreshold.boolValue)
				{
					EditorGUI.indentLevel++;
					EditorGUILayout.PropertyField(_customQualityToStop, new GUIContent("Quality to Stop"));
					EditorGUI.indentLevel--;
				}

				// Runtime buttons
				if (Application.isPlaying && trackedBody.IsRegistered)
				{
					EditorGUILayout.Space(4);
					EditorGUILayout.BeginHorizontal();

					using (new EditorGUI.DisabledScope(trackedBody.IsTracking))
					{
						if (GUILayout.Button("Force Start"))
							trackedBody.ForceStartTracking();
					}

					using (new EditorGUI.DisabledScope(!trackedBody.IsTracking))
					{
						if (GUILayout.Button("Reset"))
							trackedBody.ResetTracking();
					}

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
							"Object is fixed in the world. Enables AR pose fusion: SLAM anchors the pose after stabilization, " +
							"tracker provides corrections when quality is high."));

					if (_isStationary.boolValue)
					{
						EditorGUI.indentLevel++;
						EditorGUILayout.PropertyField(_smoothTime,
							new GUIContent("Smooth Time",
								"Time in seconds to smooth pose corrections. 0 = instant."));
						EditorGUILayout.HelpBox(
							"Requires AR Foundation for SLAM. Falls back to standard tracking if unavailable.",
							MessageType.Info);
						EditorGUI.indentLevel--;
					}
				}

				EditorGUILayout.PropertyField(_rotationStability);
				EditorGUILayout.PropertyField(_positionStability);

				// Silhouette modality settings
				if (silhouetteEnabled)
				{
					EditorGUILayout.Space(4);
					_foldoutSilhouette = EditorGUILayout.Foldout(_foldoutSilhouette, "Silhouette Tracking", true);
					if (_foldoutSilhouette)
					{
						EditorGUILayout.PropertyField(
							_silhouetteTracking.FindPropertyRelative("_edgeTolerance"),
							new GUIContent("Edge Tolerance",
								"How tolerant the tracker is to blurry or indistinct edges. " +
								"Higher values are more forgiving for thin objects."));
						EditorGUILayout.PropertyField(
							_silhouetteTracking.FindPropertyRelative("_updateResponsiveness"),
							new GUIContent("Update Responsiveness",
								"How aggressively the tracker adjusts pose each frame. " +
								"Lower = cautious, higher = faster convergence but may overshoot."));
						// Min Continuous Distance, Function Length, Histogram Bins,
						// Pyramid Scales, and Standard Deviations hidden from inspector.
						// Still settable via script (SilhouetteTrackingSettings / MultiScaleSettings).
					}
				}

				// Edge modality settings
				if (edgeEnabled)
				{
					DrawEdgeModalityContent();
				}

				// Show depth tracking settings only when depth is enabled
				if (_enableDepthTracking.boolValue)
				{
					EditorGUILayout.Space(4);
					EditorGUILayout.PropertyField(_depthTracking, new GUIContent("Depth Tracking",
						"Advanced tuning for RealSense depth-based tracking. Adjust correspondence matching and occlusion handling."), true);
				}

				EndSectionContent();
			}
		}

		private void DrawEdgeModalityContent()
		{
			EditorGUILayout.Space(4);
			_edgeModality.isExpanded = EditorGUILayout.Foldout(_edgeModality.isExpanded,
				new GUIContent("Edge Tracking",
					"Tuning parameters for render-based edge tracking."), true);

			if (_edgeModality.isExpanded)
			{
				try
				{
					EditorGUILayout.PropertyField(_edgeModality.FindPropertyRelative("_depthEdgeThreshold"));
					EditorGUILayout.PropertyField(_edgeModality.FindPropertyRelative("_searchRadius"));
					EditorGUILayout.PropertyField(_edgeModality.FindPropertyRelative("_minGradient"));
					EditorGUILayout.PropertyField(_edgeModality.FindPropertyRelative("_sampleStep"));
					var useKeyframeProp = _edgeModality.FindPropertyRelative("_useKeyframe");
					EditorGUILayout.PropertyField(useKeyframeProp,
						new GUIContent("Use Keyframe",
							"Reuse edge sites across frames instead of re-rendering every frame. " +
							"Reduces jitter by keeping a stable set of 3D reference points."));
					if (useKeyframeProp.boolValue)
					{
						EditorGUI.indentLevel++;
						EditorGUILayout.PropertyField(_edgeModality.FindPropertyRelative("_keyframeRotationDeg"),
							new GUIContent("Rotation Threshold (deg)",
								"Rotation threshold in degrees to trigger keyframe refresh."));
						EditorGUILayout.PropertyField(_edgeModality.FindPropertyRelative("_keyframeTranslation"),
							new GUIContent("Translation Threshold (m)",
								"Translation threshold in meters to trigger keyframe refresh."));
						EditorGUI.indentLevel--;
					}
					EditorGUILayout.PropertyField(_edgeModality.FindPropertyRelative("_enableCreaseEdges"));
					if (_edgeModality.FindPropertyRelative("_enableCreaseEdges").boolValue)
					{
						EditorGUI.indentLevel++;
						EditorGUILayout.PropertyField(_edgeModality.FindPropertyRelative("_creaseEdgeAngle"));
						EditorGUI.indentLevel--;
					}
					// Search Radius Scales and Standard Deviations hidden from inspector.
					// Still settable via script (EdgeTrackingSettings).
				}
				catch (System.ArgumentException) { }
			}
		}

		private void DrawGPUFeaturesSection()
		{
			_foldoutGPUFeatures = EditorGUILayout.Foldout(_foldoutGPUFeatures, "GPU Features", true, EditorStyles.foldoutHeader);
			if (_foldoutGPUFeatures)
			{
				BeginSectionContent();

				EditorGUILayout.HelpBox(
					"These features use additional GPU resources. Consider disabling on mobile devices.",
					MessageType.None);

				bool isSilhouette = _trackingMethod.enumValueIndex == (int)TrackingMethod.Silhouette;
				if (isSilhouette)
				{
					EditorGUILayout.PropertyField(_enableSilhouetteValidation, new GUIContent("Silhouette Validation",
						"Validates tracking points against rendered silhouette boundaries. Improves accuracy by rejecting false correspondences."));
				}

				EditorGUILayout.PropertyField(_enableTextureTracking, new GUIContent("Texture Tracking",
					"Adds feature-based tracking (ORB keypoints) in addition to silhouette tracking.\n\n" +
					"IMPORTANT: This does NOT use the Unity texture/material. Instead, it detects visual features " +
					"directly from the camera image on the object's visible surfaces.\n\n" +
					"Best for objects with distinctive surface patterns (text, logos, scratches, textures). " +
					"Not useful for uniform/solid-colored objects."));

				// Edge modality handles occlusion automatically via the shared normal renderer
				bool isEdge = _trackingMethod.enumValueIndex == (int)TrackingMethod.Edge;
				if (!isEdge)
				{
					EditorGUILayout.PropertyField(_enableOcclusion, new GUIContent("Occlusion",
						"Enable depth-based occlusion handling for this body's tracking. " +
						"Silhouette points hidden behind other objects (occluders) are ignored.\n\n" +
						"Child bodies with 'Occlude Parent' enabled will automatically occlude this body."));

					// Show occlusion settings only when occlusion is enabled
					if (_enableOcclusion.boolValue)
					{
						EditorGUI.indentLevel++;
						EditorGUILayout.PropertyField(_occlusionSettings, new GUIContent("Settings",
							"Occlusion detection parameters. Adjust threshold for tight assemblies where parts are close together."), true);
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

				// Validation section - only for child bodies
				if (hasParent)
				{
					EditorGUILayout.Space(8);
					EditorGUILayout.PropertyField(_enableValidation, new GUIContent("Enable Validation",
						"When enabled, fires OnBecomeValid/OnBecomeInvalid events based on tracking quality."));

					if (_enableValidation.boolValue)
					{
						EditorGUI.indentLevel++;
						EditorGUILayout.PropertyField(_validationThreshold, new GUIContent("Threshold",
							"Quality threshold (0.5-1.0). Object becomes valid when quality exceeds this value."));
						EditorGUILayout.Space(4);
						EditorGUILayout.PropertyField(_onBecomeValid, new GUIContent("On Become Valid"));
						EditorGUILayout.PropertyField(_onBecomeInvalid, new GUIContent("On Become Invalid"));
						EditorGUI.indentLevel--;
					}
				}

				EndSectionContent();
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
				EditorGUILayout.EnumPopup("Status", trackedBody.TrackingStatus);

				// Primary quality metrics (averaged)
				EditorGUILayout.Slider("Tracking Quality", trackedBody.TrackingQuality, 0f, 1f);
				if (trackedBody.EnableSilhouetteTracking || status.HasRegionModality)
					EditorGUILayout.Slider("Shape Quality", trackedBody.ShapeQuality, 0f, 1f);

				// Silhouette tracking (only show if silhouette tracking is enabled)
				if (trackedBody.EnableSilhouetteTracking || status.HasRegionModality)
				{
					EditorGUILayout.Space(2);
					EditorGUILayout.LabelField("Silhouette Tracking", EditorStyles.miniLabel);
					EditorGUILayout.Slider("  Visibility", trackedBody.Visibility, 0f, 1f);
					EditorGUILayout.LabelField("  Valid/Max Lines", $"{status.n_valid_lines} / {status.n_max_lines}");

					// Raw metrics for debugging
					EditorGUILayout.Space(2);
					EditorGUILayout.LabelField("Raw Metrics", EditorStyles.miniLabel);
					EditorGUILayout.Slider("  Histogram Discrim.", status.histogram_discriminability, 0f, 1f);
					EditorGUILayout.FloatField("  Variance StdDev", status.variance_stddev);
					EditorGUILayout.FloatField("  Mean Variance", status.mean_variance);
				}

				// Depth tracking metrics (only show if depth tracking is enabled)
				if (trackedBody.EnableDepthTracking || status.HasDepthModality)
				{
					EditorGUILayout.Space(2);
					EditorGUILayout.LabelField("Depth Tracking", EditorStyles.miniLabel);
					EditorGUILayout.Slider("  Visibility", trackedBody.DepthVisibility, 0f, 1f);
					EditorGUILayout.LabelField("  Valid/Max Points", $"{status.n_valid_points} / {status.n_max_points}");
					EditorGUILayout.FloatField("  Mean Corr. Distance", status.mean_correspondence_distance);
					EditorGUILayout.FloatField("  Corr. Distance StdDev", status.correspondence_distance_stddev);
				}

				// Edge tracking metrics (only show if edge tracking is enabled)
				if (trackedBody.EnableEdgeTracking || status.HasEdgeModality)
				{
					EditorGUILayout.Space(2);
					EditorGUILayout.LabelField("Edge Tracking", EditorStyles.miniLabel);
					EditorGUILayout.Slider("  Edge Quality", trackedBody.EdgeTrackingQuality, 0f, 1f);
					EditorGUILayout.Slider("  Edge Coverage (valid/total)", trackedBody.EdgeCoverageAverage, 0f, 1f);
					float trackingPct = status.n_total_edge_sites > 0 ? 100f * status.n_tracking_edge_sites / status.n_total_edge_sites : 0f;
					EditorGUILayout.LabelField("  Tracking Coverage", $"{status.n_tracking_edge_sites} / {status.n_total_edge_sites} ({trackingPct:F0}%)");
					EditorGUILayout.Slider("  Projection Error (°)", trackedBody.ProjectionErrorAverage, 0f, 90f);
					EditorGUILayout.FloatField("  Median Residual (px)", status.mean_edge_residual);
				}
			}

			// Force start button (outside disabled scope so it's clickable)
			if (!trackedBody.IsTracking)
			{
				if (GUILayout.Button("Force Start Tracking"))
					trackedBody.ForceStartTracking();
			}

			// Auto-repaint during play mode
			if (Application.isPlaying)
			{
				Repaint();
			}
		}

		private static string FormatBytes(long bytes)
		{
			if (bytes < 1024) return $"{bytes} B";
			if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1} KB";
			return $"{bytes / (1024f * 1024f):F2} MB";
		}
	}
}