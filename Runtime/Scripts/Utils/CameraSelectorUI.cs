using UnityEngine;

namespace IV.FormulaTracker
{
	public class CameraSelectorUI : MonoBehaviour
	{
		#region Private Fields

		private FTCameraDevice[] _cameras;
		private bool _hasSelected;

		#endregion

		#region Unity Lifecycle

		private void Start()
		{
			var manager = XRTrackerManager.Instance;
			if (manager != null)
			{
				manager.OnCamerasEnumerated += OnCamerasEnumerated;

				if (manager.AvailableCameras != null && manager.AvailableCameras.Length > 0)
				{
					_cameras = manager.AvailableCameras;
				}
			}
		}

		private void OnDestroy()
		{
			var manager = XRTrackerManager.Instance;
			if (manager != null)
			{
				manager.OnCamerasEnumerated -= OnCamerasEnumerated;
			}
		}

		private void OnGUI()
		{
			if (_hasSelected || _cameras == null || _cameras.Length == 0) return;

			float boxWidth = 400;
			float boxHeight = 60 + _cameras.Length * 40;
			float x = (Screen.width - boxWidth) / 2;
			float y = (Screen.height - boxHeight) / 2;

			GUI.Box(new Rect(x - 10, y - 10, boxWidth + 20, boxHeight + 20), "");

			GUILayout.BeginArea(new Rect(x, y, boxWidth, boxHeight));

			GUILayout.Label("Select Camera", new GUIStyle(GUI.skin.label)
			{
				fontSize = 18,
				fontStyle = FontStyle.Bold,
				alignment = TextAnchor.MiddleCenter
			});

			GUILayout.Space(10);

			for (int i = 0; i < _cameras.Length; i++)
			{
				var cam = _cameras[i];

				if (GUILayout.Button(cam.name, GUILayout.Height(35)))
				{
					SelectCamera(i);
				}
			}

			GUILayout.EndArea();
		}

		#endregion

		#region Private Methods

		private void OnCamerasEnumerated(FTCameraDevice[] cameras)
		{
			_cameras = cameras;
		}

		private async void SelectCamera(int index)
		{
			_hasSelected = true;

			var manager = XRTrackerManager.Instance;
			if (manager != null)
			{
				await manager.SelectCameraAsync(index);
			}
		}

		#endregion
	}
}
