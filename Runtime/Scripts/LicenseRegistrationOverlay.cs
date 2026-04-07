using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Full-screen blocking overlay shown when no license is present.
	/// Prompts the user to register for a free Developer license.
	/// No heartbeat is sent — tracking is already blocked by CanUpdatePose().
	/// </summary>
	[AddComponentMenu("")] // Hidden from Add Component menu
	[DefaultExecutionOrder(1000)]
	public class LicenseRegistrationOverlay : MonoBehaviour
	{
		private GUIStyle _titleStyle;
		private GUIStyle _bodyStyle;
		private GUIStyle _buttonStyle;
		private float _copiedMessageTimer;
		private const float COPIED_MESSAGE_DURATION = 2f;

		private void OnEnable()
		{
			hideFlags = HideFlags.HideInInspector | HideFlags.DontSaveInEditor;
		}

		private void OnGUI()
		{
			if (_titleStyle == null)
				CreateStyles();

			// Semi-transparent background
			GUI.color = new Color(0f, 0f, 0f, 0.75f);
			GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
			GUI.color = Color.white;

			float centerX = Screen.width * 0.5f;
			float centerY = Screen.height * 0.5f;

			// Title
			var titleContent = new GUIContent("XRTracker \u2014 No License");
			var titleSize = _titleStyle.CalcSize(titleContent);
			GUI.Label(new Rect(centerX - titleSize.x * 0.5f, centerY - 60, titleSize.x, titleSize.y),
				titleContent, _titleStyle);

			// Body
			string body = "Register for a free Developer license:\nTools > XRTracker > License Registration";
			var bodyContent = new GUIContent(body);
			float bodyWidth = 500f;
			float bodyHeight = _bodyStyle.CalcHeight(bodyContent, bodyWidth);
			GUI.Label(new Rect(centerX - bodyWidth * 0.5f, centerY - 10, bodyWidth, bodyHeight),
				bodyContent, _bodyStyle);

			// Machine ID
			string machineId = SystemInfo.deviceUniqueIdentifier;
			string machineIdText = $"Machine ID: {machineId}";
			var machineIdContent = new GUIContent(machineIdText);
			var machineIdSize = _bodyStyle.CalcSize(machineIdContent);
			GUI.Label(new Rect(centerX - machineIdSize.x * 0.5f, centerY + 50, machineIdSize.x, machineIdSize.y),
				machineIdContent, _bodyStyle);

			// Copy button
			if (_copiedMessageTimer > 0)
				_copiedMessageTimer -= Time.unscaledDeltaTime;

			string btnLabel = _copiedMessageTimer > 0 ? "Copied!" : "Copy Machine ID";
			var btnContent = new GUIContent(btnLabel);
			var btnSize = _buttonStyle.CalcSize(btnContent);
			if (GUI.Button(new Rect(centerX - btnSize.x * 0.5f, centerY + 85, btnSize.x, btnSize.y),
				btnContent, _buttonStyle))
			{
				GUIUtility.systemCopyBuffer = machineId;
				_copiedMessageTimer = COPIED_MESSAGE_DURATION;
				Debug.Log($"[XRTracker] Machine ID copied to clipboard: {machineId}");
			}
		}

		private void CreateStyles()
		{
			_titleStyle = new GUIStyle(GUI.skin.label)
			{
				fontSize = 24,
				fontStyle = FontStyle.Bold,
				alignment = TextAnchor.MiddleCenter,
				normal = { textColor = new Color(1f, 0.85f, 0.4f, 1f) }
			};

			_bodyStyle = new GUIStyle(GUI.skin.label)
			{
				fontSize = 16,
				alignment = TextAnchor.MiddleCenter,
				wordWrap = true,
				normal = { textColor = new Color(1f, 1f, 1f, 0.9f) }
			};

			_buttonStyle = new GUIStyle(GUI.skin.button)
			{
				fontSize = 14,
				padding = new RectOffset(12, 12, 6, 6)
			};
		}
	}
}
