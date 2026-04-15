#if HAS_TMP
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

namespace IV.FormulaTracker
{
	public class TrackingDiagnosticsPanel : MonoBehaviour
	{
		[SerializeField] private TMP_Text _fpsText;
		[SerializeField] private TMP_Text _bodiesText;

		private readonly List<TrackedBody> _bodies = new();
		private readonly StringBuilder _sb = new();
		private string _lastBodiesText = "";
		private string _lastFpsText = "";

		private void Start()
		{
			if (_bodiesText != null)
				_bodiesText.SetText("");

			var manager = XRTrackerManager.Instance;
			if (manager == null) return;

			foreach (var body in manager.TrackedBodies)
				_bodies.Add(body);

			manager.OnBodyRegistered += OnBodyRegistered;
			manager.OnBodyUnregistered += OnBodyUnregistered;
		}

		private void OnDestroy()
		{
			var manager = XRTrackerManager.Instance;
			if (manager == null) return;

			manager.OnBodyRegistered -= OnBodyRegistered;
			manager.OnBodyUnregistered -= OnBodyUnregistered;
		}

		private void OnBodyRegistered(TrackedBody body)
		{
			if (!_bodies.Contains(body))
				_bodies.Add(body);
		}

		private void OnBodyUnregistered(TrackedBody body)
		{
			_bodies.Remove(body);
		}

		private void Update()
		{
			UpdateFps();
			UpdateBodies();
		}

		private void UpdateFps()
		{
			if (_fpsText == null) return;

			int fps = Mathf.RoundToInt(1f / Time.unscaledDeltaTime);
			string fpsColor = fps >= 30 ? "#4CAF50" : fps >= 15 ? "#FFC107" : "#F44336";
			string text = $"<color={fpsColor}>{fps}</color> FPS";

			if (text != _lastFpsText)
			{
				_lastFpsText = text;
				_fpsText.SetText(text);
			}
		}

		private void UpdateBodies()
		{
			if (_bodiesText == null) return;

			_sb.Clear();

			for (int i = 0; i < _bodies.Count; i++)
			{
				var body = _bodies[i];
				if (body == null) continue;

				if (_sb.Length > 0)
					_sb.Append('\n');

				float quality = body.TrackingQuality;
				string color = body.TrackingStatus == TrackingStatus.Tracking
					? quality >= 0.5f ? "#4CAF50" : "#FFC107"
					: "#F44336";

				_sb.Append(body.BodyId);
				_sb.Append("  <color=");
				_sb.Append(color);
				_sb.Append('>');
				_sb.Append(quality.ToString("F2"));
				_sb.Append("</color>");
			}

			string text = _sb.ToString();
			if (text != _lastBodiesText)
			{
				_lastBodiesText = text;
				_bodiesText.SetText(text);
			}
		}
	}
}
#endif
