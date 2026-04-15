using UnityEditor;
using UnityEngine;

namespace IV.FormulaTracker.Editor
{
	[CustomEditor(typeof(TrackedBodyViewpoints))]
	public class TrackedBodyViewpointsEditor : UnityEditor.Editor
	{
		private SerializedProperty _trackedBody;
		private SerializedProperty _viewpoints;

		private void OnEnable()
		{
			_trackedBody = serializedObject.FindProperty("_trackedBody");
			_viewpoints = serializedObject.FindProperty("_viewpoints");
		}

		public override void OnInspectorGUI()
		{
			serializedObject.Update();

			var viewpointsComponent = (TrackedBodyViewpoints)target;

			// Script field (read-only)
			using (new EditorGUI.DisabledScope(true))
			{
				EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));
			}

			EditorGUILayout.PropertyField(_trackedBody);

			EditorGUILayout.Space(8);
			EditorGUILayout.LabelField("Viewpoints", EditorStyles.boldLabel);

			EditorGUILayout.PropertyField(_viewpoints, true);

			// Runtime controls
			if (Application.isPlaying && viewpointsComponent.TrackedBody != null)
			{
				EditorGUILayout.Space(8);
				EditorGUILayout.LabelField("Runtime Controls", EditorStyles.boldLabel);

				for (int i = 0; i < viewpointsComponent.Count; i++)
				{
					var vp = viewpointsComponent.Viewpoints[i];
					if (vp._transform == null)
						continue;

					string label = string.IsNullOrEmpty(vp._name) ? $"Viewpoint {i}" : vp._name;
					if (GUILayout.Button($"Set Pose: {label}"))
					{
						viewpointsComponent.SetInitialPoseFromViewpoint(i);
					}
				}
			}

			serializedObject.ApplyModifiedProperties();
		}
	}
}
