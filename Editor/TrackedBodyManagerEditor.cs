using UnityEditor;
using UnityEngine;

namespace IV.FormulaTracker.Editor
{
	[CustomEditor(typeof(TrackedBodyManager))]
	public class TrackedBodyManagerEditor : UnityEditor.Editor
	{
		private SerializedProperty _enableWorldFrameCoast;
		private SerializedProperty _savedViewRecoveryCapacity;

		private void OnEnable()
		{
			_enableWorldFrameCoast = serializedObject.FindProperty("_enableWorldFrameCoast");
			_savedViewRecoveryCapacity = serializedObject.FindProperty("_savedViewRecoveryCapacity");
		}

		public override void OnInspectorGUI()
		{
			serializedObject.Update();
			EditorGUILayout.PropertyField(_enableWorldFrameCoast);
			EditorGUILayout.PropertyField(_savedViewRecoveryCapacity);
			serializedObject.ApplyModifiedProperties();

			var manager = (TrackedBodyManager)target;

			EditorGUILayout.Space();
			EditorGUILayout.LabelField("Runtime Status", EditorStyles.boldLabel);

			using (new EditorGUI.DisabledGroupScope(true))
			{
				EditorGUILayout.Toggle("AR Pose Fusion Active", manager.IsARPoseFusionActive);
				EditorGUILayout.IntField("Managed Bodies", manager.ManagedBodyCount);
				EditorGUILayout.IntField("Bodies in AR Mode", manager.BodiesInARMode);
			}

			if (Application.isPlaying)
			{
				EditorGUILayout.Space();

				// Show per-body status
				var trackingManager = XRTrackerManager.Instance;
				if (trackingManager != null && trackingManager.TrackedBodies != null)
				{
					EditorGUILayout.LabelField("Per-Body Status", EditorStyles.boldLabel);

					foreach (var body in trackingManager.TrackedBodies)
					{
						if (body == null || !body.IsRegistered)
							continue;

						bool inARMode = manager.IsBodyInARMode(body);

						EditorGUILayout.BeginHorizontal();

						// Body name
						EditorGUILayout.LabelField(body.BodyId, GUILayout.Width(120));

						// Mode indicator
						string modeText = body.IsActiveMode ? (inARMode ? "AR" : "Tracking") : "Rigid";
						var modeStyle = new GUIStyle(EditorStyles.label);
						if (inARMode)
							modeStyle.normal.textColor = Color.yellow;
						else if (body.Confidence == TrackingConfidence.Tracked)
							modeStyle.normal.textColor = Color.green;
						else if (body.Confidence == TrackingConfidence.Degraded)
							modeStyle.normal.textColor = new Color(1f, 0.5f, 0f); // Orange
						else
							modeStyle.normal.textColor = Color.gray;

						EditorGUILayout.LabelField(modeText, modeStyle, GUILayout.Width(70));

						// Quality
						EditorGUILayout.LabelField($"Q: {body.TrackingQuality:F2}", GUILayout.Width(60));

						EditorGUILayout.EndHorizontal();
					}
				}

				EditorGUILayout.Space();

				EditorGUILayout.BeginHorizontal();
				if (GUILayout.Button("Reset All"))
				{
					manager.ResetAll();
				}
				if (GUILayout.Button("Refresh AR"))
				{
					manager.RefreshARAvailability();
				}
				EditorGUILayout.EndHorizontal();

				Repaint();
			}
		}
	}
}
