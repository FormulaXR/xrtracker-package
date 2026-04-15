using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Displays a license watermark overlay for Free, Trial, and Developer tiers.
	/// Uses OnGUI for render-pipeline independence (Built-in, URP, HDRP all work).
	/// Sends a heartbeat to native each frame — removing this component degrades tracking.
	/// </summary>
	[AddComponentMenu("")] // Hidden from Add Component menu
	[DefaultExecutionOrder(1000)]
	public class LicenseWatermark : MonoBehaviour
	{
		// Free/Trial: repositions every 3s, always visible (legacy, kept for future reuse)
		// Developer: fixed bottom-left corner, always visible, no fade
		private const float PADDING = 16f;
		private const float MARGIN = 20f;

		private GUIStyle _style;
		private Vector2 _position;
		private float _nextRepositionTime;
		private float _repositionInterval;
		private bool _frozen;
		private bool _expired;
		private LicenseTier _tier;

		// Developer tier fade
		private float _fadeAlpha;
		private float _fadeTimer;
		private const float DEV_VISIBLE_DURATION = 2f;
		private const float DEV_HIDDEN_DURATION = 8f;
		private const float DEV_FADE_SPEED = 3f;

		// Copy Machine ID
		private GUIStyle _buttonStyle;
		private float _copiedMessageTimer;
		private const float COPIED_MESSAGE_DURATION = 2f;

		private void OnEnable()
		{
			hideFlags = HideFlags.HideInInspector | HideFlags.DontSaveInEditor;
			PickRandomPosition();
		}

		private void Update()
		{
			FTBridge.FT_WatermarkHeartbeat();

			var manager = XRTrackerManager.Instance;
			if (manager == null) return;

			_tier = manager.LicenseTier;
			_frozen = manager.IsLicenseFrozen;
			_expired = manager.LicenseStatus == LicenseStatus.Expired;


			// Developer: fixed corner, no reposition, no fade
			// Free/Trial (legacy): repositions every 3s
			if (_tier != LicenseTier.Developer)
			{
				_repositionInterval = 3f;
				if (!_frozen && !_expired && Time.unscaledTime >= _nextRepositionTime)
				{
					PickRandomPosition();
					_nextRepositionTime = Time.unscaledTime + _repositionInterval;
				}
			}

			_fadeAlpha = 1f;
		}

		private void OnGUI()
		{
			if (_style == null)
				CreateStyle();

			var manager = XRTrackerManager.Instance;
			if (manager == null) return;

			// Only show for Free, Trial, Developer, or any expired license
			if (!_expired &&
			    _tier != LicenseTier.Free && _tier != LicenseTier.Trial && _tier != LicenseTier.Developer)
				return;

			string text;
			Color textColor;
			int fontSize;

			if (_expired)
			{
				string tierName = _tier == LicenseTier.Trial ? "Trial" :
				                  _tier == LicenseTier.Developer ? "Developer" :
				                  _tier == LicenseTier.Commercial ? "Commercial" : "License";
				text = $"XRTracker \u2014 {tierName} License Expired";
				textColor = new Color(1f, 0.3f, 0.3f, 0.9f);
				fontSize = 18;
			}
			else if (_frozen)
			{
				text = "XRTracker \u2014 Free License\nTracking paused \u2014 restart app/Unity to reset\nContact support@formulaxr.com for a 15-day unlimited trial";
				textColor = new Color(1f, 0.3f, 0.3f, 0.9f);
				fontSize = 18;
			}
			else if (_tier == LicenseTier.Free)
			{
				float remaining = manager.FreeSecondsRemaining;
				text = FormatTimeText("Free License", remaining);
				textColor = new Color(1f, 1f, 1f, 0.85f);
				fontSize = 18;
			}
			else if (_tier == LicenseTier.Trial)
			{
				text = "XRTracker \u2014 Trial License";
				textColor = new Color(1f, 1f, 1f, 0.7f);
				fontSize = 16;
			}
			else // Developer — persistent fixed-corner watermark
			{
				text = "XRTracker \u2014 Developer License";
				textColor = new Color(1f, 1f, 1f, 0.55f);
				fontSize = 14;
			}

			_style.fontSize = fontSize;

			// Auto-size rect from text content
			var content = new GUIContent(text);
			_style.wordWrap = text.Contains("\n");
			Vector2 size;
			if (_style.wordWrap)
			{
				size.x = _style.CalcSize(new GUIContent(text.Split('\n')[0])).x + PADDING;
				size.y = _style.CalcHeight(content, size.x) + PADDING * 0.5f;
			}
			else
			{
				size = _style.CalcSize(content);
				size.x += PADDING;
				size.y += PADDING * 0.5f;
			}

			Rect rect;
			if (_tier == LicenseTier.Developer && !_expired)
			{
				// Fixed bottom-left corner (like Unity's "DEVELOPMENT BUILD")
				rect = new Rect(MARGIN, Screen.height - size.y - MARGIN, size.x, size.y);
			}
			else
			{
				rect = new Rect(_position.x, _position.y, size.x, size.y);
				// Clamp to screen
				rect.x = Mathf.Clamp(rect.x, 0, Mathf.Max(0, Screen.width - rect.width));
				rect.y = Mathf.Clamp(rect.y, 0, Mathf.Max(0, Screen.height - rect.height));
			}

			// Shadow
			var shadowRect = new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height);
			_style.normal.textColor = new Color(0f, 0f, 0f, textColor.a * 0.6f);
			GUI.Label(shadowRect, text, _style);

			// Text
			_style.normal.textColor = textColor;
			GUI.Label(rect, text, _style);

			// Copy Machine ID button (bottom-right corner)
			DrawMachineIdButton();
		}

		private static string FormatTimeText(string tierName, float remaining)
		{
			int mins = Mathf.FloorToInt(remaining / 60f);
			int secs = Mathf.FloorToInt(remaining % 60f);
			return mins > 0
				? $"XRTracker \u2014 {tierName} \u2014 {mins}:{secs:D2} remaining"
				: $"XRTracker \u2014 {tierName} \u2014 {secs}s remaining";
		}

		private void DrawMachineIdButton()
		{
			float scale = Mathf.Max(1f, Screen.dpi / 160f);
			int btnFontSize = Mathf.RoundToInt(14 * scale);
			int pad = Mathf.RoundToInt(10 * scale);

			if (_buttonStyle == null)
				_buttonStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Normal };

			_buttonStyle.fontSize = btnFontSize;
			_buttonStyle.padding = new RectOffset(pad, pad, pad / 2, pad / 2);

			bool showCopied = _copiedMessageTimer > 0;
			if (showCopied)
				_copiedMessageTimer -= Time.unscaledDeltaTime;

			string label = showCopied ? "Machine ID Copied!" : "Copy Machine ID";
			var size = _buttonStyle.CalcSize(new GUIContent(label));
			var rect = new Rect(Screen.width - size.x - 10, Screen.height - size.y - 10, size.x, size.y);

			if (GUI.Button(rect, label, _buttonStyle))
			{
				string machineId = SystemInfo.deviceUniqueIdentifier;
				GUIUtility.systemCopyBuffer = machineId;
				_copiedMessageTimer = COPIED_MESSAGE_DURATION;
				Debug.Log($"[XRTracker] Machine ID copied to clipboard: {machineId}");
			}
		}

		private void CreateStyle()
		{
			_style = new GUIStyle(GUI.skin.label)
			{
				fontSize = 18,
				fontStyle = FontStyle.Bold,
				alignment = TextAnchor.MiddleCenter,
				wordWrap = false
			};
		}

		private void PickRandomPosition()
		{
			// Pick a random anchor point; clamping to screen happens at draw time
			float x = Random.Range(MARGIN, Mathf.Max(MARGIN, Screen.width * 0.7f));
			float y = Random.Range(MARGIN, Mathf.Max(MARGIN, Screen.height * 0.85f));
			_position = new Vector2(x, y);
			_nextRepositionTime = Time.unscaledTime + _repositionInterval;
		}
	}
}
