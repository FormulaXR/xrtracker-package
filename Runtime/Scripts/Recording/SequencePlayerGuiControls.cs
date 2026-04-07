using UnityEngine;

namespace IV.FormulaTracker.Recording
{
	/// <summary>
	/// On-screen GUI controls for SequencePlayerFeeder.
	/// Renders play/pause, step, seek slider, and frame info using IMGUI.
	/// Attach to the same GameObject as SequencePlayerFeeder.
	/// </summary>
	[RequireComponent(typeof(SequencePlayerFeeder))]
	public class SequencePlayerGuiControls : MonoBehaviour
	{
#if UNITY_EDITOR
		[Header("Layout")]
		[SerializeField] private Anchor _anchor = Anchor.BottomLeft;
		[SerializeField] private Vector2 _offset = new Vector2(10, 10);
		[SerializeField] private float _panelWidth = 320f;

		[Header("Appearance")]
		[SerializeField] private int _fontSize = 14;

		private SequencePlayerFeeder _feeder;
		private GUIStyle _boxStyle;
		private GUIStyle _buttonStyle;
		private GUIStyle _labelStyle;
		private bool _stylesInitialized;

		public enum Anchor { TopLeft, TopRight, BottomLeft, BottomRight }

		private void Awake()
		{
			_feeder = GetComponent<SequencePlayerFeeder>();
		}

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
				alignment = TextAnchor.MiddleCenter
			};

			_stylesInitialized = true;
		}

		private void OnGUI()
		{
			if (_feeder == null || _feeder.FrameCount == 0) return;

			InitStyles();

			float panelHeight = 90f;
			Rect panelRect = GetPanelRect(panelHeight);

			GUILayout.BeginArea(panelRect, _boxStyle);
			GUILayout.BeginVertical();

			// Row 1: Transport controls
			GUILayout.BeginHorizontal();

			if (GUILayout.Button(_feeder.IsPlaying ? "||" : ">", _buttonStyle, GUILayout.Width(40)))
			{
				if (_feeder.IsPlaying) _feeder.Pause();
				else _feeder.Play();
			}

			if (GUILayout.Button("|<", _buttonStyle, GUILayout.Width(35)))
				_feeder.Seek(0);

			if (GUILayout.Button("<", _buttonStyle, GUILayout.Width(35)))
				_feeder.Step(-1);

			if (GUILayout.Button(">", _buttonStyle, GUILayout.Width(35)))
				_feeder.Step(1);

			GUILayout.FlexibleSpace();

			GUILayout.Label($"{_feeder.CurrentFrame} / {_feeder.FrameCount - 1}", _labelStyle);

			GUILayout.EndHorizontal();

			// Row 2: Seek slider
			int newFrame = (int)GUILayout.HorizontalSlider(
				_feeder.CurrentFrame, 0, _feeder.FrameCount - 1);
			if (newFrame != _feeder.CurrentFrame)
				_feeder.Seek(newFrame);

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
#endif
	}
}
