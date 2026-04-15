using IV.FormulaTracker.Validation;
using UnityEditor;
using UnityEngine;

namespace IV.FormulaTracker.Editor
{
	[CustomEditor(typeof(ValidationZone))]
	[CanEditMultipleObjects]
	public class ValidationZoneEditor : UnityEditor.Editor
	{
		// Serialized properties
		private SerializedProperty _zoneId;
		private SerializedProperty _shape;
		private SerializedProperty _dimensionX;
		private SerializedProperty _dimensionY;
		private SerializedProperty _dimensionZ;
		private SerializedProperty _trackingQualityThreshold;
		private SerializedProperty _validatorType;
		private SerializedProperty _templateTexture;
		private SerializedProperty _passThreshold;
		private SerializedProperty _useAlphaMask;
		private SerializedProperty _histogramMethod;
		private SerializedProperty _useHueOnly;
		private SerializedProperty _numBins;
		private SerializedProperty _autoValidate;
		private SerializedProperty _autoValidateInterval;
		private SerializedProperty _onValidationComplete;
		private SerializedProperty _onValidationPassed;
		private SerializedProperty _onValidationFailed;

		// Foldout states
		private static bool _foldoutDimensions = true;
		private static bool _foldoutValidation = true;
		private static bool _foldoutAutoValidation = false;
		private static bool _foldoutEvents = false;

		private void OnEnable()
		{
			_zoneId = serializedObject.FindProperty("_zoneId");
			_shape = serializedObject.FindProperty("_shape");
			_dimensionX = serializedObject.FindProperty("_dimensionX");
			_dimensionY = serializedObject.FindProperty("_dimensionY");
			_dimensionZ = serializedObject.FindProperty("_dimensionZ");
			_trackingQualityThreshold = serializedObject.FindProperty("_trackingQualityThreshold");
			_validatorType = serializedObject.FindProperty("_validatorType");
			_templateTexture = serializedObject.FindProperty("_templateTexture");
			_passThreshold = serializedObject.FindProperty("_passThreshold");
			_useAlphaMask = serializedObject.FindProperty("_useAlphaMask");
			_histogramMethod = serializedObject.FindProperty("_histogramMethod");
			_useHueOnly = serializedObject.FindProperty("_useHueOnly");
			_numBins = serializedObject.FindProperty("_numBins");
			_autoValidate = serializedObject.FindProperty("_autoValidate");
			_autoValidateInterval = serializedObject.FindProperty("_autoValidateInterval");
			_onValidationComplete = serializedObject.FindProperty("_onValidationComplete");
			_onValidationPassed = serializedObject.FindProperty("_onValidationPassed");
			_onValidationFailed = serializedObject.FindProperty("_onValidationFailed");
		}

		public override void OnInspectorGUI()
		{
			serializedObject.Update();

			var validationZone = (ValidationZone)target;

			// Script field (read-only)
			using (new EditorGUI.DisabledScope(true))
			{
				EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));
			}

			// Zone ID
			EditorGUILayout.PropertyField(_zoneId, new GUIContent("Zone ID", "Unique identifier for this validation zone"));

			EditorGUILayout.Space(4);

			// Shape
			EditorGUILayout.PropertyField(_shape, new GUIContent("Shape", "Shape of the validation zone"));

			var currentShape = (FTZoneShape)_shape.enumValueIndex;

			// Dimensions section
			DrawDimensionsSection(currentShape);

			// Validation section
			DrawValidationSection();

			// Auto Validation section
			DrawAutoValidationSection();

			// Events section
			DrawEventsSection();

			// Runtime status
			DrawRuntimeStatusSection(validationZone);

			serializedObject.ApplyModifiedProperties();
		}

		private void DrawDimensionsSection(FTZoneShape shape)
		{
			_foldoutDimensions = EditorGUILayout.Foldout(_foldoutDimensions, "Dimensions", true, EditorStyles.foldoutHeader);
			if (_foldoutDimensions)
			{
				EditorGUI.indentLevel++;

				// Dimension X label depends on shape
				string dimXLabel;
				string dimXTooltip;
				switch (shape)
				{
					case FTZoneShape.Cylinder:
						dimXLabel = "Radius";
						dimXTooltip = "Radius of the cylinder (meters)";
						break;
					case FTZoneShape.Box:
					case FTZoneShape.Quad:
					default:
						dimXLabel = "Width";
						dimXTooltip = "Width of the zone (meters)";
						break;
				}
				EditorGUILayout.PropertyField(_dimensionX, new GUIContent(dimXLabel, dimXTooltip));

				// Dimension Y is always height
				EditorGUILayout.PropertyField(_dimensionY, new GUIContent("Height", "Height of the zone (meters)"));

				// Dimension Z only shown for Box
				if (shape == FTZoneShape.Box)
				{
					EditorGUILayout.PropertyField(_dimensionZ, new GUIContent("Depth", "Depth of the box (meters)"));
				}

				EditorGUILayout.Space(4);
				EditorGUILayout.PropertyField(_trackingQualityThreshold, new GUIContent("Quality Threshold",
					"Minimum tracking quality (0-1) required to run validation"));

				EditorGUI.indentLevel--;
				EditorGUILayout.Space(2);
			}
		}

		private void DrawValidationSection()
		{
			_foldoutValidation = EditorGUILayout.Foldout(_foldoutValidation, "Validation", true, EditorStyles.foldoutHeader);
			if (_foldoutValidation)
			{
				EditorGUI.indentLevel++;

				EditorGUILayout.PropertyField(_validatorType, new GUIContent("Validator Type",
					"Type of validator to use for this zone"));

				EditorGUILayout.PropertyField(_templateTexture, new GUIContent("Template Texture",
					"Template image for validation (RGBA format)"));

				EditorGUILayout.PropertyField(_passThreshold, new GUIContent("Pass Threshold",
					"Confidence threshold to pass validation (0-1)"));

				var validatorType = (ValidatorType)_validatorType.enumValueIndex;

				EditorGUILayout.Space(4);

				if (validatorType == ValidatorType.Template)
				{
					// Template validator settings
					EditorGUILayout.LabelField("Template Settings", EditorStyles.boldLabel);
					EditorGUI.indentLevel++;
					EditorGUILayout.PropertyField(_useAlphaMask, new GUIContent("Use Alpha Mask",
						"Use alpha channel as comparison mask (transparent regions ignored)"));
					EditorGUI.indentLevel--;
				}
				else
				{
					// Histogram validator settings
					EditorGUILayout.LabelField("Histogram Settings", EditorStyles.boldLabel);
					EditorGUI.indentLevel++;
					EditorGUILayout.PropertyField(_histogramMethod, new GUIContent("Compare Method",
						"Correlation: General purpose, measures linear correlation (recommended)\n" +
						"Chi-Square: Strict matching, penalizes large differences heavily\n" +
						"Intersection: Measures histogram overlap, good for partial matches\n" +
						"Bhattacharyya: Statistical distance, robust to minor color variations"));
					EditorGUILayout.PropertyField(_useHueOnly, new GUIContent("Use Hue Only",
						"Use only Hue channel, ignoring saturation and brightness. More robust to lighting changes but less discriminative for colors with similar hue."));
					EditorGUILayout.PropertyField(_numBins, new GUIContent("Histogram Bins",
						"Number of histogram bins per channel. Lower = more tolerant to color variations, Higher = more precise matching. Default 32 is a good balance."));
					EditorGUI.indentLevel--;
				}

				EditorGUI.indentLevel--;
				EditorGUILayout.Space(2);
			}
		}

		private void DrawAutoValidationSection()
		{
			_foldoutAutoValidation = EditorGUILayout.Foldout(_foldoutAutoValidation, "Auto Validation", true, EditorStyles.foldoutHeader);
			if (_foldoutAutoValidation)
			{
				EditorGUI.indentLevel++;

				EditorGUILayout.PropertyField(_autoValidate, new GUIContent("Auto Validate",
					"Automatically run validation each frame when tracking quality is sufficient"));

				// Only show interval when auto-validate is enabled
				if (_autoValidate.boolValue)
				{
					EditorGUI.indentLevel++;
					EditorGUILayout.PropertyField(_autoValidateInterval, new GUIContent("Interval",
						"Minimum interval between auto-validations (seconds)"));
					EditorGUI.indentLevel--;
				}

				EditorGUI.indentLevel--;
				EditorGUILayout.Space(2);
			}
		}

		private void DrawEventsSection()
		{
			_foldoutEvents = EditorGUILayout.Foldout(_foldoutEvents, "Events", true, EditorStyles.foldoutHeader);
			if (_foldoutEvents)
			{
				EditorGUI.indentLevel++;

				EditorGUILayout.PropertyField(_onValidationComplete, new GUIContent("On Validation Complete"));
				EditorGUILayout.PropertyField(_onValidationPassed, new GUIContent("On Validation Passed"));
				EditorGUILayout.PropertyField(_onValidationFailed, new GUIContent("On Validation Failed"));

				EditorGUI.indentLevel--;
				EditorGUILayout.Space(2);
			}
		}

		private void DrawRuntimeStatusSection(ValidationZone validationZone)
		{
			if (!Application.isPlaying)
				return;

			EditorGUILayout.Space(4);
			EditorGUILayout.LabelField("Runtime Status", EditorStyles.boldLabel);

			using (new EditorGUI.DisabledScope(true))
			{
				EditorGUILayout.Toggle("Registered", validationZone.IsRegistered);

				var lastResult = validationZone.LastValidationResult;
				if (lastResult != null)
				{
					EditorGUILayout.EnumPopup("Readiness", lastResult.Readiness);
					EditorGUILayout.Slider("Visibility", lastResult.VisibilityScore, 0f, 1f);
					EditorGUILayout.Toggle("Validation Attempted", lastResult.ValidationAttempted);

					if (lastResult.ValidationAttempted)
					{
						EditorGUILayout.Toggle("Passed", lastResult.Passed);

						if (lastResult.ValidatorResults != null)
						{
							foreach (var vr in lastResult.ValidatorResults)
							{
								EditorGUILayout.Space(2);
								EditorGUILayout.LabelField($"  {vr.ValidatorId}", EditorStyles.miniLabel);
								EditorGUILayout.Slider($"    Confidence", vr.Confidence, 0f, 1f);
							}
						}
					}
				}
			}

			// Auto-repaint during play mode
			Repaint();
		}

		#region Scene View Handles

		// Handle size for edge handles
		private const float kHandleSize = 0.04f;
		private static readonly Color kHandleColor = new Color(0f, 1f, 0.5f, 1f);
		private static readonly Color kHandleColorHover = new Color(0.5f, 1f, 0.8f, 1f);

		private void OnSceneGUI()
		{
			var zone = (ValidationZone)target;
			if (zone == null) return;

			var shape = zone.Shape;
			var t = zone.transform;

			// Draw wire shape
			Handles.color = kHandleColor;
			Handles.matrix = t.localToWorldMatrix;

			var dims = zone.Dimensions;

			switch (shape)
			{
				case FTZoneShape.Quad:
					DrawQuadHandles(zone, dims);
					break;
				case FTZoneShape.Box:
					DrawBoxHandles(zone, dims);
					break;
				case FTZoneShape.Cylinder:
					DrawCylinderHandles(zone, dims);
					break;
			}

			Handles.matrix = Matrix4x4.identity;
		}

		private void DrawQuadHandles(ValidationZone zone, Vector3 dims)
		{
			var t = zone.transform;
			float halfX = dims.x / 2f;
			float halfY = dims.y / 2f;

			// Edge handle positions in local space
			// +X edge (right in local space)
			Vector3 posXHandle = new Vector3(halfX, 0, 0);
			// -X edge (left in local space)
			Vector3 negXHandle = new Vector3(-halfX, 0, 0);
			// +Y edge (top in local space)
			Vector3 posYHandle = new Vector3(0, halfY, 0);
			// -Y edge (bottom in local space)
			Vector3 negYHandle = new Vector3(0, -halfY, 0);

			float handleSize = HandleUtility.GetHandleSize(t.position) * kHandleSize;

			EditorGUI.BeginChangeCheck();

			// +X handle (drag along local X)
			Handles.color = new Color(1f, 0.4f, 0.4f, 1f); // Red for X
			Vector3 newPosX = Handles.Slider(posXHandle, Vector3.right, handleSize, Handles.DotHandleCap, 0f);
			if (newPosX != posXHandle)
			{
				float delta = newPosX.x - posXHandle.x;
				ApplyEdgeDelta(zone, t, Vector3.right, delta, true);
			}

			// -X handle
			Vector3 newNegX = Handles.Slider(negXHandle, Vector3.left, handleSize, Handles.DotHandleCap, 0f);
			if (newNegX != negXHandle)
			{
				float delta = negXHandle.x - newNegX.x;
				ApplyEdgeDelta(zone, t, Vector3.left, delta, true);
			}

			// +Y handle (drag along local Y)
			Handles.color = new Color(0.4f, 1f, 0.4f, 1f); // Green for Y
			Vector3 newPosY = Handles.Slider(posYHandle, Vector3.up, handleSize, Handles.DotHandleCap, 0f);
			if (newPosY != posYHandle)
			{
				float delta = newPosY.y - posYHandle.y;
				ApplyEdgeDelta(zone, t, Vector3.up, delta, false);
			}

			// -Y handle
			Vector3 newNegY = Handles.Slider(negYHandle, Vector3.down, handleSize, Handles.DotHandleCap, 0f);
			if (newNegY != negYHandle)
			{
				float delta = negYHandle.y - newNegY.y;
				ApplyEdgeDelta(zone, t, Vector3.down, delta, false);
			}

			if (EditorGUI.EndChangeCheck())
			{
				Undo.RecordObject(zone, "Resize Validation Zone");
				Undo.RecordObject(t, "Move Validation Zone");
			}
		}

		private void DrawBoxHandles(ValidationZone zone, Vector3 dims)
		{
			var t = zone.transform;
			float halfX = dims.x / 2f;
			float halfY = dims.y / 2f;
			float halfZ = dims.z / 2f;

			float handleSize = HandleUtility.GetHandleSize(t.position) * kHandleSize;

			EditorGUI.BeginChangeCheck();

			// X axis handles (red)
			Handles.color = new Color(1f, 0.4f, 0.4f, 1f);
			Vector3 posXHandle = new Vector3(halfX, 0, 0);
			Vector3 negXHandle = new Vector3(-halfX, 0, 0);

			Vector3 newPosX = Handles.Slider(posXHandle, Vector3.right, handleSize, Handles.DotHandleCap, 0f);
			if (newPosX != posXHandle)
			{
				float delta = newPosX.x - posXHandle.x;
				ApplyEdgeDeltaBox(zone, t, 0, delta, true);
			}

			Vector3 newNegX = Handles.Slider(negXHandle, Vector3.left, handleSize, Handles.DotHandleCap, 0f);
			if (newNegX != negXHandle)
			{
				float delta = negXHandle.x - newNegX.x;
				ApplyEdgeDeltaBox(zone, t, 0, delta, false);
			}

			// Y axis handles (green)
			Handles.color = new Color(0.4f, 1f, 0.4f, 1f);
			Vector3 posYHandle = new Vector3(0, halfY, 0);
			Vector3 negYHandle = new Vector3(0, -halfY, 0);

			Vector3 newPosY = Handles.Slider(posYHandle, Vector3.up, handleSize, Handles.DotHandleCap, 0f);
			if (newPosY != posYHandle)
			{
				float delta = newPosY.y - posYHandle.y;
				ApplyEdgeDeltaBox(zone, t, 1, delta, true);
			}

			Vector3 newNegY = Handles.Slider(negYHandle, Vector3.down, handleSize, Handles.DotHandleCap, 0f);
			if (newNegY != negYHandle)
			{
				float delta = negYHandle.y - newNegY.y;
				ApplyEdgeDeltaBox(zone, t, 1, delta, false);
			}

			// Z axis handles (blue)
			Handles.color = new Color(0.4f, 0.4f, 1f, 1f);
			Vector3 posZHandle = new Vector3(0, 0, halfZ);
			Vector3 negZHandle = new Vector3(0, 0, -halfZ);

			Vector3 newPosZ = Handles.Slider(posZHandle, Vector3.forward, handleSize, Handles.DotHandleCap, 0f);
			if (newPosZ != posZHandle)
			{
				float delta = newPosZ.z - posZHandle.z;
				ApplyEdgeDeltaBox(zone, t, 2, delta, true);
			}

			Vector3 newNegZ = Handles.Slider(negZHandle, Vector3.back, handleSize, Handles.DotHandleCap, 0f);
			if (newNegZ != negZHandle)
			{
				float delta = negZHandle.z - newNegZ.z;
				ApplyEdgeDeltaBox(zone, t, 2, delta, false);
			}

			if (EditorGUI.EndChangeCheck())
			{
				Undo.RecordObject(zone, "Resize Validation Zone");
				Undo.RecordObject(t, "Move Validation Zone");
			}
		}

		private void DrawCylinderHandles(ValidationZone zone, Vector3 dims)
		{
			var t = zone.transform;
			float radius = dims.x;
			float halfHeight = dims.y / 2f;

			float handleSize = HandleUtility.GetHandleSize(t.position) * kHandleSize;

			EditorGUI.BeginChangeCheck();

			// Height handles (top and bottom along Y)
			Handles.color = new Color(0.4f, 1f, 0.4f, 1f);
			Vector3 topHandle = new Vector3(0, halfHeight, 0);
			Vector3 botHandle = new Vector3(0, -halfHeight, 0);

			Vector3 newTop = Handles.Slider(topHandle, Vector3.up, handleSize, Handles.DotHandleCap, 0f);
			if (newTop != topHandle)
			{
				float delta = newTop.y - topHandle.y;
				ApplyEdgeDelta(zone, t, Vector3.up, delta, false);
			}

			Vector3 newBot = Handles.Slider(botHandle, Vector3.down, handleSize, Handles.DotHandleCap, 0f);
			if (newBot != botHandle)
			{
				float delta = botHandle.y - newBot.y;
				ApplyEdgeDelta(zone, t, Vector3.down, delta, false);
			}

			// Radius handles (at 4 cardinal directions on XZ plane)
			Handles.color = new Color(1f, 0.4f, 0.4f, 1f);

			// +X radius
			Vector3 posXRadius = new Vector3(radius, 0, 0);
			Vector3 newPosXR = Handles.Slider(posXRadius, Vector3.right, handleSize, Handles.DotHandleCap, 0f);
			if (newPosXR != posXRadius)
			{
				float delta = newPosXR.x - posXRadius.x;
				ApplyCylinderRadiusDelta(zone, delta);
			}

			// -X radius
			Vector3 negXRadius = new Vector3(-radius, 0, 0);
			Vector3 newNegXR = Handles.Slider(negXRadius, Vector3.left, handleSize, Handles.DotHandleCap, 0f);
			if (newNegXR != negXRadius)
			{
				float delta = negXRadius.x - newNegXR.x;
				ApplyCylinderRadiusDelta(zone, delta);
			}

			// +Z radius
			Handles.color = new Color(0.4f, 0.4f, 1f, 1f);
			Vector3 posZRadius = new Vector3(0, 0, radius);
			Vector3 newPosZR = Handles.Slider(posZRadius, Vector3.forward, handleSize, Handles.DotHandleCap, 0f);
			if (newPosZR != posZRadius)
			{
				float delta = newPosZR.z - posZRadius.z;
				ApplyCylinderRadiusDelta(zone, delta);
			}

			// -Z radius
			Vector3 negZRadius = new Vector3(0, 0, -radius);
			Vector3 newNegZR = Handles.Slider(negZRadius, Vector3.back, handleSize, Handles.DotHandleCap, 0f);
			if (newNegZR != negZRadius)
			{
				float delta = negZRadius.z - newNegZR.z;
				ApplyCylinderRadiusDelta(zone, delta);
			}

			if (EditorGUI.EndChangeCheck())
			{
				Undo.RecordObject(zone, "Resize Validation Zone");
				Undo.RecordObject(t, "Move Validation Zone");
			}
		}

		/// <summary>
		/// Apply edge delta for Quad shape.
		/// When dragging an edge, the opposite edge stays fixed.
		/// </summary>
		private void ApplyEdgeDelta(ValidationZone zone, Transform t, Vector3 localDir, float delta, bool isXAxis)
		{
			Undo.RecordObject(zone, "Resize Validation Zone");
			Undo.RecordObject(t, "Move Validation Zone");

			var dims = zone.Dimensions;

			if (isXAxis)
			{
				// Adjust X dimension
				dims.x = Mathf.Max(0.001f, dims.x + delta);
			}
			else
			{
				// Adjust Y dimension
				dims.y = Mathf.Max(0.001f, dims.y + delta);
			}

			zone.Dimensions = dims;

			// Move position by half delta in the drag direction (in local space)
			Vector3 worldDelta = t.TransformDirection(localDir.normalized * (delta * 0.5f));
			t.position += worldDelta;

			EditorUtility.SetDirty(zone);
			EditorUtility.SetDirty(t);
		}

		/// <summary>
		/// Apply edge delta for Box shape.
		/// axis: 0=X, 1=Y, 2=Z
		/// positive: true if dragging positive face, false if negative face
		/// </summary>
		private void ApplyEdgeDeltaBox(ValidationZone zone, Transform t, int axis, float delta, bool positive)
		{
			Undo.RecordObject(zone, "Resize Validation Zone");
			Undo.RecordObject(t, "Move Validation Zone");

			var dims = zone.Dimensions;

			switch (axis)
			{
				case 0: dims.x = Mathf.Max(0.001f, dims.x + delta); break;
				case 1: dims.y = Mathf.Max(0.001f, dims.y + delta); break;
				case 2: dims.z = Mathf.Max(0.001f, dims.z + delta); break;
			}

			zone.Dimensions = dims;

			// Move position by half delta in the appropriate direction
			Vector3 localDir = axis switch
			{
				0 => positive ? Vector3.right : Vector3.left,
				1 => positive ? Vector3.up : Vector3.down,
				2 => positive ? Vector3.forward : Vector3.back,
				_ => Vector3.zero
			};

			Vector3 worldDelta = t.TransformDirection(localDir * (delta * 0.5f));
			t.position += worldDelta;

			EditorUtility.SetDirty(zone);
			EditorUtility.SetDirty(t);
		}

		/// <summary>
		/// Apply radius delta for Cylinder shape.
		/// Radius changes uniformly (no position adjustment needed).
		/// </summary>
		private void ApplyCylinderRadiusDelta(ValidationZone zone, float delta)
		{
			Undo.RecordObject(zone, "Resize Validation Zone");

			var dims = zone.Dimensions;
			dims.x = Mathf.Max(0.001f, dims.x + delta); // x is radius for cylinder
			zone.Dimensions = dims;

			EditorUtility.SetDirty(zone);
		}

		#endregion
	}
}
