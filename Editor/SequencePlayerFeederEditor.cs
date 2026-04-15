using IV.FormulaTracker.Recording;
using UnityEditor;
using UnityEngine;

namespace IV.FormulaTracker.Editor
{
	[CustomEditor(typeof(SequencePlayerFeeder))]
	public class SequencePlayerFeederEditor : UnityEditor.Editor
	{
		private SerializedProperty _autoPlay;
		private SerializedProperty _loop;
		private SerializedProperty _useCustomStart;
		private SerializedProperty _rangeStart;
		private SerializedProperty _useCustomEnd;
		private SerializedProperty _rangeEnd;

		private void OnEnable()
		{
			_autoPlay = serializedObject.FindProperty("_autoPlay");
			_loop = serializedObject.FindProperty("_loop");
			_useCustomStart = serializedObject.FindProperty("_useCustomStart");
			_rangeStart = serializedObject.FindProperty("_rangeStart");
			_useCustomEnd = serializedObject.FindProperty("_useCustomEnd");
			_rangeEnd = serializedObject.FindProperty("_rangeEnd");
		}

		public override void OnInspectorGUI()
		{
			serializedObject.Update();

			var feeder = (SequencePlayerFeeder)target;

			// Script field
			using (new EditorGUI.DisabledScope(true))
			{
				EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));
			}

			// Playback settings
			EditorGUILayout.Space(4);
			EditorGUILayout.LabelField("Playback", EditorStyles.boldLabel);

			EditorGUILayout.PropertyField(_autoPlay);
			EditorGUILayout.PropertyField(_loop);

			// Frame range
			EditorGUILayout.Space(4);
			EditorGUILayout.LabelField("Frame Range", EditorStyles.boldLabel);

			int maxFrame = feeder.FrameCount > 0 ? feeder.FrameCount - 1 : 0;

			// Custom start
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.PropertyField(_useCustomStart, new GUIContent("Custom Start"));
			using (new EditorGUI.DisabledScope(!_useCustomStart.boolValue))
				EditorGUILayout.PropertyField(_rangeStart, GUIContent.none, GUILayout.Width(80));
			EditorGUILayout.EndHorizontal();

			// Custom end
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.PropertyField(_useCustomEnd, new GUIContent("Custom End"));
			using (new EditorGUI.DisabledScope(!_useCustomEnd.boolValue))
				EditorGUILayout.PropertyField(_rangeEnd, GUIContent.none, GUILayout.Width(80));
			EditorGUILayout.EndHorizontal();

			// Clamp values
			if (_rangeStart.intValue < 0) _rangeStart.intValue = 0;
			if (_rangeEnd.intValue < 0) _rangeEnd.intValue = 0;
			if (maxFrame > 0)
			{
				if (_rangeStart.intValue > maxFrame) _rangeStart.intValue = maxFrame;
				if (_rangeEnd.intValue > maxFrame) _rangeEnd.intValue = maxFrame;
			}
			if (_useCustomStart.boolValue && _useCustomEnd.boolValue &&
			    _rangeEnd.intValue < _rangeStart.intValue)
				_rangeEnd.intValue = _rangeStart.intValue;

			// Info
			if (feeder.FrameCount > 0 && (_useCustomStart.boolValue || _useCustomEnd.boolValue))
			{
				int effStart = _useCustomStart.boolValue ? _rangeStart.intValue : 0;
				int effEnd = _useCustomEnd.boolValue ? _rangeEnd.intValue : maxFrame;
				int rangeCount = effEnd - effStart + 1;
				EditorGUILayout.HelpBox(
					$"Playing frames {effStart} to {effEnd} ({rangeCount} frames)",
					MessageType.Info);
			}

			// Runtime controls (play mode only)
			if (Application.isPlaying)
			{
				EditorGUILayout.Space(8);
				EditorGUILayout.LabelField("Playback Controls", EditorStyles.boldLabel);

				// Play/Pause/Step buttons
				EditorGUILayout.BeginHorizontal();

				if (feeder.IsPlaying)
				{
					if (GUILayout.Button("Pause"))
						feeder.Pause();
				}
				else
				{
					if (GUILayout.Button("Play"))
						feeder.Play();
				}

				if (GUILayout.Button("|<", GUILayout.Width(30)))
					feeder.Seek(feeder.EffectiveStartFrame);
				if (GUILayout.Button("<", GUILayout.Width(30)))
					feeder.Step(-1);
				if (GUILayout.Button(">", GUILayout.Width(30)))
					feeder.Step(1);

				EditorGUILayout.EndHorizontal();

				// Seek slider
				int frameCount = feeder.FrameCount;
				if (frameCount > 0)
				{
					int sliderMin = feeder.EffectiveStartFrame;
					int sliderMax = feeder.EffectiveEndFrame;
					int newFrame = EditorGUILayout.IntSlider("Frame",
						feeder.CurrentFrame, sliderMin, sliderMax);
					if (newFrame != feeder.CurrentFrame)
						feeder.Seek(newFrame);
				}

				// Status
				EditorGUILayout.Space(4);
				using (new EditorGUI.DisabledScope(true))
				{
					EditorGUILayout.Toggle("Playing", feeder.IsPlaying);
					EditorGUILayout.IntField("Current Frame", feeder.CurrentFrame);
					EditorGUILayout.IntField("Total Frames", feeder.FrameCount);
				}

				Repaint();
			}

			serializedObject.ApplyModifiedProperties();
		}
	}
}
