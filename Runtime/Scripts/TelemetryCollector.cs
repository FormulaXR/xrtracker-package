using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace IV.FormulaTracker
{
	internal class TelemetryCollector : MonoBehaviour
	{
		private const string METRICS_URL = "https://api.xrtracker.net/metrics";
		private const float HEARTBEAT_INTERVAL = 300f; // 5 minutes
		private const string VERSION = "1.0.0";

		private string _sessionId;
		private string _licenseId;

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
		private static void AutoStart()
		{
			var go = new GameObject("[XRTracker Telemetry]");
			go.hideFlags = HideFlags.HideAndDontSave;
			DontDestroyOnLoad(go);
			go.AddComponent<TelemetryCollector>();
		}

		private IEnumerator Start()
		{
			// Wait for XRTrackerManager to be available and initialized
			XRTrackerManager manager = null;
			float waited = 0f;
			while (waited < 30f)
			{
				manager = XRTrackerManager.Instance;
				if (manager != null && manager.IsInitialized)
					break;
				yield return new WaitForSeconds(1f);
				waited += 1f;
			}

			if (manager == null || !manager.IsInitialized)
			{
				Destroy(gameObject);
				yield break;
			}

			_sessionId = Guid.NewGuid().ToString("N").Substring(0, 16);
			_licenseId = ExtractLicenseId(manager);

			SendHeartbeat();

			while (true)
			{
				yield return new WaitForSeconds(HEARTBEAT_INTERVAL);
				SendHeartbeat();
			}
		}

		private void SendHeartbeat()
		{
			var manager = XRTrackerManager.Instance;
			if (manager == null || !manager.IsInitialized) return;

			var payload = new MetricsPayload
			{
				platform = Application.platform.ToString(),
				license_tier = manager.LicenseTier.ToString(),
				unity_version = Application.unityVersion,
				xrtracker_version = VERSION,
				tracking_mode = GetTrackingMode(manager),
				device_model = SystemInfo.deviceModel,
				body_count = manager.TrackedBodies?.Count ?? 0,
				session_id = _sessionId,
				machine_id = SystemInfo.deviceUniqueIdentifier,
				license_id = _licenseId,
				app_id = Application.identifier,
				is_editor = Application.isEditor,
				avg_quality = GetAvgQuality(manager),
				client_utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
			};

			var json = JsonUtility.ToJson(payload);
			StartCoroutine(PostMetrics(json));
		}

		private static IEnumerator PostMetrics(string json)
		{
			using var request = new UnityWebRequest(METRICS_URL, "POST");
			request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
			request.downloadHandler = new DownloadHandlerBuffer();
			request.SetRequestHeader("Content-Type", "application/json");
			request.timeout = 10;
			yield return request.SendWebRequest();
		}

		private static string GetTrackingMode(XRTrackerManager manager)
		{
			if (manager.TrackedBodies == null || manager.TrackedBodies.Count == 0)
				return "none";

			foreach (var body in manager.TrackedBodies)
			{
				if (body == null) continue;
				return body.TrackingMethod.ToString();
			}
			return "none";
		}

		private static float GetAvgQuality(XRTrackerManager manager)
		{
			if (manager.TrackedBodies == null || manager.TrackedBodies.Count == 0)
				return 0f;

			float sum = 0f;
			int count = 0;
			foreach (var body in manager.TrackedBodies)
			{
				if (body == null) continue;
				sum += body.TrackingQuality;
				count++;
			}
			return count > 0 ? sum / count : 0f;
		}

		private static string ExtractLicenseId(XRTrackerManager manager)
		{
			try
			{
				var field = typeof(XRTrackerManager).GetField("_embeddedLicense",
					System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
				if (field == null) return null;

				var textAsset = field.GetValue(manager) as TextAsset;
				if (textAsset == null || string.IsNullOrEmpty(textAsset.text)) return null;

				var outer = JsonUtility.FromJson<SignedLicense>(textAsset.text);
				if (outer == null || string.IsNullOrEmpty(outer.payload)) return null;

				var payloadJson = Encoding.UTF8.GetString(Convert.FromBase64String(outer.payload));
				var payload = JsonUtility.FromJson<LicensePayloadData>(payloadJson);
				return payload?.licenseId;
			}
			catch
			{
				return null;
			}
		}

		[Serializable]
		private struct MetricsPayload
		{
			public string platform;
			public string license_tier;
			public string unity_version;
			public string xrtracker_version;
			public string tracking_mode;
			public string device_model;
			public int body_count;
			public string session_id;
			public string machine_id;
			public string license_id;
			public string app_id;
			public bool is_editor;
			public float avg_quality;
			public string client_utc;
		}

		[Serializable]
		private class SignedLicense
		{
			public string payload;
		}

		[Serializable]
		private class LicensePayloadData
		{
			public string licenseId;
		}
	}
}
