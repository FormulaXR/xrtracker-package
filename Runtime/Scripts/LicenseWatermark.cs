using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Displays the license watermark for the Developer tier and for any expired license.
	/// A license of a retired tier shows a short notice that the tier is no longer supported.
	/// Uses OnGUI for render-pipeline independence (Built-in, URP, HDRP all work).
	/// Sends a heartbeat to the native plugin each frame, and tracking degrades when this component is removed.
	/// </summary>
	[AddComponentMenu("")] // Hidden from Add Component menu
	[DefaultExecutionOrder(1000)]
	public class LicenseWatermark : MonoBehaviour
	{
		// Developer: fixed bottom-left corner, always visible.
		// Any other watermark moves to a random position every few seconds.
		private const float PADDING = 16f;
		private const float MARGIN = 20f;

		private GUIStyle _style;
		private Vector2 _position;
		private float _nextRepositionTime;
		private float _repositionInterval;
		private bool _frozen;
		private bool _expired;
		private LicenseTier _tier;

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
#pragma warning disable CS0618 // The frozen state belongs to the retired tiers, which the plugin can still report.
			_frozen = manager.IsLicenseFrozen;
#pragma warning restore CS0618
			_expired = manager.LicenseStatus == LicenseStatus.Expired;

			// Developer: fixed corner, no reposition.
			if (_tier != LicenseTier.Developer)
			{
				_repositionInterval = 3f;
				if (!_frozen && !_expired && Time.unscaledTime >= _nextRepositionTime)
				{
					PickRandomPosition();
					_nextRepositionTime = Time.unscaledTime + _repositionInterval;
				}
			}
		}

		private void OnGUI()
		{
			if (_style == null)
				CreateStyle();

			var manager = XRTrackerManager.Instance;
			if (manager == null) return;

			// Only show for the Developer tier, a retired tier, or any expired license
			bool retired = IsRetiredTier(_tier);
			if (!_expired && !retired && _tier != LicenseTier.Developer)
				return;

			string text;
			Color textColor;
			int fontSize;

			if (_expired)
			{
				text = $"XRTracker — {GetLicenseName(_tier)} Expired";
				textColor = new Color(1f, 0.3f, 0.3f, 0.9f);
				fontSize = 18;
			}
			else if (retired && _frozen)
			{
				text = "XRTracker — Tracking paused\nThis license tier is no longer supported. Restart the app to reset.";
				textColor = new Color(1f, 0.3f, 0.3f, 0.9f);
				fontSize = 18;
			}
			else if (retired)
			{
				text = "XRTracker — This license tier is no longer supported";
				textColor = new Color(1f, 1f, 1f, 0.7f);
				fontSize = 16;
			}
			else // Developer — persistent fixed-corner watermark
			{
				text = "XRTracker — Developer License";
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

		private static bool IsRetiredTier(LicenseTier tier)
		{
#pragma warning disable CS0618 // The Free and Trial tiers are retired, but a license file can still name them.
			return tier == LicenseTier.Free || tier == LicenseTier.Trial;
#pragma warning restore CS0618
		}

		private static string GetLicenseName(LicenseTier tier)
		{
			switch (tier)
			{
				case LicenseTier.Developer: return "Developer License";
				case LicenseTier.Commercial: return "Commercial License";
				default: return "License";
			}
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
				Debug.Log($"[XRTracker] Machine ID copied to the clipboard ({machineId}).");
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
