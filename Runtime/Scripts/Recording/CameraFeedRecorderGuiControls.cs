using System;
using UnityEngine;

namespace IV.FormulaTracker.Recording
{
	/// <summary>
	/// On-screen GUI controls for live recording via C++ SequenceRecorder.
	/// Renders start/stop recording button and status info using IMGUI.
	/// Recording is handled entirely in C++ — FT_TrackStep auto-captures from cameras.
	/// </summary>
	public class CameraFeedRecorderGuiControls : MonoBehaviour
	{
		[Header("Layout")]
		[SerializeField] private Anchor _anchor = Anchor.TopLeft;
		[SerializeField] private Vector2 _offset = new Vector2(10, 10);
		[SerializeField] private float _panelWidth = 280f;

		[Header("Appearance")]
		[SerializeField] private int _fontSize = 14;

		private GUIStyle _boxStyle;
		private GUIStyle _buttonStyle;
		private GUIStyle _labelStyle;
		private GUIStyle _recLabelStyle;
		private bool _stylesInitialized;
		private string _currentOutputDir;

		public enum Anchor { TopLeft, TopRight, BottomLeft, BottomRight }

		private bool IsRecording => FTBridge.FT_IsRecording() != 0;

		private void InitStyles()
		{
			if (_stylesInitialized) return;

			_boxStyle = new GUIStyle(GUI.skin.box)
			{
				fontSize = _fontSize
			};

			_buttonStyle = new GUIStyle(GUI.skin.button)
			{
				fontSize = _fontSize
			};

			_labelStyle = new GUIStyle(GUI.skin.label)
			{
				fontSize = _fontSize,
				alignment = TextAnchor.MiddleLeft
			};

			_recLabelStyle = new GUIStyle(GUI.skin.label)
			{
				fontSize = _fontSize,
				alignment = TextAnchor.MiddleCenter,
				normal = { textColor = Color.red },
				fontStyle = FontStyle.Bold
			};

			_stylesInitialized = true;
		}

		private void OnGUI()
		{
			InitStyles();

			bool recording = IsRecording;
			float panelHeight = recording ? 70f : 40f;
			Rect panelRect = GetPanelRect(panelHeight);

			GUILayout.BeginArea(panelRect, _boxStyle);
			GUILayout.BeginVertical();

			GUILayout.BeginHorizontal();

			if (recording)
			{
				// Blinking REC indicator
				if ((int)(Time.unscaledTime * 2) % 2 == 0)
					GUILayout.Label("REC", _recLabelStyle, GUILayout.Width(40));
				else
					GUILayout.Space(40);

				if (GUILayout.Button("Stop Recording", _buttonStyle))
				{
					int count = FTBridge.FT_StopRecording();
					Debug.Log($"[Recording] Stopped. {count} frames saved to: {_currentOutputDir}");
				}

				GUILayout.FlexibleSpace();
				GUILayout.Label($"{FTBridge.FT_GetRecordedFrameCount()} frames", _labelStyle);
			}
			else
			{
				if (GUILayout.Button("Start Recording", _buttonStyle))
				{
					string baseDir = System.IO.Path.Combine(Application.persistentDataPath, "Recordings");
					_currentOutputDir = System.IO.Path.Combine(baseDir, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
					FTBridge.FT_StartRecording(_currentOutputDir);
					Debug.Log($"[Recording] Started: {_currentOutputDir}");
				}
			}

			GUILayout.EndHorizontal();

			if (recording && !string.IsNullOrEmpty(_currentOutputDir))
			{
				GUILayout.Label(_currentOutputDir, _labelStyle);
			}

			GUILayout.EndVertical();
			GUILayout.EndArea();
		}

		private Rect GetPanelRect(float panelHeight)
		{
			float x, y;

			switch (_anchor)
			{
				case Anchor.TopLeft:
					x = _offset.x;
					y = _offset.y;
					break;
				case Anchor.TopRight:
					x = Screen.width - _panelWidth - _offset.x;
					y = _offset.y;
					break;
				case Anchor.BottomRight:
					x = Screen.width - _panelWidth - _offset.x;
					y = Screen.height - panelHeight - _offset.y;
					break;
				case Anchor.BottomLeft:
				default:
					x = _offset.x;
					y = Screen.height - panelHeight - _offset.y;
					break;
			}

			return new Rect(x, y, _panelWidth, panelHeight);
		}
	}
}
