using UnityEditor;
using UnityEngine;

namespace IV.FormulaTracker.Editor
{
	[CustomEditor(typeof(TrackingModelAsset))]
	public class TrackingModelAssetEditor : UnityEditor.Editor
	{
		// Avoid accessing serializedObject entirely — it forces Unity to serialize
		// the full object (including massive byte[] fields) into the undo/serialization
		// system, causing freezes on selection and Reset.

		public override void OnInspectorGUI()
		{
			var asset = (TrackingModelAsset)target;

			EditorGUILayout.Space(4);

			// Silhouette Model Status
			EditorGUILayout.LabelField("Silhouette Model", EditorStyles.boldLabel);
			if (asset.HasValidSilhouetteModel)
			{
				EditorGUILayout.HelpBox($"Valid silhouette model: {FormatBytes(asset.SilhouetteDataSize)}", MessageType.Info);
			}
			else
			{
				EditorGUILayout.HelpBox("No silhouette model", MessageType.Warning);
			}

			EditorGUILayout.Space(8);

			// Depth Model Status
			EditorGUILayout.LabelField("Depth Model", EditorStyles.boldLabel);
			if (asset.HasValidDepthModel)
			{
				EditorGUILayout.HelpBox($"Valid depth model: {FormatBytes(asset.DepthDataSize)}", MessageType.Info);
			}
			else
			{
				EditorGUILayout.HelpBox("No depth model (optional - for RealSense/depth cameras)", MessageType.None);
			}

			EditorGUILayout.Space(8);

			// All read-only — use direct property access, never serializedObject
			using (new EditorGUI.DisabledScope(true))
			{
				// Model Settings
				EditorGUILayout.LabelField("Model Settings", EditorStyles.boldLabel);
				var settings = asset.ModelSettings;
				if (settings != null)
				{
					EditorGUI.indentLevel++;
					EditorGUILayout.FloatField("Sphere Radius", settings.sphereRadius);
					EditorGUILayout.IntField("N Divides", settings.nDivides);
					EditorGUILayout.IntField("N Points", settings.nPoints);
					EditorGUILayout.FloatField("Max Radius Depth Offset", settings.maxRadiusDepthOffset);
					EditorGUILayout.FloatField("Stride Depth Offset", settings.strideDepthOffset);
					EditorGUILayout.IntField("Image Size", settings.imageSize);
					EditorGUILayout.EnumPopup("Viewpoint Preset", settings.viewpointPreset);
					EditorGUILayout.FloatField("Min Elevation", settings.minElevation);
					EditorGUILayout.FloatField("Max Elevation", settings.maxElevation);
					if (settings.enableHorizontalFilter)
					{
						EditorGUILayout.Toggle("Horizontal Filter", settings.enableHorizontalFilter);
						EditorGUILayout.FloatField("Min Horizontal", settings.minHorizontal);
						EditorGUILayout.FloatField("Max Horizontal", settings.maxHorizontal);
						EditorGUILayout.EnumPopup("Forward Axis", settings.forwardAxis);
					}
					EditorGUI.indentLevel--;
				}

				EditorGUILayout.Space(4);

				// Silhouette info
				EditorGUILayout.LabelField("Silhouette Model Info", EditorStyles.boldLabel);
				EditorGUI.indentLevel++;
				EditorGUILayout.IntField("Data Size (bytes)", asset.SilhouetteDataSize);
				EditorGUILayout.TextField("Generated", asset.SilhouetteGeneratedDate ?? "—");
				EditorGUILayout.FloatField("Scale", asset.DisplayScale);
				EditorGUI.indentLevel--;

				EditorGUILayout.Space(4);

				// Depth info
				EditorGUILayout.LabelField("Depth Model Info", EditorStyles.boldLabel);
				EditorGUI.indentLevel++;
				EditorGUILayout.IntField("Data Size (bytes)", asset.DepthDataSize);
				EditorGUILayout.TextField("Generated", asset.DepthGeneratedDate ?? "—");
				EditorGUI.indentLevel--;

				// Source mesh hash
				if (!string.IsNullOrEmpty(asset.SourceMeshHash))
				{
					EditorGUILayout.Space(4);
					EditorGUILayout.TextField("Source Mesh Hash", asset.SourceMeshHash);
				}
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
