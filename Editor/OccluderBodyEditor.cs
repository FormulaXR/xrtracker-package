using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace IV.FormulaTracker.Editor
{
	[CustomEditor(typeof(OccluderBody))]
	[CanEditMultipleObjects]
	public class OccluderBodyEditor : UnityEditor.Editor
	{
		private SerializedProperty _bodyId;
		private SerializedProperty _meshFilters;
		private SerializedProperty _geometryUnitInMeter;

		private static GUIContent _addChildMeshesIcon;

		// Cached gizmo data (computed once on selection, not every repaint)
		private Bounds _cachedBounds;
		private bool _gizmoCacheValid;

		private void OnEnable()
		{
			_gizmoCacheValid = false;
			_bodyId = serializedObject.FindProperty("_bodyId");
			_meshFilters = serializedObject.FindProperty("_meshFilters");
			_geometryUnitInMeter = serializedObject.FindProperty("_geometryUnitInMeter");

			_addChildMeshesIcon ??= new GUIContent(
				EditorGUIUtility.IconContent("d_Toolbar Plus").image,
				"Add all child MeshFilters to the list");
		}

		public override void OnInspectorGUI()
		{
			serializedObject.Update();

			var occluderBody = (OccluderBody)target;

			// Script field (read-only)
			using (new EditorGUI.DisabledScope(true))
			{
				EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));
			}

			EditorGUILayout.PropertyField(_bodyId, new GUIContent("Body ID"));

			EditorGUILayout.Space(4);

			// Mesh section
			EditorGUILayout.LabelField("Mesh", EditorStyles.boldLabel);
			EditorGUI.indentLevel++;

			// Mesh Filters with inline add button
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.PropertyField(_meshFilters, true);

			if (GUILayout.Button(_addChildMeshesIcon, GUILayout.Width(24), GUILayout.Height(EditorGUIUtility.singleLineHeight)))
			{
				foreach (var t in targets)
				{
					var body = (OccluderBody)t;
					var method = body.GetType().GetMethod("AddChildMeshes", BindingFlags.Instance | BindingFlags.NonPublic);
					method?.Invoke(body, null);
					EditorUtility.SetDirty(body);
				}
			}
			EditorGUILayout.EndHorizontal();

			EditorGUILayout.PropertyField(_geometryUnitInMeter, new GUIContent("Geometry Unit (m)",
				"Scale factor for geometry. Use 0.001 for meshes in mm, 0.01 for cm, 1.0 for meters."));

			EditorGUI.indentLevel--;

			// Model info
			if (!_gizmoCacheValid)
			{
				_cachedBounds = occluderBody.ComputeLocalBounds();
				_gizmoCacheValid = true;
			}
			if (_cachedBounds.size != Vector3.zero)
			{
				EditorGUILayout.Space(4);
				EditorGUILayout.LabelField("Model Info", EditorStyles.boldLabel);
				EditorGUI.indentLevel++;
				using (new EditorGUI.DisabledScope(true))
				{
					EditorGUILayout.Vector3Field("Dimensions", _cachedBounds.size);
				}
				EditorGUI.indentLevel--;
			}

			// Runtime status
			if (Application.isPlaying)
			{
				EditorGUILayout.Space(4);
				EditorGUILayout.LabelField("Runtime Status", EditorStyles.boldLabel);
				using (new EditorGUI.DisabledScope(true))
				{
					EditorGUILayout.Toggle("Registered", occluderBody.IsRegistered);
				}
			}

			if (serializedObject.ApplyModifiedProperties())
				_gizmoCacheValid = false;
		}

		private void OnSceneGUI()
		{
			var occluderBody = (OccluderBody)target;

			if (!_gizmoCacheValid)
			{
				_cachedBounds = occluderBody.ComputeLocalBounds();
				_gizmoCacheValid = true;
			}

			if (_cachedBounds.size == Vector3.zero)
				return;

			var t = occluderBody.transform;
			Vector3 center = t.TransformPoint(_cachedBounds.center);

			// Draw bounds box
			Handles.color = new Color(1f, 0.5f, 0f, 0.7f); // Orange for occluder bodies
			Matrix4x4 matrix = Matrix4x4.TRS(center, t.rotation, Vector3.one);
			Vector3 scaledSize = Vector3.Scale(_cachedBounds.size, t.lossyScale);

			using (new Handles.DrawingScope(matrix))
			{
				DrawWireCube(Vector3.zero, scaledSize);
			}

			// Draw label
			Handles.color = Color.white;
			Handles.Label(center + t.up * (scaledSize.y * 0.5f + 0.02f), "Occluder", EditorStyles.whiteBoldLabel);
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
	}
}
