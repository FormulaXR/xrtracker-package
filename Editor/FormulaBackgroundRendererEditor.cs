using UnityEditor;
using UnityEngine;

namespace IV.FormulaTracker.Editor
{
	[CustomEditor(typeof(TrackerBackgroundRenderer))]
	public class FormulaBackgroundRendererEditor : UnityEditor.Editor
	{
		private SerializedProperty _backgroundLayer;
		private SerializedProperty _planeDistance;

		private void OnEnable()
		{
			_backgroundLayer = serializedObject.FindProperty("_backgroundLayer");
			_planeDistance = serializedObject.FindProperty("_planeDistance");
		}

		public override void OnInspectorGUI()
		{
			serializedObject.Update();

			// Script field (read-only)
			using (new EditorGUI.DisabledScope(true))
			{
				EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));
			}

			EditorGUILayout.Space(4);
			EditorGUILayout.LabelField("Background Settings", EditorStyles.boldLabel);

			// Layer dropdown
			DrawLayerField();

			// Plane distance
			EditorGUILayout.PropertyField(_planeDistance, new GUIContent("Plane Distance", "Distance of background plane from camera"));

			// Runtime info
			if (Application.isPlaying)
			{
				DrawRuntimeInfo();
			}

			serializedObject.ApplyModifiedProperties();
		}

		private const string BackgroundLayerName = "FT_Background";

		private void DrawLayerField()
		{
			int currentLayer = _backgroundLayer.intValue;
			string layerName = LayerMask.LayerToName(currentLayer);
			bool layerExists = !string.IsNullOrEmpty(layerName);

			EditorGUILayout.BeginHorizontal();

			// Layer dropdown showing all layers
			int newLayer = EditorGUILayout.LayerField(
				new GUIContent("Background Layer", "Layer for background plane (ensure this layer exists)"),
				currentLayer);

			if (newLayer != currentLayer)
			{
				_backgroundLayer.intValue = newLayer;
			}

			EditorGUILayout.EndHorizontal();

			// Warning if layer doesn't exist or is default
			if (!layerExists)
			{
				EditorGUILayout.HelpBox(
					$"Layer {currentLayer} is not defined. The background may not render correctly.",
					MessageType.Warning);

				// Find first available layer slot and offer to create it
				int availableSlot = FindFirstAvailableLayerSlot();
				if (availableSlot >= 0)
				{
					if (GUILayout.Button($"Create '{BackgroundLayerName}' Layer"))
					{
						if (TrySetLayerName(availableSlot, BackgroundLayerName))
						{
							_backgroundLayer.intValue = availableSlot;
							Debug.Log($"[XRTracker] Created layer '{BackgroundLayerName}' at index {availableSlot}");
						}
					}
				}
				else
				{
					EditorGUILayout.HelpBox("No available layer slots. Please free up a layer in Tags & Layers settings.", MessageType.Error);
					if (GUILayout.Button("Open Tags & Layers Settings"))
					{
						SettingsService.OpenProjectSettings("Project/Tags and Layers");
					}
				}
			}
			else if (currentLayer == 0)
			{
				EditorGUILayout.HelpBox(
					"Using Default layer is not recommended. The background plane may interfere with other objects.",
					MessageType.Info);
			}
		}

		private int FindFirstAvailableLayerSlot()
		{
			// User layers are 8-31 (0-7 are built-in)
			for (int i = 8; i <= 31; i++)
			{
				string testName = LayerMask.LayerToName(i);
				if (string.IsNullOrEmpty(testName))
					return i;
			}
			return -1;
		}

		private bool TrySetLayerName(int layerIndex, string layerName)
		{
			SerializedObject tagManager = new SerializedObject(
				AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/TagManager.asset"));

			SerializedProperty layersProperty = tagManager.FindProperty("layers");

			if (layersProperty != null && layerIndex < layersProperty.arraySize)
			{
				SerializedProperty layerProperty = layersProperty.GetArrayElementAtIndex(layerIndex);

				if (layerProperty != null && string.IsNullOrEmpty(layerProperty.stringValue))
				{
					layerProperty.stringValue = layerName;
					tagManager.ApplyModifiedProperties();
					return true;
				}
			}

			return false;
		}

		private void DrawRuntimeInfo()
		{
			var renderer = (TrackerBackgroundRenderer)target;

			EditorGUILayout.Space(8);
			EditorGUILayout.LabelField("Runtime Status", EditorStyles.boldLabel);

			using (new EditorGUI.DisabledScope(true))
			{
				EditorGUILayout.ObjectField("Background Camera", renderer.BackgroundCamera, typeof(Camera), true);
				EditorGUILayout.ObjectField("Main Camera", renderer.MainCamera, typeof(Camera), true);
			}

			Repaint();
		}
	}
}
