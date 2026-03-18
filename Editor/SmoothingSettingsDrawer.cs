using UnityEditor;
using UnityEngine;

namespace IV.FormulaTracker.Editor
{
	/// <summary>
	/// Custom property drawer for SmoothingSettings that handles conditional visibility.
	/// </summary>
	[CustomPropertyDrawer(typeof(SmoothingSettings))]
	public class SmoothingSettingsDrawer : PropertyDrawer
	{
		public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
		{
			if (!property.isExpanded)
				return EditorGUIUtility.singleLineHeight;

			float lineHeight = EditorGUIUtility.singleLineHeight;
			float spacing = EditorGUIUtility.standardVerticalSpacing;
			float lineWithSpacing = lineHeight + spacing;

			float height = lineHeight; // Foldout

			// Outlier Rejection section
			height += lineWithSpacing; // _enableOutlierRejection
			if (property.FindPropertyRelative("_enableOutlierRejection").boolValue)
			{
				height += lineWithSpacing * 2; // _maxPositionDelta, _maxRotationDelta
			}

			// Interpolation header (space before + label)
			height += spacing * 2 + lineHeight;

			// Mode
			height += lineWithSpacing;

			// Mode-specific properties
			var mode = (SmoothingMode)property.FindPropertyRelative("_mode").enumValueIndex;
			switch (mode)
			{
				case SmoothingMode.Lerp:
					height += lineWithSpacing * 2; // position/rotation smooth time
					break;
				case SmoothingMode.Kalman:
					height += lineWithSpacing * 4; // pos/vel process noise, measurement noise, rotation smoothness
					break;
			}

			return height;
		}

		public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
		{
			EditorGUI.BeginProperty(position, label, property);

			float lineHeight = EditorGUIUtility.singleLineHeight;
			float spacing = EditorGUIUtility.standardVerticalSpacing;

			var rect = new Rect(position.x, position.y, position.width, lineHeight);

			// Foldout
			property.isExpanded = EditorGUI.Foldout(rect, property.isExpanded, label, true);
			rect.y += lineHeight + spacing;

			if (property.isExpanded)
			{
				EditorGUI.indentLevel++;

				// Outlier Rejection
				var enableOutlierRejection = property.FindPropertyRelative("_enableOutlierRejection");
				EditorGUI.PropertyField(rect, enableOutlierRejection, new GUIContent("Enable Outlier Rejection", "Clamp pose changes that exceed thresholds."));
				rect.y += lineHeight + spacing;

				if (enableOutlierRejection.boolValue)
				{
					EditorGUI.PropertyField(rect, property.FindPropertyRelative("_maxPositionDelta"), new GUIContent("Max Position Delta", "Maximum allowed position change per second in meters."));
					rect.y += lineHeight + spacing;

					EditorGUI.PropertyField(rect, property.FindPropertyRelative("_maxRotationDelta"), new GUIContent("Max Rotation Delta", "Maximum allowed rotation change per second in degrees."));
					rect.y += lineHeight + spacing;
				}

				// Interpolation Header (with extra spacing before)
				rect.y += spacing;
				EditorGUI.LabelField(rect, "Interpolation", EditorStyles.boldLabel);
				rect.y += lineHeight + spacing;

				// Mode
				var modeProperty = property.FindPropertyRelative("_mode");
				EditorGUI.PropertyField(rect, modeProperty, new GUIContent("Mode", "Smoothing algorithm: None = raw pose, Lerp = simple interpolation, Kalman = adaptive filtering"));
				rect.y += lineHeight + spacing;

				var mode = (SmoothingMode)modeProperty.enumValueIndex;

				switch (mode)
				{
					case SmoothingMode.Lerp:
						EditorGUI.PropertyField(rect, property.FindPropertyRelative("_positionSmoothTime"), new GUIContent("Position Smooth Time", "Time constant for position smoothing. Lower = faster response, more jitter."));
						rect.y += lineHeight + spacing;

						EditorGUI.PropertyField(rect, property.FindPropertyRelative("_rotationSmoothTime"), new GUIContent("Rotation Smooth Time", "Time constant for rotation smoothing. Lower = faster response, more jitter."));
						rect.y += lineHeight + spacing;
						break;

					case SmoothingMode.Kalman:
						EditorGUI.PropertyField(rect, property.FindPropertyRelative("_posProcessNoise"), new GUIContent("Position Process Noise", "Higher = faster adaptation to movement, more jitter."));
						rect.y += lineHeight + spacing;

						EditorGUI.PropertyField(rect, property.FindPropertyRelative("_velProcessNoise"), new GUIContent("Velocity Process Noise", "Higher = faster velocity changes, less smooth motion."));
						rect.y += lineHeight + spacing;

						EditorGUI.PropertyField(rect, property.FindPropertyRelative("_measurementNoise"), new GUIContent("Measurement Noise", "Higher = trust predictions more, smoother but more lag."));
						rect.y += lineHeight + spacing;

						EditorGUI.PropertyField(rect, property.FindPropertyRelative("_rotationSmoothness"), new GUIContent("Rotation Smoothness", "Higher = smoother rotation, more lag."));
						rect.y += lineHeight + spacing;
						break;
				}

				EditorGUI.indentLevel--;
			}

			EditorGUI.EndProperty();
		}
	}
}
